using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A seção "⚡ Comandos" do ambiente pronto (ADR-051): botões na ordem, rodar,
/// perguntar só o que o comando pede, e mostrar o resultado.
/// </summary>
public class QuickCommandsViewModelTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 5, 14, 0, 0, TimeSpan.Zero);

    private const string Worktree = @"C:\Projects\eco-feature-x";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();
    private readonly FakeQuickCommandPrompt _prompt = new();
    private readonly Guid _taskId = Guid.CreateVersion7();
    private readonly Guid _developmentId = Guid.CreateVersion7();

    private static readonly QuickCommandEntry Run = Entry("Executar aplicação", "dotnet run", CommandMode.Terminal);
    private static readonly QuickCommandEntry Test = Entry("Testes", "dotnet test");

    private static QuickCommandEntry Entry(
        string name,
        string template,
        CommandMode mode = CommandMode.Execute,
        IReadOnlyList<CommandParameterSpec>? parameters = null,
        bool confirm = false) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), name, "@x", template, mode, null, true, confirm,
            parameters ?? [], "ECO CORE", "@eco", false);

    private static DevelopmentCommandRow Global(string alias, string command) =>
        new(Guid.CreateVersion7(), alias, command, null, At, Name: alias.TrimStart('@'));

    private CommandExecutionView Execution(
        QuickCommandEntry entry,
        CommandExecutionStatus status,
        int? exitCode = null,
        int? processId = null,
        string? output = null) =>
        new(Guid.CreateVersion7(), _taskId, _developmentId, entry.CommandId, entry.BindingId, entry.Name, entry.Template,
            Worktree, entry.Mode, status, At, status is CommandExecutionStatus.Running ? null : At.AddSeconds(4),
            processId, exitCode, output, null, null);

    private static QuickCommandsView View(
        IReadOnlyList<QuickCommandEntry> entries,
        IReadOnlyList<CommandExecutionView>? recent = null,
        IReadOnlyList<DevelopmentCommandRow>? globals = null) =>
        new(entries, globals ?? [Global("@deploy", "deploy")], recent ?? []);

    private static QuickCommandPlan Plan(QuickCommandEntry entry) => new(entry, Worktree, CommandContext.None);

    private async Task<QuickCommandsViewModel> LoadedAsync(QuickCommandsView view)
    {
        _runner.ResultsByHandler[typeof(GetQuickCommandsHandler)] = view;

        var viewModel = new QuickCommandsViewModel(_runner, _prompt, NullLogger<QuickCommandsViewModel>.Instance);
        viewModel.Load(_taskId, _developmentId);
        await viewModel.RefreshAsync(Ct);

        return viewModel;
    }

    [Fact]
    public async Task Loading_ShowsTheButtonsInOrder_WithTheLastRunOfEach()
    {
        var viewModel = await LoadedAsync(View(
            [Run, Test],
            [Execution(Test, CommandExecutionStatus.Failed, exitCode: 1), Execution(Test, CommandExecutionStatus.Completed, 0)]));

        viewModel.Items.Select(item => item.RunLabel).Should().Equal("▶ Executar aplicação", "▶ Testes");
        viewModel.Items[0].HasStatus.Should().BeFalse();
        viewModel.Items[1].IsFailed.Should().BeTrue();
        viewModel.Items[1].StatusText.Should().StartWith("✗ Falhou · exit 1");
        viewModel.HasNoItems.Should().BeFalse();
        viewModel.HasGlobals.Should().BeTrue();
    }

    [Fact]
    public async Task NoCommands_SaysWhereToConfigureThem()
    {
        var viewModel = await LoadedAsync(View([]));

        viewModel.HasItems.Should().BeFalse();
        viewModel.HasNoItems.Should().BeTrue();
    }

    [Fact]
    public async Task Run_WithNothingToAsk_RunsAtOnce()
    {
        var viewModel = await LoadedAsync(View([Test]));
        _runner.ResultsByHandler[typeof(PrepareQuickCommandHandler)] = Plan(Test);
        _runner.ResultsByHandler[typeof(RunQuickCommandHandler)] = Execution(Test, CommandExecutionStatus.Completed, 0, output: "ok\n");

        await viewModel.RunAsync(viewModel.Items[0]);

        _prompt.Asked.Should().BeEmpty();
        _runner.Invoked.Should().ContainInOrder(
            typeof(PrepareQuickCommandHandler), typeof(RunQuickCommandHandler), typeof(GetQuickCommandsHandler));
        viewModel.HasOutput.Should().BeTrue();
        viewModel.Output.IsSucceeded.Should().BeTrue();
        viewModel.Output.FooterText.Should().Contain("Exit Code 0");
        viewModel.IsRunning.Should().BeFalse();
    }

    [Fact]
    public async Task Run_WithParameters_AsksFirst_AndCancellingRunsNothing()
    {
        var withProject = Entry("Executar projeto", "dotnet run --project {project}", parameters: [new("project", "Projeto")]);
        var viewModel = await LoadedAsync(View([withProject]));
        _runner.ResultsByHandler[typeof(PrepareQuickCommandHandler)] = Plan(withProject);
        _prompt.Answer = null;

        await viewModel.RunAsync(viewModel.Items[0]);

        _prompt.Asked.Should().ContainSingle().Which.Entry.Should().Be(withProject);
        _runner.Invoked.Should().NotContain(typeof(RunQuickCommandHandler));
    }

    [Fact]
    public async Task Run_RequiringConfirmation_Asks_EvenWithoutParameters()
    {
        var prune = Entry("Limpar Docker", "docker system prune -af", confirm: true);
        var viewModel = await LoadedAsync(View([prune]));
        _runner.ResultsByHandler[typeof(PrepareQuickCommandHandler)] = Plan(prune);
        _runner.ResultsByHandler[typeof(RunQuickCommandHandler)] = Execution(prune, CommandExecutionStatus.Completed, 0);

        await viewModel.RunAsync(viewModel.Items[0]);

        _prompt.Asked.Should().ContainSingle();
        _runner.Invoked.Should().Contain(typeof(RunQuickCommandHandler));
    }

    [Fact]
    public async Task Run_Terminal_ShowsRunning_AndOffersShowTerminal()
    {
        var running = Execution(Run, CommandExecutionStatus.Running, processId: 24680);
        _runner.Enqueue<GetQuickCommandsHandler>(View([Run]), View([Run], [running]));
        var viewModel = new QuickCommandsViewModel(_runner, _prompt, NullLogger<QuickCommandsViewModel>.Instance);
        viewModel.Load(_taskId, _developmentId);
        await viewModel.RefreshAsync(Ct);
        _runner.ResultsByHandler[typeof(PrepareQuickCommandHandler)] = Plan(Run);
        _runner.ResultsByHandler[typeof(RunQuickCommandHandler)] = running;

        await viewModel.RunAsync(viewModel.Items[0]);

        var item = viewModel.Items[0];
        item.HasTerminal.Should().BeTrue();
        item.StatusText.Should().StartWith("🟢 Executando desde");
        item.CanRun.Should().BeFalse();
        viewModel.HasOutput.Should().BeFalse("o terminal é a janela dele");
    }

    [Fact]
    public async Task ShowTerminal_OfOneThatClosed_SaysSo()
    {
        var running = Execution(Run, CommandExecutionStatus.Running, processId: 24680);
        var viewModel = await LoadedAsync(View([Run], [running]));
        _runner.ResultsByHandler[typeof(FocusCommandExecutionHandler)] =
            new CommandFocusResult(running with { Status = CommandExecutionStatus.Completed, FinishedAt = At }, false);

        await viewModel.ShowTerminalAsync(viewModel.Items[0]);

        viewModel.Message.Should().Be("O terminal já foi fechado.");
        viewModel.Items[0].HasTerminal.Should().BeFalse();
    }

    [Fact]
    public async Task ADomainError_IsTheMessage()
    {
        var viewModel = await LoadedAsync(View([Test]));
        _runner.FailuresByHandler[typeof(PrepareQuickCommandHandler)] =
            new DomainException("A pasta src/Eco.Web não existe neste worktree.");

        await viewModel.RunAsync(viewModel.Items[0]);

        viewModel.Message.Should().Be("A pasta src/Eco.Web não existe neste worktree.");
        _runner.Invoked.Should().NotContain(typeof(RunQuickCommandHandler));
    }

    [Fact]
    public async Task AFailedTerminal_ShowsWhy()
    {
        var viewModel = await LoadedAsync(View([Run]));
        _runner.ResultsByHandler[typeof(PrepareQuickCommandHandler)] = Plan(Run);
        _runner.ResultsByHandler[typeof(RunQuickCommandHandler)] =
            Execution(Run, CommandExecutionStatus.Failed) with { FailureReason = "Abrir um terminal pelo app ainda não é suportado neste sistema." };

        await viewModel.RunAsync(viewModel.Items[0]);

        viewModel.Message.Should().Contain("ainda não é suportado");
    }

    [Fact]
    public async Task AdHoc_RunsAnyGlobal_AndCloses()
    {
        var deploy = Global("@deploy", "deploy");
        var viewModel = await LoadedAsync(View([Test], globals: [deploy]));
        var entry = QuickCommandCatalog.AdHoc(deploy, []);
        _runner.ResultsByHandler[typeof(PrepareQuickCommandHandler)] = Plan(entry);
        _runner.ResultsByHandler[typeof(RunQuickCommandHandler)] = Execution(entry, CommandExecutionStatus.Completed, 0);

        viewModel.ToggleAdHoc();
        viewModel.SelectedAdHoc.Should().Be(deploy);
        viewModel.AdHocPreview.Should().Contain("deploy").And.Contain("raiz do worktree");

        await viewModel.RunAdHocAsync();

        _runner.Invoked.Should().Contain(typeof(RunQuickCommandHandler));
        viewModel.IsAdHocOpen.Should().BeFalse();
    }

    [Fact]
    public async Task ShowOutput_BringsBackTheStoredOutput()
    {
        var finished = Execution(Test, CommandExecutionStatus.Completed, 0, output: "linha 1\nlinha 2\n");
        var viewModel = await LoadedAsync(View([Test], [finished]));

        viewModel.Items[0].HasOutput.Should().BeTrue();
        viewModel.ShowOutput(viewModel.Items[0]);

        viewModel.HasOutput.Should().BeTrue();
        viewModel.Output.Lines.Select(line => line.Text).Should().Equal("linha 1", "linha 2");
        viewModel.Output.IsSucceeded.Should().BeTrue();
    }

    [Fact]
    public void ARunningHiddenCommand_MakesTheEnvironmentBusy()
    {
        var environment = TestDevelopment.Environment(_runner);

        environment.IsBusy.Should().BeFalse();
        environment.QuickCommands.IsRunning = true;

        environment.IsBusy.Should().BeTrue();
    }

    [Fact]
    public async Task ALoadFailure_IsAMessage_NotACrash()
    {
        _runner.FailuresByHandler[typeof(GetQuickCommandsHandler)] = new InvalidOperationException("banco");
        var viewModel = new QuickCommandsViewModel(_runner, _prompt, NullLogger<QuickCommandsViewModel>.Instance);
        viewModel.Load(_taskId, _developmentId);

        await viewModel.RefreshAsync(Ct);

        viewModel.Message.Should().Contain("Não foi possível carregar");
        viewModel.HasNoItems.Should().BeFalse();
    }
}
