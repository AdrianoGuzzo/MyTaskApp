using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// Os comandos rápidos nas janelas de verdade (ADR-051): o binding de XAML só
/// falha em runtime, e um botão que não liga parece igual a um que liga.
/// </summary>
public class QuickCommandsRenderingTests
{
    private const string Repository = @"C:\Projects\ecossistema-core";

    private static readonly DateTimeOffset At = new(2026, 10, 5, 14, 0, 0, TimeSpan.Zero);

    private static readonly QuickCommandEntry Run = new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), "Executar aplicação", "@run", "dotnet run", CommandMode.Terminal,
        null, true, false, [], "ECO CORE", "@eco", false);

    private static readonly QuickCommandEntry Test = new(
        Guid.CreateVersion7(), Guid.CreateVersion7(), "Testes", "@test", "dotnet test", CommandMode.Execute,
        null, true, false, [], "ECO CORE", "@eco", false);

    private static async Task<(TaskNotesWindow Window, TaskNotesViewModel ViewModel, FakeUseCaseRunner Runner)> ShowAsync(
        TaskDevelopmentView development,
        QuickCommandsView? quickCommands = null)
    {
        var runner = new FakeUseCaseRunner();
        runner.ResultsByHandler[typeof(GetTaskDirectoriesHandler)] = TaskNotesAliasTests.Directories;
        runner.ResultsByHandler[typeof(GetDevelopmentCommandsHandler)] = (IReadOnlyList<DevelopmentCommandRow>)[];
        runner.ResultsByHandler[typeof(GetQuickCommandsHandler)] = quickCommands ?? QuickCommandsView.Empty;
        runner.Enqueue<GetTaskDevelopmentsHandler>(TestDevelopment.List(development));
        runner.ResultsByHandler[typeof(DetectGitHandler)] = new GitInstallation(true, "2.51.0", "git");
        runner.ResultsByHandler[typeof(InspectDirectoryHandler)] = new DirectoryInspection(true, true, Repository);
        runner.ResultsByHandler[typeof(ListBranchesHandler)] = new BranchList([GitBranch.Local("main")], GitBranch.Local("main"));

        var viewModel = new TaskNotesViewModel(
            runner,
            new FakeDirectoryProbe(),
            TestDevelopment.For(runner, timeProvider: new FakeTimeProvider()),
            NullLogger<TaskNotesViewModel>.Instance);

        viewModel.Load(TaskNotesAliasTests.Row());

        var window = new TaskNotesWindow(viewModel, new FakeConfirmationDialog());
        window.Show();

        viewModel.SelectedTabIndex = TaskNotesViewModel.DevelopmentTab;
        await viewModel.Developments.ActivateAsync(CancellationToken.None);
        Settle(window);

        return (window, viewModel, runner);
    }

    private static void Settle(Window window)
    {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        Dispatcher.UIThread.RunJobs();
    }

    private static T Named<T>(Control root, string name)
        where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(control => control.Name == name);

    private static IEnumerable<string?> Texts(Control root) =>
        root.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text);

    [AvaloniaFact]
    public async Task TheCard_SitsBetweenTheReadyAndTheAgentCards()
    {
        var (window, _, _) = await ShowAsync(
            TestDevelopment.View(Guid.NewGuid(), TaskDevelopmentStatus.Ready),
            new QuickCommandsView([Run, Test], [], []));

        var panel = Named<StackPanel>(window, "DevelopmentPanel");
        var cards = panel.Children.OfType<Border>().Select(border => border.Name).ToList();

        cards.IndexOf("QuickCommandsCard").Should().Be(cards.IndexOf("ReadyCard") + 1);
        cards.IndexOf("AgentCard").Should().Be(cards.IndexOf("QuickCommandsCard") + 1);
        Named<Border>(window, "QuickCommandsCard").IsVisible.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task TheCard_DrawsOneWiredButtonPerCommand()
    {
        var (window, _, runner) = await ShowAsync(
            TestDevelopment.View(Guid.NewGuid(), TaskDevelopmentStatus.Ready),
            new QuickCommandsView([Run, Test], [], []));

        var buttons = Named<ItemsControl>(window, "QuickCommandList")
            .GetVisualDescendants().OfType<Button>()
            .Where(button => (button.Content as string)?.StartsWith('▶') == true)
            .ToList();

        buttons.Select(button => button.Content).Should().Equal("▶ Executar aplicação", "▶ Testes");
        buttons.Should().OnlyContain(button => button.Command != null && button.Command.CanExecute(button.CommandParameter));
        Texts(Named<Border>(window, "QuickCommandsCard")).Should().Contain("⚡ Comandos").And.Contain("dotnet run");

        runner.ResultsByHandler[typeof(PrepareQuickCommandHandler)] =
            new QuickCommandPlan(Test, @"C:\wt", CommandContext.None);
        runner.ResultsByHandler[typeof(RunQuickCommandHandler)] = new CommandExecutionView(
            Guid.NewGuid(), Guid.NewGuid(), null, Test.CommandId, Test.BindingId, "Testes", "dotnet test", @"C:\wt",
            CommandMode.Execute, CommandExecutionStatus.Completed, At, At.AddSeconds(2), null, 0, "ok\n", null, null);

        buttons[1].Command!.Execute(buttons[1].CommandParameter);
        Settle(window);

        runner.Invoked.Should().Contain(typeof(RunQuickCommandHandler));
        Named<StackPanel>(window, "QuickCommandOutput").IsVisible.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task WithoutCommands_TheCardSaysWhereToConfigureThem()
    {
        var (window, _, _) = await ShowAsync(
            TestDevelopment.View(Guid.NewGuid(), TaskDevelopmentStatus.Ready),
            new QuickCommandsView([], [], []));

        Named<TextBlock>(window, "QuickCommandsEmpty").IsVisible.Should().BeTrue();
        Named<Button>(window, "AdHocButton").IsEffectivelyVisible.Should().BeFalse("sem global não há o que executar");
    }

    [AvaloniaFact]
    public async Task TheCard_IsHidden_UntilTheWorktreeIsReady()
    {
        var (window, _, runner) = await ShowAsync(TestDevelopment.View(Guid.NewGuid(), TaskDevelopmentStatus.Error, failure: "falhou"));

        Named<Border>(window, "QuickCommandsCard").IsVisible.Should().BeFalse();
        runner.Invoked.Should().NotContain(typeof(GetQuickCommandsHandler));
    }

    [AvaloniaFact]
    public void ThePrompt_DrawsTheFieldsAndThePreview()
    {
        var plan = new QuickCommandPlan(
            Run with
            {
                Template = "dotnet run --launch-profile {profile}",
                Parameters = [new CommandParameterSpec("profile", "Perfil", CommandParameterType.Choice, "Development", true, ["Development", "Staging"])],
                RequiresConfirmation = true,
            },
            @"C:\wt",
            CommandContext.None);
        var viewModel = new QuickCommandPromptViewModel(plan);

        var window = new QuickCommandPromptWindow(viewModel);
        window.Show();
        Settle(window);

        Named<SelectableTextBlock>(window, "PreviewLine").Text.Should().Be("dotnet run --launch-profile Development");
        window.GetVisualDescendants().OfType<ComboBox>().Single().SelectedItem.Should().Be("Development");
        Named<Button>(window, "RunButton").IsEnabled.Should().BeTrue();

        viewModel.Fields[0].Value = "Staging";
        Settle(window);

        Named<SelectableTextBlock>(window, "PreviewLine").Text.Should().Be("dotnet run --launch-profile Staging");
    }

    [AvaloniaFact]
    public async Task TheGlobalsWindow_DrawsTheQuickCommandFields()
    {
        var runner = new FakeUseCaseRunner();
        runner.ResultsByHandler[typeof(GetDevelopmentCommandsHandler)] = (IReadOnlyList<DevelopmentCommandRow>)[];
        var viewModel = new DevelopmentCommandsViewModel(runner, new FakeConfirmationDialog(), NullLogger<DevelopmentCommandsViewModel>.Instance);

        var window = new DevelopmentCommandsWindow(viewModel);
        window.Show();
        await viewModel.LoadAsync(CancellationToken.None);
        viewModel.Command = "dotnet run --launch-profile {profile}";
        Settle(window);

        Named<TextBox>(window, "NameBox").Should().NotBeNull();
        Named<RadioButton>(window, "TerminalMode").IsChecked.Should().BeFalse();
        Named<TextBox>(window, "WorkingDirectoryBox").Should().NotBeNull();
        Named<ItemsControl>(window, "ParameterEditorList").GetVisualDescendants().OfType<ComboBox>().Should().ContainSingle();

        Named<RadioButton>(window, "TerminalMode").IsChecked = true;
        Settle(window);

        viewModel.IsTerminal.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task TheTagsWindow_DrawsTheDirectoryCommands()
    {
        var eco = new TagRow(Guid.NewGuid(), "ECO CORE", "#22C55E", 1, 1);
        var core = new TagDirectoryRow(Guid.NewGuid(), eco.Id, eco.Name, eco.ColorHex, "@ecossistema-core", Repository, null, null, CommandCount: 1);
        var run = new DevelopmentCommandRow(Guid.NewGuid(), "@run", "dotnet run", null, At, "Executar aplicação");
        var test = new DevelopmentCommandRow(Guid.NewGuid(), "@test", "dotnet test", null, At, "Testes");

        var runner = new FakeUseCaseRunner();
        runner.ResultsByHandler[typeof(GetTagsHandler)] = (IReadOnlyList<TagRow>)[eco];
        runner.ResultsByHandler[typeof(GetTagDirectoriesHandler)] = (IReadOnlyList<TagDirectoryRow>)[core];
        runner.ResultsByHandler[typeof(GetDevelopmentCommandsHandler)] = (IReadOnlyList<DevelopmentCommandRow>)[run, test];
        runner.ResultsByHandler[typeof(GetTagDirectoryCommandsHandler)] = (IReadOnlyList<TagDirectoryCommandRow>)
            [new(Guid.NewGuid(), core.Id, run.Id, run.Alias, run.Name, run.Command, 0, true, null, null)];

        var viewModel = new TagsViewModel(
            runner, new FakeConfirmationDialog(), new FakeDirectoryProbe(), new FakeDirectoryCommandEditor(), NullLogger<TagsViewModel>.Instance);
        var window = new TagsWindow(viewModel);
        window.Show();
        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.ToggleDirectoriesAsync(viewModel.Tags.Single(), CancellationToken.None);
        await viewModel.ToggleDirectoryCommandsAsync(viewModel.Tags.Single().Directories.Single(), CancellationToken.None);
        Settle(window);

        var section = Named<Border>(window, "DirectoryCommands");
        section.IsVisible.Should().BeTrue();
        Texts(section).Should().Contain("Executar aplicação").And.Contain("dotnet run");
        section.GetVisualDescendants().OfType<Button>()
            .Where(button => button.Command is not null)
            .Should().NotBeEmpty()
            .And.OnlyContain(button => button.Command!.CanExecute(button.CommandParameter));
        section.GetVisualDescendants().OfType<ComboBox>().Single().SelectedItem.Should().Be(test);
    }

    [AvaloniaFact]
    public async Task TheTagsWindow_DrawsADirectoryOnlyCommand_WithEditInsteadOfCustomize()
    {
        var eco = new TagRow(Guid.NewGuid(), "ECO CORE", "#22C55E", 1, 1);
        var core = new TagDirectoryRow(Guid.NewGuid(), eco.Id, eco.Name, eco.ColorHex, "@ecossistema-core", Repository, null, null, CommandCount: 1);

        var runner = new FakeUseCaseRunner();
        runner.ResultsByHandler[typeof(GetTagsHandler)] = (IReadOnlyList<TagRow>)[eco];
        runner.ResultsByHandler[typeof(GetTagDirectoriesHandler)] = (IReadOnlyList<TagDirectoryRow>)[core];
        runner.ResultsByHandler[typeof(GetDevelopmentCommandsHandler)] = (IReadOnlyList<DevelopmentCommandRow>)[];
        runner.ResultsByHandler[typeof(GetTagDirectoryCommandsHandler)] = (IReadOnlyList<TagDirectoryCommandRow>)
            [new(Guid.NewGuid(), core.Id, Guid.NewGuid(), null, "Front-end", "npm run dev", 0, true, null, null, IsDirectoryOnly: true)];

        var viewModel = new TagsViewModel(
            runner, new FakeConfirmationDialog(), new FakeDirectoryProbe(), new FakeDirectoryCommandEditor(), NullLogger<TagsViewModel>.Instance);
        var window = new TagsWindow(viewModel);
        window.Show();
        await viewModel.LoadAsync(CancellationToken.None);
        await viewModel.ToggleDirectoriesAsync(viewModel.Tags.Single(), CancellationToken.None);
        await viewModel.ToggleDirectoryCommandsAsync(viewModel.Tags.Single().Directories.Single(), CancellationToken.None);
        Settle(window);

        var section = Named<Border>(window, "DirectoryCommands");
        var visible = section.GetVisualDescendants().OfType<Button>().Where(button => button.IsEffectivelyVisible).ToList();

        Texts(section).Should().Contain("Front-end").And.Contain("Só deste diretório");
        visible.Select(button => button.Content as string).Should()
            .Contain("Editar").And.Contain("+ Novo comando")
            .And.NotContain("Personalizar").And.NotContain("+ Adicionar global");
        visible.Where(button => button.Command is not null)
            .Should().OnlyContain(button => button.Command!.CanExecute(button.CommandParameter));
        section.GetVisualDescendants().OfType<ComboBox>().Should().OnlyContain(combo => !combo.IsEffectivelyVisible);
    }

    [AvaloniaFact]
    public void TheDirectoryCommandDialog_DrawsTheFormWithoutAlias_AndAcceptsOnlyWithAName()
    {
        var viewModel = new DirectoryCommandEditorViewModel(new DirectoryCommandEditorRequest(
            "Novo comando de @ecossistema-core",
            "Criar comando",
            null,
            (_, _) => Task.FromResult<string?>(null)));

        var window = new DirectoryCommandWindow(viewModel);
        window.Show();
        viewModel.Command = "npm run dev -- --port {porta}";
        Settle(window);

        window.Title.Should().Be("Novo comando de @ecossistema-core");
        Named<TextBox>(window, "NameBox").Should().NotBeNull();
        window.GetVisualDescendants().OfType<TextBox>().Should().NotContain(box => box.Name == "AliasBox");
        Named<ItemsControl>(window, "ParameterEditorList").GetVisualDescendants().OfType<ComboBox>().Should().ContainSingle();
        Named<Button>(window, "AcceptButton").IsEnabled.Should().BeFalse("sem nome, não há rótulo para o botão");

        viewModel.Name = "Front-end";
        Named<RadioButton>(window, "TerminalMode").IsChecked = true;
        Settle(window);

        Named<Button>(window, "AcceptButton").IsEnabled.Should().BeTrue();
        viewModel.IsTerminal.Should().BeTrue();
    }
}
