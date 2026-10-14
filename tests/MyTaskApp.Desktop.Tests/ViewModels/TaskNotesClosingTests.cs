using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Tags;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// O "X" da janela da tarefa, na janela de verdade (ADR-027, 031, 051, 060):
/// o que ocupa um ambiente segura a janela e explica, a preparação é cancelada,
/// e só o texto não salvo pergunta antes de fechar.
/// </summary>
public class TaskNotesClosingTests
{
    private const string Repository = @"C:\Projects\ecossistema-core";

    private static readonly DateTimeOffset At = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private sealed record Shown(TaskNotesWindow Window, TaskNotesViewModel ViewModel, FakeConfirmationDialog Confirmation)
    {
        public bool Closed { get; set; }

        public TaskDevelopmentViewModel Environment => ViewModel.Developments.Items.Single();

        public void TryClose()
        {
            Window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private static async Task<Shown> ShowAsync()
    {
        var runner = new FakeUseCaseRunner();
        runner.ResultsByHandler[typeof(GetTaskDirectoriesHandler)] = TaskNotesAliasTests.Directories;
        runner.ResultsByHandler[typeof(GetDevelopmentCommandsHandler)] = (IReadOnlyList<DevelopmentCommandRow>)[];
        runner.Enqueue<GetTaskDevelopmentsHandler>(TestDevelopment.List(new TaskDevelopmentView(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Repository, "origin/main", "feature/x", Repository + "-feature-x",
            TaskDevelopmentStatus.Ready, At, null)));
        runner.ResultsByHandler[typeof(DetectGitHandler)] = new GitInstallation(true, "2.51.0", "git");
        runner.ResultsByHandler[typeof(InspectDirectoryHandler)] = new DirectoryInspection(true, true, Repository);

        var viewModel = new TaskNotesViewModel(
            runner,
            new FakeDirectoryProbe(),
            TestDevelopment.For(runner, timeProvider: new FakeTimeProvider()),
            NullLogger<TaskNotesViewModel>.Instance);

        viewModel.Load(TaskNotesAliasTests.Row());

        var confirmation = new FakeConfirmationDialog();
        var window = new TaskNotesWindow(viewModel, confirmation);
        window.Show();
        await viewModel.Developments.ActivateAsync(CancellationToken.None);
        Dispatcher.UIThread.RunJobs();

        var shown = new Shown(window, viewModel, confirmation);
        window.Closed += (_, _) => shown.Closed = true;
        viewModel.SelectedTabIndex = 0;

        return shown;
    }

    private static void ShouldHoldAndShow(Shown shown, string message)
    {
        shown.Closed.Should().BeFalse();
        shown.ViewModel.SelectedTabIndex.Should().Be(TaskNotesViewModel.DevelopmentTab);
        shown.ViewModel.Developments.Selected.Should().BeSameAs(shown.Environment);
        shown.Environment.Message.Should().Be(message);
    }

    [AvaloniaFact]
    public async Task NothingGoingOn_Closes()
    {
        var shown = await ShowAsync();

        shown.TryClose();

        shown.Closed.Should().BeTrue();
        shown.Confirmation.Asked.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task TheWorktreeBeingCreated_HoldsTheWindow_AndSaysWhy()
    {
        var shown = await ShowAsync();
        shown.Environment.State = DevelopmentPanelState.Running;
        shown.Environment.CanCancel = false;

        shown.TryClose();

        ShouldHoldAndShow(shown, "Aguarde a criação do worktree terminar para fechar.");
    }

    [AvaloniaFact]
    public async Task ThePreparation_IsCancelled_AndTheWindowStays()
    {
        var shown = await ShowAsync();
        shown.Environment.State = DevelopmentPanelState.Running;
        shown.Environment.CanCancel = true;
        var before = shown.Environment.Message;

        shown.TryClose();

        shown.Closed.Should().BeFalse();
        shown.Environment.Message.Should().Be(before);
        shown.ViewModel.SelectedTabIndex.Should().Be(0, "a preparação só é cancelada; a aba não muda");
    }

    [AvaloniaFact]
    public async Task ADatabaseCopy_HoldsTheWindow_AndSaysWhy()
    {
        var shown = await ShowAsync();
        shown.Environment.Database.IsRunning = true;

        shown.TryClose();

        ShouldHoldAndShow(shown, "Cópia do banco em andamento. Aguarde terminar, ou clique em Cancelar cópia, para fechar.");
    }

    [AvaloniaFact]
    public async Task AQuickCommand_IsCancelled_AndTheNextCloseLeaves()
    {
        var shown = await ShowAsync();
        shown.Environment.QuickCommands.IsRunning = true;

        shown.TryClose();

        ShouldHoldAndShow(shown, "Comando cancelado. Feche de novo para sair.");
    }

    [AvaloniaFact]
    public async Task ThePostWorktreeCommands_AreCancelled_AndTheNextCloseLeaves()
    {
        var shown = await ShowAsync();
        shown.Environment.Commands.IsRunning = true;

        shown.TryClose();

        ShouldHoldAndShow(shown, "Execução dos comandos cancelada. Feche de novo para sair.");
    }

    /// <summary>A decisão é do ViewModel e se testa sem janela.</summary>
    [AvaloniaFact]
    public async Task TheDecision_IsTheViewModels()
    {
        var shown = await ShowAsync();

        shown.ViewModel.RequestClose().Should().Be(TaskNotesCloseDecision.Close);

        shown.ViewModel.Text = "rascunho";
        shown.ViewModel.RequestClose().Should().Be(TaskNotesCloseDecision.AskToDiscard);

        shown.Environment.Commands.IsRunning = true;
        shown.ViewModel.RequestClose().Should().Be(TaskNotesCloseDecision.Stay, "ambiente ocupado vem antes do texto não salvo");
    }

    [AvaloniaFact]
    public async Task UnsavedText_AsksBeforeClosing()
    {
        var shown = await ShowAsync();
        shown.ViewModel.Text = "uma anotação que ninguém salvou";

        shown.TryClose();

        shown.Closed.Should().BeFalse();
        shown.Confirmation.LastAsked!.Headline.Should().Be("Descartar as alterações?");
    }
}
