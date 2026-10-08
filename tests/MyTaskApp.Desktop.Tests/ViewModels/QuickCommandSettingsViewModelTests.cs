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
    private readonly FakeDirectoryCommandEditor _editor = new();

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

        var viewModel = new TagsViewModel(_runner, _confirmation, new FakeDirectoryProbe(), _editor, NullLogger<TagsViewModel>.Instance);
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

    // ---- Comando só do diretório (ADR-055) ----------------------------------

    private static readonly DevelopmentCommandRow Front = new(
        Guid.CreateVersion7(), null, "npm run dev -- --port {port}", "Sobe o Vite", At,
        "Front-end", CommandMode.Terminal, "web", true, false,
        [new CommandParameterSpec("port", "Porta", CommandParameterType.Number, "5173", true)],
        TagDirectoryId: Core.Id);

    private static TagDirectoryCommandRow OwnBinding(int order) =>
        new(Guid.NewGuid(), Core.Id, Front.Id, null, Front.Name, Front.Command, order, true, null, null, IsDirectoryOnly: true);

    private static DirectoryCommandDraft FrontDraft(DirectoryCommandEditorRequest _) =>
        new("npm run dev", null, new DevelopmentCommandSettings("Front-end", CommandMode.Terminal));

    [Fact]
    public async Task NewCommand_OpensTheDialog_CreatesInTheDirectory_AndReloads()
    {
        var (viewModel, directory) = await DirectoryAsync();
        await viewModel.ToggleDirectoryCommandsAsync(directory, Ct);
        _runner.ResultsByHandler[typeof(CreateDirectoryOnlyCommandHandler)] = Guid.NewGuid();
        _editor.Answer = FrontDraft;

        await viewModel.NewDirectoryCommandAsync(directory, Ct);

        var asked = _editor.Asked.Should().ContainSingle().Subject;
        asked.Heading.Should().Be("Novo comando de @ecossistema-core");
        asked.AcceptLabel.Should().Be("Criar comando");
        asked.Initial.Should().BeNull();
        _runner.Invoked.Should().ContainInOrder(typeof(CreateDirectoryOnlyCommandHandler), typeof(GetTagDirectoryCommandsHandler));
        viewModel.StatusMessage.Should().Be("Front-end criado em @ecossistema-core.");
    }

    [Fact]
    public async Task NewCommand_Refused_KeepsTheDialogOpen_WithTheReason()
    {
        var (viewModel, directory) = await DirectoryAsync();
        _runner.FailuresByHandler[typeof(CreateDirectoryOnlyCommandHandler)] =
            new MyTaskApp.Domain.DomainException("A pasta do comando precisa ficar dentro do worktree.");
        _editor.Answer = FrontDraft;

        await viewModel.NewDirectoryCommandAsync(directory, Ct);

        _editor.LastError.Should().Be("A pasta do comando precisa ficar dentro do worktree.");
        viewModel.StatusMessage.Should().BeNull();
    }

    [Fact]
    public async Task NewCommand_Cancelled_ChangesNothing()
    {
        var (viewModel, directory) = await DirectoryAsync();

        await viewModel.NewDirectoryCommandAsync(directory, Ct);

        _editor.Asked.Should().ContainSingle();
        _runner.Invoked.Should().NotContain(typeof(CreateDirectoryOnlyCommandHandler));
    }

    [Fact]
    public async Task AnOwnCommand_IsEditedInTheDialog_StartingFromWhatIsSaved()
    {
        var (viewModel, directory) = await DirectoryAsync(OwnBinding(0));
        await viewModel.ToggleDirectoryCommandsAsync(directory, Ct);
        _runner.ResultsByHandler[typeof(GetDirectoryOnlyCommandHandler)] = Front;
        _runner.ResultsByHandler[typeof(UpdateDirectoryOnlyCommandHandler)] = Front;
        _editor.Answer = request => new DirectoryCommandEditorViewModel(request).Draft();
        var item = directory.Commands.Single();

        item.IsDirectoryOnly.Should().BeTrue();
        item.RemoveTip.Should().Contain("Excluir");

        await viewModel.EditDirectoryOnlyCommandAsync(item, Ct);

        var asked = _editor.Asked.Should().ContainSingle().Subject;
        asked.Heading.Should().Be("Editar comando de @ecossistema-core");
        asked.Initial.Should().Be(Front);
        _runner.Invoked.Should().ContainInOrder(
            typeof(GetDirectoryOnlyCommandHandler),
            typeof(UpdateDirectoryOnlyCommandHandler),
            typeof(GetTagDirectoryCommandsHandler));
        viewModel.StatusMessage.Should().Be("Comando atualizado.");
    }

    [Fact]
    public async Task RemovingAnOwnCommand_AsksFirst_BecauseItIsDeleted()
    {
        var (viewModel, directory) = await DirectoryAsync(OwnBinding(0));
        await viewModel.ToggleDirectoryCommandsAsync(directory, Ct);

        await viewModel.RemoveDirectoryCommandAsync(directory.Commands[0], Ct);

        _confirmation.Asked.Should().ContainSingle().Which.Message.Should().Contain("só existe em @ecossistema-core");
        _runner.Invoked.Should().NotContain(typeof(RemoveTagDirectoryCommandHandler));

        _confirmation.Answer = true;
        await viewModel.RemoveDirectoryCommandAsync(directory.Commands[0], Ct);

        _runner.Invoked.Should().Contain(typeof(RemoveTagDirectoryCommandHandler));
        viewModel.StatusMessage.Should().Be("Front-end excluído.");
    }

    [Fact]
    public async Task RemovingAGlobal_DoesNotAsk()
    {
        var (viewModel, directory) = await DirectoryAsync(Binding(Run, 0));
        await viewModel.ToggleDirectoryCommandsAsync(directory, Ct);

        await viewModel.RemoveDirectoryCommandAsync(directory.Commands[0], Ct);

        _confirmation.Asked.Should().BeEmpty();
        _runner.Invoked.Should().Contain(typeof(RemoveTagDirectoryCommandHandler));
    }

    [Fact]
    public async Task WithEveryGlobalAlreadyHere_TheHintPointsToNewCommand()
    {
        var (viewModel, directory) = await DirectoryAsync(Binding(Run, 0), Binding(Test, 1));
        await viewModel.ToggleDirectoryCommandsAsync(directory, Ct);

        directory.NewCommandHint.Should().StartWith("Nenhum comando global a adicionar");
    }

    [Fact]
    public void TheDialog_StartsFromTheSavedCommand_AndBuildsTheDraft()
    {
        var editor = new DirectoryCommandEditorViewModel(
            new DirectoryCommandEditorRequest("Editar", "Salvar", Front, (_, _) => Task.FromResult<string?>(null)));

        editor.Name.Should().Be("Front-end");
        editor.IsTerminal.Should().BeTrue();
        editor.WorkingDirectory.Should().Be("web");
        editor.ParameterEditors.Should().ContainSingle().Which.Label.Should().Be("Porta");

        editor.Name = "  ";
        editor.CanAccept.Should().BeFalse();

        editor.Name = " Front ";
        editor.IsExecute = true;
        editor.Command = "npm run dev -- --port {port} --host {host}";
        var draft = editor.Draft();

        draft.Command.Should().Be("npm run dev -- --port {port} --host {host}");
        draft.Description.Should().Be("Sobe o Vite");
        draft.Settings.Name.Should().Be("Front");
        draft.Settings.Mode.Should().Be(CommandMode.Execute);
        draft.Settings.WorkingDirectory.Should().Be("web");
        draft.Settings.Parameters!.Select(spec => spec.Name).Should().Equal("port", "host");
        draft.Settings.Parameters![0].Label.Should().Be("Porta", "o que já estava preenchido fica");
    }

    [Fact]
    public async Task TheDialog_ShowsTheRefusal_AndStaysOpen()
    {
        var editor = new DirectoryCommandEditorViewModel(
            new DirectoryCommandEditorRequest("Novo", "Criar", null, (_, _) => Task.FromResult<string?>("Recusado.")))
        {
            Name = "Front-end",
            Command = "npm run dev",
        };

        (await editor.AcceptAsync(Ct)).Should().BeFalse();

        editor.ErrorMessage.Should().Be("Recusado.");
        editor.IsBusy.Should().BeFalse();
    }
}
