using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>A janela "Comandos globais" (ADR-028).</summary>
public class DevelopmentCommandsViewModelTests
{
    private static readonly DateTimeOffset At = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private static readonly IReadOnlyList<DevelopmentCommandRow> Rows =
    [
        new(Guid.CreateVersion7(), "@build", "dotnet build", "Compila o projeto", At),
        new(Guid.CreateVersion7(), "@docker-up", "docker compose up -d", "Inicia containers", At),
        new(Guid.CreateVersion7(), "@restore", "dotnet restore", null, At),
        new(Guid.CreateVersion7(), "@eco-sync", "eco-sync {nomebanco} -Dev", null, At),
    ];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();
    private readonly FakeConfirmationDialog _confirmation = new();

    private async Task<DevelopmentCommandsViewModel> LoadedAsync()
    {
        _runner.ResultsByHandler[typeof(GetDevelopmentCommandsHandler)] = Rows;

        var viewModel = new DevelopmentCommandsViewModel(
            _runner, _confirmation, NullLogger<DevelopmentCommandsViewModel>.Instance);
        await viewModel.LoadAsync(Ct);

        return viewModel;
    }

    [Fact]
    public async Task Load_ListsTheCommands()
    {
        var viewModel = await LoadedAsync();

        viewModel.Commands.Select(item => item.Alias).Should().Equal("@build", "@docker-up", "@restore", "@eco-sync");
        viewModel.IsEmpty.Should().BeFalse();
    }

    [Theory]
    [InlineData("docker", "@docker-up")]
    [InlineData("RESTORE", "@restore")]
    [InlineData("compila", "@build")]
    public async Task Search_FiltersByAliasCommandOrDescription(string query, string expected)
    {
        var viewModel = await LoadedAsync();

        viewModel.Search = query;

        viewModel.Commands.Select(item => item.Alias).Should().Equal(expected);
    }

    [Fact]
    public async Task Save_WithoutAnAliasOrCommand_IsNotPossible()
    {
        var viewModel = await LoadedAsync();

        viewModel.Alias = "@x";

        viewModel.SaveCommand.CanExecute(null).Should().BeFalse();
    }

    [Fact]
    public async Task Save_CreatesThenReloads()
    {
        var viewModel = await LoadedAsync();
        _runner.ResultsByHandler[typeof(CreateDevelopmentCommandHandler)] = Rows[0];
        viewModel.Alias = "@test";
        viewModel.Command = "dotnet test";

        await viewModel.SaveAsync(Ct);

        _runner.Invoked.Should().ContainInOrder(
            typeof(CreateDevelopmentCommandHandler),
            typeof(GetDevelopmentCommandsHandler));
        viewModel.StatusMessage.Should().Be("Comando criado.");
        viewModel.Alias.Should().BeEmpty();
    }

    [Fact]
    public async Task Edit_ThenSave_Updates()
    {
        var viewModel = await LoadedAsync();
        _runner.ResultsByHandler[typeof(UpdateDevelopmentCommandHandler)] = Rows[0];

        viewModel.Edit(viewModel.Commands[0]);
        viewModel.IsEditing.Should().BeTrue();
        viewModel.Command.Should().Be("dotnet build");
        viewModel.Command = "dotnet build -c Release";
        await viewModel.SaveAsync(Ct);

        _runner.Invoked.Should().Contain(typeof(UpdateDevelopmentCommandHandler));
        viewModel.IsEditing.Should().BeFalse();
    }

    [Fact]
    public async Task ADuplicateAlias_ShowsWhy_AndKeepsTheForm()
    {
        var viewModel = await LoadedAsync();
        _runner.FailuresByHandler[typeof(CreateDevelopmentCommandHandler)] =
            new DomainException("Já existe um comando chamado \"@build\".");
        viewModel.Alias = "@build";
        viewModel.Command = "make";

        await viewModel.SaveAsync(Ct);

        viewModel.ErrorMessage.Should().Be("Já existe um comando chamado \"@build\".");
        viewModel.Alias.Should().Be("@build");
    }

    [Fact]
    public async Task Delete_AsksFirst_AndDoesNothingWhenRefused()
    {
        var viewModel = await LoadedAsync();
        _confirmation.Answer = false;

        await viewModel.DeleteAsync(viewModel.Commands[0], Ct);

        _confirmation.LastAsked!.Headline.Should().Contain("@build");
        _runner.Invoked.Should().NotContain(typeof(DeleteDevelopmentCommandHandler));
    }

    [Fact]
    public async Task Delete_WhenConfirmed_Deletes()
    {
        var viewModel = await LoadedAsync();
        _confirmation.Answer = true;

        await viewModel.DeleteAsync(viewModel.Commands[0], Ct);

        _runner.Invoked.Should().Contain(typeof(DeleteDevelopmentCommandHandler));
        viewModel.StatusMessage.Should().Be("@build excluído.");
    }

    [Fact]
    public async Task Test_WithoutAFolder_RunsNothing()
    {
        var viewModel = await LoadedAsync();

        await viewModel.TestAsync(viewModel.Commands[0]);

        viewModel.ErrorMessage.Should().Contain("pasta");
        _runner.Invoked.Should().NotContain(typeof(RunCommandHandler));
    }

    [Fact]
    public async Task Test_RunsInTheChosenFolder_AndShowsTheResult()
    {
        var viewModel = await LoadedAsync();
        viewModel.UseTestDirectory(@"C:\Projects\ecossistema-core");
        _runner.ResultsByHandler[typeof(RunCommandHandler)] = new CommandRunSummary(
        [
            new(0, "@build", "dotnet build", CommandStepState.Failed,
                new CommandExecutionResult(1, "", "error", At, At.AddSeconds(2)), null),
        ]);

        await viewModel.TestAsync(viewModel.Commands[0]);

        viewModel.HasTest.Should().BeTrue();
        viewModel.IsTesting.Should().BeFalse();
        viewModel.TestOutput.IsFailed.Should().BeTrue();
        viewModel.TestOutput.FooterText.Should().Contain("Exit Code 1");
    }

    // --- Parâmetros -------------------------------------------------------------

    [Fact]
    public async Task TheForm_ShowsTheParametersWhileTyping()
    {
        var viewModel = await LoadedAsync();

        viewModel.Command = "dotnet build";
        viewModel.HasDetectedParameters.Should().BeFalse();

        viewModel.Alias = "eco-sync";
        viewModel.Command = "eco-sync {nomebanco} -Dev";

        viewModel.HasDetectedParameters.Should().BeTrue();
        viewModel.DetectedParameters.Should().Contain("nomebanco").And.Contain("@eco-sync nomebanco=…");
    }

    [Fact]
    public async Task TheList_ShowsHowToCallACommandWithParameters()
    {
        var viewModel = await LoadedAsync();

        var eco = viewModel.Commands.Single(item => item.Alias == "@eco-sync");
        eco.HasParameters.Should().BeTrue();
        eco.Usage.Should().Be("Uso: @eco-sync nomebanco=…");
        viewModel.Commands[0].HasParameters.Should().BeFalse();
    }

    [Fact]
    public async Task Test_WithAMissingParameter_AsksForIt_AndRunsNothing()
    {
        var viewModel = await LoadedAsync();
        viewModel.UseTestDirectory(@"C:\Projects\ecossistema-core");

        await viewModel.TestAsync(viewModel.Commands.Single(item => item.Alias == "@eco-sync"));

        viewModel.TestArguments.Should().Be("nomebanco=");
        viewModel.ErrorMessage.Should().Contain("nomebanco");
        _runner.Invoked.Should().NotContain(typeof(RunCommandHandler));
    }

    [Fact]
    public async Task Test_WithTheParameter_Runs()
    {
        var viewModel = await LoadedAsync();
        viewModel.UseTestDirectory(@"C:\Projects\ecossistema-core");
        viewModel.TestArguments = "nomebanco=MeuBanco";
        _runner.ResultsByHandler[typeof(RunCommandHandler)] = new CommandRunSummary([]);

        await viewModel.TestAsync(viewModel.Commands.Single(item => item.Alias == "@eco-sync"));

        viewModel.ErrorMessage.Should().BeNull();
        viewModel.TestOutput.Command.Should().Be("eco-sync MeuBanco -Dev");
        _runner.Invoked.Should().Contain(typeof(RunCommandHandler));
    }
}
