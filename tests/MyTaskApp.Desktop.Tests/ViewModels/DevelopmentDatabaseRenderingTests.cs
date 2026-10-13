using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O banco do ambiente na janela de verdade (ADR-060): o binding de XAML só
/// falha em runtime, e uma seção que não liga parece vazia.
/// </summary>
public class DevelopmentDatabaseRenderingTests
{
    private const string Repository = @"C:\Projects\ecossistema-core";

    private static readonly DateTimeOffset At = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    private static readonly DevelopmentDatabaseView Copied =
        new("lock_eco_core_1010_20261013_101500", "localhost", 5433, "postgres", "Local", Guid.CreateVersion7(), At);

    private static async Task<(TaskNotesWindow Window, TaskNotesViewModel ViewModel)> ShowAsync(TaskDevelopmentView? development)
    {
        var runner = new FakeUseCaseRunner();
        runner.ResultsByHandler[typeof(GetTaskDirectoriesHandler)] = TaskNotesAliasTests.Directories;
        runner.ResultsByHandler[typeof(GetDevelopmentCommandsHandler)] = (IReadOnlyList<DevelopmentCommandRow>)[];
        runner.ResultsByHandler[typeof(GetQuickCommandsHandler)] = QuickCommandsView.Empty;
        runner.Enqueue<GetTaskDevelopmentsHandler>(TestDevelopment.List(development));
        runner.ResultsByHandler[typeof(DetectGitHandler)] = new GitInstallation(true, "2.51.0", "git");
        runner.ResultsByHandler[typeof(InspectDirectoryHandler)] = new DirectoryInspection(true, true, Repository);
        runner.ResultsByHandler[typeof(ListBranchesHandler)] = new BranchList([GitBranch.Local("main")], GitBranch.Local("main"));
        runner.ResultsByHandler[typeof(GetDatabaseConnectionsHandler)] = (IReadOnlyList<DatabaseConnectionRow>)[];
        runner.ResultsByHandler[typeof(GetAnonymizationProfilesHandler)] = (IReadOnlyList<AnonymizationProfileRow>)[];
        runner.ResultsByHandler[typeof(GetDatabaseCopyProfilesHandler)] = (IReadOnlyList<DatabaseCopyProfileRow>)[];
        runner.ResultsByHandler[typeof(GetSavedDatabasesHandler)] = (IReadOnlyList<SavedDatabaseRow>)[];

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

        return (window, viewModel);
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
    public async Task TheReadyCard_ShowsTheCopiedDatabase_BeforeThePostWorktreeCommands()
    {
        var development = TestDevelopment.View(Guid.NewGuid(), TaskDevelopmentStatus.Ready) with { Database = Copied };

        var (window, _) = await ShowAsync(development);

        Named<SelectableTextBlock>(window, "DatabaseName").Text.Should().Be(Copied.Name);
        Texts(Named<Border>(window, "DatabaseCard")).Should()
            .Contain("🗄 Banco do ambiente")
            .And.Contain("localhost:5433 · usuário postgres · Local");
        Named<Button>(window, "CopyDatabaseButton").Content.Should().Be("Copiar de novo…");

        var ready = Named<Border>(window, "ReadyCard").GetVisualDescendants().ToList();
        ready.IndexOf(Named<Border>(window, "DatabaseCard"))
            .Should().BeLessThan(ready.IndexOf(Named<PostWorktreeCommandsView>(window, "ReadyCommands")));
    }

    [AvaloniaFact]
    public async Task WithoutADatabase_TheCardOffersTheCopy_AndOpensTheSelector()
    {
        var (window, viewModel) = await ShowAsync(TestDevelopment.View(Guid.NewGuid(), TaskDevelopmentStatus.Ready));

        Named<TextBlock>(window, "NoDatabaseText").IsEffectivelyVisible.Should().BeTrue();
        var copy = Named<Button>(window, "CopyDatabaseButton");
        copy.Content.Should().Be("Copiar banco…");

        copy.Command!.Execute(null);
        Settle(window);

        viewModel.Developments.Selected!.Database.IsSelecting.Should().BeTrue();
        Named<Button>(window, "CopyNowButton").IsEffectivelyVisible.Should().BeTrue();
        Named<Button>(window, "CopyNowButton").IsEffectivelyEnabled.Should().BeFalse("nada escolhido ainda");
    }

    [AvaloniaFact]
    public async Task TheSetupForm_HidesTheSelector_UntilTheCopyIsChecked()
    {
        var (window, viewModel) = await ShowAsync(null);

        var check = Named<CheckBox>(window, "CopyOnCreateBox");
        check.IsEffectivelyVisible.Should().BeTrue();
        Named<StackPanel>(window, "SetupDatabase").GetVisualDescendants().OfType<DevelopmentDatabaseSelector>()
            .Single().IsEffectivelyVisible.Should().BeFalse();

        check.IsChecked = true;
        Settle(window);

        viewModel.Developments.Selected!.Database.CopyOnCreate.Should().BeTrue();
        Named<StackPanel>(window, "SetupDatabase").GetVisualDescendants().OfType<DevelopmentDatabaseSelector>()
            .Single().IsEffectivelyVisible.Should().BeTrue();
        Texts(window).Should().Contain(DevelopmentDatabaseViewModel.VariablesHint);
    }
}
