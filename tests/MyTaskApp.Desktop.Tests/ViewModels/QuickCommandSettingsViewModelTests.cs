using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// Onde o comando rápido é configurado (ADR-051): o formulário de Comandos
/// globais e a lista "Comandos" do diretório em Etiquetas.
/// </summary>
public class QuickCommandSettingsViewModelTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 5, 10, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DevelopmentCommandRow Run = new(
        Guid.CreateVersion7(), "@run", "dotnet run --launch-profile {profile}", null, At,
        "Executar aplicação", CommandMode.Terminal, "src/Eco.Web", false, true,
        [new CommandParameterSpec("profile", "Perfil", CommandParameterType.Choice, "Development", true, ["Development", "Staging"])],
        BindingCount: 2);

    private static readonly DevelopmentCommandRow Test = new(Guid.CreateVersion7(), "@test", "dotnet test", null, At, "Testes");

    private readonly FakeUseCaseRunner _runner = new();
    private readonly FakeConfirmationDialog _confirmation = new();

    private async Task<DevelopmentCommandsViewModel> GlobalsAsync()
    {
        _runner.ResultsByHandler[typeof(GetDevelopmentCommandsHandler)] = (IReadOnlyList<DevelopmentCommandRow>)[Run, Test];

        var viewModel = new DevelopmentCommandsViewModel(_runner, _confirmation, NullLogger<DevelopmentCommandsViewModel>.Instance);
        await viewModel.LoadAsync(Ct);

        return viewModel;
    }

    [Fact]
    public async Task TheList_ShowsTheNameAndWhatTheButtonDoes()
    {
        var viewModel = await GlobalsAsync();

        var run = viewModel.Commands.Single(item => item.Alias == "@run");
        run.DisplayName.Should().Be("Executar aplicação");
        run.Summary.Should().Be("Terminal · pasta src/Eco.Web · pede confirmação · em 2 diretórios");
        viewModel.Commands.Single(item => item.Alias == "@test").Summary.Should().Be("Execução");
    }

    [Fact]
    public async Task TypingAName_SuggestsTheAlias_UntilTheUserWritesOne()
    {
        var viewModel = await GlobalsAsync();

        viewModel.Name = "Executar aplicação";
        viewModel.Alias.Should().Be("@executar-aplicacao");

        viewModel.Name = "Executar API";
        viewModel.Alias.Should().Be("@executar-api");

        viewModel.Alias = "@api";
        viewModel.Name = "Executar a API";
        viewModel.Alias.Should().Be("@api");
    }

    [Fact]
    public async Task TypingAPlaceholder_AddsItsParameterRow_ButNotForContextVariables()
    {
        var viewModel = await GlobalsAsync();

        viewModel.Command = "dotnet run --project \"{worktree}/{project}\" --launch-profile {profile}";

        viewModel.ParameterEditors.Select(editor => editor.Name).Should().Equal("project", "profile");
        viewModel.DetectedParameters.Should().NotContain("worktree");

        viewModel.ParameterEditors[1].Label = "Perfil";
        viewModel.Command = "dotnet run --launch-profile {profile}";

        viewModel.ParameterEditors.Should().ContainSingle().Which.Label.Should().Be("Perfil", "o que já foi preenchido fica");
    }

    [Fact]
    public async Task Edit_FillsTheQuickCommandFields()
    {
        var viewModel = await GlobalsAsync();

        viewModel.Edit(viewModel.Commands.Single(item => item.Alias == "@run"));

        viewModel.Name.Should().Be("Executar aplicação");
        viewModel.Alias.Should().Be("@run");
        viewModel.IsTerminal.Should().BeTrue();
        viewModel.IsExecute.Should().BeFalse();
        viewModel.WorkingDirectory.Should().Be("src/Eco.Web");
        viewModel.KeepTerminalOpen.Should().BeFalse();
        viewModel.RequiresConfirmation.Should().BeTrue();
        viewModel.ParameterEditors.Should().ContainSingle();
        viewModel.ParameterEditors[0].IsChoice.Should().BeTrue();
        viewModel.ParameterEditors[0].ToSpec().Should().BeEquivalentTo(Run.Parameters![0]);
    }

    [Fact]
    public async Task Save_WithOnlyAName_UsesTheSuggestedAlias()
    {
        var viewModel = await GlobalsAsync();
        _runner.ResultsByHandler[typeof(CreateDevelopmentCommandHandler)] = Test;

        viewModel.Name = "Testes";
        viewModel.Alias = string.Empty;
        viewModel.Command = "dotnet test";

        viewModel.CanSave.Should().BeTrue();
        await viewModel.SaveAsync(Ct);

        _runner.Invoked.Should().Contain(typeof(CreateDevelopmentCommandHandler));
        viewModel.StatusMessage.Should().Be("Comando criado.");
        viewModel.Name.Should().BeEmpty();
        viewModel.IsTerminal.Should().BeFalse();
    }

    [Fact]
    public async Task Delete_WarnsHowManyDirectoriesUseIt()
    {
        var viewModel = await GlobalsAsync();

        await viewModel.DeleteAsync(viewModel.Commands.Single(item => item.Alias == "@run"), Ct);

        _confirmation.Asked.Should().ContainSingle().Which.Message.Should().Contain("2 diretórios");
    }

    [Fact]
    public async Task TheUsageHint_LeavesContextVariablesOut()
    {
        _runner.ResultsByHandler[typeof(GetDevelopmentCommandsHandler)] =
            (IReadOnlyList<DevelopmentCommandRow>)[new(Guid.CreateVersion7(), "@open", "code \"{worktree}\"", null, At)];
        var viewModel = new DevelopmentCommandsViewModel(_runner, _confirmation, NullLogger<DevelopmentCommandsViewModel>.Instance);
        await viewModel.LoadAsync(Ct);

        viewModel.Commands.Single().HasParameters.Should().BeFalse();
    }

    // ---- Etiquetas ----------------------------------------------------------

    private static readonly TagRow Eco = new(Guid.NewGuid(), "ECO CORE", "#22C55E", 1, 1);

    private static readonly TagDirectoryRow Core =
        new(Guid.NewGuid(), Eco.Id, Eco.Name, Eco.ColorHex, "@ecossistema-core", @"C:\Projects\ecossistema-core", null, null, CommandCount: 1);

    private static TagDirectoryCommandRow Binding(DevelopmentCommandRow command, int order, bool enabled = true, string? commandOverride = null) =>
        new(Guid.NewGuid(), Core.Id, command.Id, command.Alias, command.Name, command.Command, order, enabled, commandOverride, null);

    private async Task<(TagsViewModel ViewModel, TagDirectoryItemViewModel Directory)> DirectoryAsync(
        params TagDirectoryCommandRow[] bindings)
    {
        _runner.ResultsByHandler[typeof(GetTagsHandler)] = (IReadOnlyList<TagRow>)[Eco];
        _runner.ResultsByHandler[typeof(GetTagDirectoriesHandler)] = (IReadOnlyList<TagDirectoryRow>)[Core];
        _runner.ResultsByHandler[typeof(GetTagDirectoryCommandsHandler)] = (IReadOnlyList<TagDirectoryCommandRow>)bindings;
        _runner.ResultsByHandler[typeof(GetDevelopmentCommandsHandler)] = (IReadOnlyList<DevelopmentCommandRow>)[Run, Test];
        _runner.ResultsByHandler[typeof(AddTagDirectoryCommandHandler)] = Guid.NewGuid();

        var viewModel = new TagsViewModel(_runner, _confirmation, new FakeDirectoryProbe(), NullLogger<TagsViewModel>.Instance);
        await viewModel.LoadAsync(Ct);
        var tag = viewModel.Tags.Single();
        await viewModel.ToggleDirectoriesAsync(tag, Ct);

        return (viewModel, tag.Directories.Single());
    }

    [Fact]
    public async Task TheDirectory_ShowsHowManyCommandsItHas_AndLoadsThemOnlyWhenOpened()
    {
        var (_, directory) = await DirectoryAsync(Binding(Run, 0));

        directory.CommandsLabel.Should().Be("Comandos (1)");
        _runner.Invoked.Should().NotContain(typeof(GetTagDirectoryCommandsHandler));
    }

    [Fact]
    public async Task Opening_ListsTheBindings_AndOffersTheOtherGlobals()
    {
        var (viewModel, directory) = await DirectoryAsync(Binding(Run, 0, commandOverride: "dotnet run --project src/Eco.Web"));

        await viewModel.ToggleDirectoryCommandsAsync(directory, Ct);

        directory.IsCommandsExpanded.Should().BeTrue();
        directory.Commands.Should().ContainSingle();
        directory.Commands[0].EffectiveCommand.Should().Be("dotnet run --project src/Eco.Web");
        directory.Commands[0].IsCustomized.Should().BeTrue();
        directory.Commands[0].CanMoveUp.Should().BeFalse();
        directory.AvailableCommands.Should().Equal(Test);
        directory.SelectedNewCommand.Should().Be(Test);
    }

    [Fact]
    public async Task Add_Toggle_Move_Remove_GoThroughTheUseCases_AndReload()
    {
        var (viewModel, directory) = await DirectoryAsync(Binding(Run, 0), Binding(Test, 1));
        await viewModel.ToggleDirectoryCommandsAsync(directory, Ct);

        await viewModel.ToggleDirectoryCommandAsync(directory.Commands[0], Ct);
        await viewModel.MoveDirectoryCommandDownAsync(directory.Commands[0], Ct);
        await viewModel.RemoveDirectoryCommandAsync(directory.Commands[1], Ct);

        _runner.Invoked.Should().ContainInOrder(
            typeof(SetTagDirectoryCommandEnabledHandler),
            typeof(GetTagDirectoryCommandsHandler),
            typeof(MoveTagDirectoryCommandHandler),
            typeof(GetTagDirectoryCommandsHandler),
            typeof(RemoveTagDirectoryCommandHandler),
            typeof(GetTagDirectoryCommandsHandler));
        viewModel.StatusMessage.Should().Contain("O comando global continua cadastrado");
    }

    [Fact]
    public async Task Customize_StartsFromTheGlobalLine_AndSaves()
    {
        var (viewModel, directory) = await DirectoryAsync(Binding(Run, 0));
        await viewModel.ToggleDirectoryCommandsAsync(directory, Ct);
        var item = directory.Commands[0];

        viewModel.BeginCustomizeDirectoryCommand(item);

        item.IsCustomizing.Should().BeTrue();
        item.UsesGlobal.Should().BeTrue();
        item.CommandOverride.Should().Be(Run.Command);

        item.Customize = true;
        item.CommandOverride = "dotnet run --project src/Eco.Web";
        await viewModel.SaveDirectoryCommandCustomizationAsync(item, Ct);

        _runner.Invoked.Should().Contain(typeof(CustomizeTagDirectoryCommandHandler));
        viewModel.StatusMessage.Should().Contain("personalizado");
    }

    [Fact]
    public async Task Add_WithNothingChosen_Explains()
    {
        var (viewModel, directory) = await DirectoryAsync(Binding(Run, 0), Binding(Test, 1));
        await viewModel.ToggleDirectoryCommandsAsync(directory, Ct);

        directory.HasAvailableCommands.Should().BeFalse();
        await viewModel.AddDirectoryCommandAsync(directory, Ct);

        viewModel.ErrorMessage.Should().Contain("Escolha um comando");
        _runner.Invoked.Should().NotContain(typeof(AddTagDirectoryCommandHandler));
    }
}
