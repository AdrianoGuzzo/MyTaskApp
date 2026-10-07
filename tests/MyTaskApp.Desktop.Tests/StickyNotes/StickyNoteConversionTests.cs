using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Desktop.Tests.ViewModels;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.Tests.StickyNotes;

/// <summary>"⋯ → Transformar em tarefa", e pronto (ADR-054).</summary>
public class StickyNoteConversionTests
{
    private static readonly Guid TaskId = Guid.CreateVersion7();

    private readonly FakeUseCaseRunner _runner = new();

    private StickyNoteViewModel Create(string content = "Verificar performance do dashboard") =>
        new(TestStickyNotes.View(content), _runner, NullLogger.Instance);

    private void Converts(bool archived, string title = "Verificar performance do dashboard") =>
        _runner.ResultsByHandler[typeof(ConvertStickyNoteToTaskHandler)] =
            new ConvertStickyNoteToTaskResult(TaskId, title, archived);

    [Fact]
    public async Task TheWholeNote_BecomesATaskAndTheNoteLeavesTheScreen()
    {
        Converts(archived: true);
        var viewModel = Create();
        ConvertStickyNoteToTaskResult? created = null;
        var closed = false;
        viewModel.TaskCreated += result => created = result;
        viewModel.CloseRequested += () => closed = true;
        viewModel.Content = "Verificar performance do dashboard\no gráfico demora";

        await viewModel.ConvertToTaskCommand.ExecuteAsync(null);

        // A última palavra digitada vai junto: grava antes de converter.
        _runner.Invoked.Should().Equal(typeof(EditStickyNoteHandler), typeof(ConvertStickyNoteToTaskHandler));
        created!.TaskId.Should().Be(TaskId);
        viewModel.StatusMessage.Should().Be("Post-it convertido em tarefa.");
        closed.Should().BeTrue();
    }

    [Fact]
    public async Task ASelection_BecomesATaskAndTheNoteStays()
    {
        Converts(archived: false, title: "Perguntar sobre homologação.");
        var viewModel = Create("Falar com Marcelo.\nPerguntar sobre homologação.");
        var closed = false;
        viewModel.CloseRequested += () => closed = true;

        viewModel.ConvertSelectionCommand.CanExecute(null).Should().BeFalse();

        viewModel.Selection = "Perguntar sobre homologação.";
        viewModel.ConvertSelectionCommand.CanExecute(null).Should().BeTrue();

        await viewModel.ConvertSelectionCommand.ExecuteAsync(null);

        viewModel.StatusMessage.Should().Be("Tarefa criada: Perguntar sobre homologação.");
        closed.Should().BeFalse();
    }

    [Fact]
    public async Task ARefusedConversion_KeepsTheNoteAndSaysWhy()
    {
        _runner.NextFailure = new DomainException("O post-it está vazio. Escreva algo antes de transformá-lo em tarefa.");
        var viewModel = Create();
        var closed = false;
        viewModel.CloseRequested += () => closed = true;

        await viewModel.ConvertToTaskCommand.ExecuteAsync(null);

        closed.Should().BeFalse();
        viewModel.HasStatus.Should().BeFalse();
        viewModel.ErrorMessage.Should().Contain("vazio");
    }

    [AvaloniaFact]
    public async Task TheWindow_ShowsTheFarewellBeforeClosing()
    {
        Converts(archived: true);
        var viewModel = Create();
        var window = new StickyNoteWindow(viewModel, anchor: null, cascadeIndex: 0);
        window.Show();
        Dispatcher.UIThread.RunJobs();

        await viewModel.ConvertToTaskCommand.ExecuteAsync(null);
        window.UpdateLayout();

        window.IsClosingSoon.Should().BeTrue();
        window.IsVisible.Should().BeTrue();
        window.GetVisualDescendants().OfType<Border>().Single(border => border.Name == "StatusBanner")
            .IsVisible.Should().BeTrue();
    }

    [AvaloniaFact]
    public void SelectingText_FeedsTheSelectionCommand()
    {
        var viewModel = Create("Falar com Marcelo.\nPerguntar sobre homologação.");
        var window = new StickyNoteWindow(viewModel, anchor: null, cascadeIndex: 0);
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var editor = window.GetVisualDescendants().OfType<TextBox>().Single(box => box.Name == "Editor");

        window.FocusEditor();
        editor.SelectionStart = 0;
        editor.SelectionEnd = "Falar com Marcelo.".Length;

        viewModel.Selection.Should().Be("Falar com Marcelo.");
        viewModel.ConvertSelectionCommand.CanExecute(null).Should().BeTrue();
    }
}
