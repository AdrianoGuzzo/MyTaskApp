using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Remote.Contracts;
using NSubstitute;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// "Preparar em…" na janela de verdade (T-25): o binding de XAML só falha em
/// runtime, e um painel que não liga simplesmente não aparece.
/// </summary>
public class RemotePrepareRenderingTests
{
    private const string Repository = @"C:\Projects\ecossistema-core";

    private static readonly DateTimeOffset Now = new(2026, 10, 26, 12, 0, 0, TimeSpan.Zero);

    private static async Task<(TaskNotesWindow Window, RemotePrepareViewModel Prepare)> ShowAsync(bool anyoneThere)
    {
        var runner = new FakeUseCaseRunner();
        runner.ResultsByHandler[typeof(GetTaskDirectoriesHandler)] = TaskNotesAliasTests.Directories;
        runner.ResultsByHandler[typeof(GetDevelopmentCommandsHandler)] = (IReadOnlyList<DevelopmentCommandRow>)[];
        runner.ResultsByHandler[typeof(GetQuickCommandsHandler)] = QuickCommandsView.Empty;
        runner.Enqueue<GetTaskDevelopmentsHandler>(TestDevelopment.List());
        runner.ResultsByHandler[typeof(DetectGitHandler)] = new GitInstallation(true, "2.51.0", "git");
        runner.ResultsByHandler[typeof(InspectDirectoryHandler)] = new DirectoryInspection(true, true, Repository);
        runner.ResultsByHandler[typeof(ListBranchesHandler)] = new BranchList([GitBranch.Local("main")], GitBranch.Local("main"));
        runner.ResultsByHandler[typeof(GetMachinesHandler)] = new MachinesView(
            new RemoteSettings(true, true),
            RemoteAvailability.Ready,
            null,
            anyoneThere ? [new MachineView(Guid.CreateVersion7(), "Notebook", "linux", "1.25.0", false, true, Now, true, null, [])] : [],
            [],
            []);
        runner.ResultsByHandler[typeof(GetRemoteControlHandler)] = new RemoteControlView(
            new AgentCatalog("h", [new CatalogDirectory(Guid.CreateVersion7(), Guid.CreateVersion7(), "ECO · Núcleo", true, true)], [], [], []),
            []);

        var account = Substitute.For<ISyncAccount>();
        account.IsAvailable.Returns(true);
        var prepare = new RemotePrepareViewModel(
            runner,
            account,
            () => new RemoteControlViewModel(runner, NullLogger<RemoteControlViewModel>.Instance),
            NullLogger<RemotePrepareViewModel>.Instance);
        var developments = new TaskDevelopmentsViewModel(
            runner,
            () => TestDevelopment.Environment(runner, timeProvider: new FakeTimeProvider()),
            NullLogger<TaskDevelopmentsViewModel>.Instance,
            prepare);

        var viewModel = new TaskNotesViewModel(runner, new FakeDirectoryProbe(), developments, NullLogger<TaskNotesViewModel>.Instance);
        viewModel.Load(TaskNotesAliasTests.Row());

        var window = new TaskNotesWindow(viewModel, new FakeConfirmationDialog());
        window.Show();

        viewModel.SelectedTabIndex = TaskNotesViewModel.DevelopmentTab;
        await viewModel.Developments.ActivateAsync(CancellationToken.None);
        Settle(window);

        return (window, prepare);
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

    [AvaloniaFact]
    public async Task WithAnotherMachineThere_TheButtonOpensTheForm_WithTheRepositoryThere()
    {
        var (window, prepare) = await ShowAsync(anyoneThere: true);

        var button = Named<Button>(window, "RemotePrepareButton");
        button.IsEffectivelyVisible.Should().BeTrue();

        button.Command!.Execute(null);
        for (var attempt = 0; attempt < 100 && prepare.SelectedRepository is null; attempt++)
        {
            await Task.Delay(10, TestContext.Current.CancellationToken);
            Settle(window);
        }

        Settle(window);
        Named<ComboBox>(window, "RemoteMachineBox").SelectedItem.Should().Be(prepare.Machines[0]);
        Named<ComboBox>(window, "RemoteRepositoryBox").SelectedItem.Should().Be(prepare.Repositories[0]);
        Named<Button>(window, "RemotePrepareSend").IsEffectivelyVisible.Should().BeTrue();
        button.IsEffectivelyVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task WithNobodyThere_TheButtonDoesNotShow()
    {
        var (window, _) = await ShowAsync(anyoneThere: false);

        Named<StackPanel>(window, "RemotePreparePanel").IsVisible.Should().BeFalse();
    }
}
