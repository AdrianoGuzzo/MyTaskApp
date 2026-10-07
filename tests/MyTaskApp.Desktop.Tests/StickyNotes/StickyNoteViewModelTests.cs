using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Desktop.Tests.ViewModels;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.Tests.StickyNotes;

/// <summary>
/// O post-it aberto (ADR-054): grava sozinho, fecha sem perder nada e nunca
/// apaga o que estava sendo escrito.
/// </summary>
public class StickyNoteViewModelTests
{
    private readonly FakeUseCaseRunner _runner = new();

    public StickyNoteViewModelTests() =>
        _runner.ResultsByHandler[typeof(SetStickyNoteOpenHandler)] = new SetStickyNoteOpenResult(Discarded: false);

    private StickyNoteViewModel Create(string content = "", bool pinned = false) =>
        new(TestStickyNotes.View(content, pinned), _runner, NullLogger.Instance);

    [Fact]
    public void TheWindowTitle_IsTheFirstLine()
    {
        var viewModel = Create("\n  Falar com Marcelo  \nsobre o deploy");

        viewModel.WindowTitle.Should().Be("Post-it — Falar com Marcelo");

        viewModel.Content = string.Empty;

        viewModel.WindowTitle.Should().Be("Post-it");
    }

    [Fact]
    public async Task Flushing_SavesOnlyWhatChanged()
    {
        var viewModel = Create("antes");

        await viewModel.FlushAsync();
        _runner.Invoked.Should().BeEmpty();

        viewModel.Content = "depois";
        viewModel.HasUnsavedText.Should().BeTrue();

        await viewModel.FlushAsync();
        await viewModel.FlushAsync();

        _runner.Invoked.Should().Equal(typeof(EditStickyNoteHandler));
        viewModel.HasUnsavedText.Should().BeFalse();
    }

    [Fact]
    public async Task AFailedSave_KeepsTheTextAndSaysSo()
    {
        var viewModel = Create();
        viewModel.Content = "importante";
        _runner.NextFailure = new IOException("disco cheio");

        await viewModel.FlushAsync();

        viewModel.HasUnsavedText.Should().BeTrue();
        viewModel.ErrorMessage.Should().Be("Não foi possível salvar o post-it. Ele continua aberto.");

        await viewModel.FlushAsync();

        viewModel.HasUnsavedText.Should().BeFalse();
        viewModel.HasError.Should().BeFalse();
    }

    [Fact]
    public async Task Pinning_ChangesAtOnceAndIsSaved()
    {
        var viewModel = Create();
        var changed = 0;
        viewModel.Changed += () => changed++;

        await viewModel.TogglePinCommand.ExecuteAsync(null);

        viewModel.IsPinned.Should().BeTrue();
        viewModel.PinGlyph.Should().Be("");
        viewModel.PinTip.Should().Contain("soltar");
        _runner.Invoked.Should().Equal(typeof(PinStickyNoteHandler));
        changed.Should().Be(1);

        await viewModel.TogglePinCommand.ExecuteAsync(null);

        viewModel.IsPinned.Should().BeFalse();
        viewModel.PinGlyph.Should().Be("");
    }

    [Fact]
    public async Task APinThatFailsToSave_IsUndone()
    {
        var viewModel = Create();
        _runner.NextFailure = new DomainException("Não é possível fixar um post-it arquivado. Restaure-o primeiro.");

        await viewModel.TogglePinCommand.ExecuteAsync(null);

        viewModel.IsPinned.Should().BeFalse();
        viewModel.ErrorMessage.Should().Contain("arquivado");
    }

    [Fact]
    public async Task Closing_SavesTextAndPlaceBeforeSayingTheWindowClosed()
    {
        var viewModel = Create("rascunho");
        var closed = false;
        viewModel.CloseRequested += () => closed = true;
        viewModel.Content = "rascunho final";
        viewModel.TrackGeometry(new StickyNoteGeometry(10, 20, 300, 220));

        await viewModel.CloseCommand.ExecuteAsync(null);

        _runner.Invoked.Should().Equal(
            typeof(EditStickyNoteHandler),
            typeof(PlaceStickyNoteHandler),
            typeof(SetStickyNoteOpenHandler));
        closed.Should().BeTrue();
    }

    [Fact]
    public async Task Closing_NeverDeletes()
    {
        var viewModel = Create("algo");

        await viewModel.CloseCommand.ExecuteAsync(null);

        _runner.Invoked.Should().NotContain(typeof(PurgeStickyNoteHandler))
            .And.NotContain(typeof(MoveStickyNoteToTrashHandler));
    }

    [Fact]
    public async Task IfTheTextCannotBeSaved_TheWindowStaysOpen()
    {
        var viewModel = Create();
        var closed = false;
        viewModel.CloseRequested += () => closed = true;
        viewModel.Content = "não pode sumir";
        _runner.FailuresByHandler[typeof(EditStickyNoteHandler)] = new IOException("travado");

        await viewModel.CloseCommand.ExecuteAsync(null);

        closed.Should().BeFalse();
        _runner.Invoked.Should().NotContain(typeof(SetStickyNoteOpenHandler));
        viewModel.HasError.Should().BeTrue();
    }

    [Fact]
    public async Task ClosingTwice_ClosesOnce()
    {
        var viewModel = Create("algo");
        var closed = 0;
        viewModel.CloseRequested += () => closed++;

        await viewModel.CloseCommand.ExecuteAsync(null);
        await viewModel.CloseCommand.ExecuteAsync(null);

        closed.Should().Be(1);
        _runner.Invoked.Count(type => type == typeof(SetStickyNoteOpenHandler)).Should().Be(1);
    }

    [Fact]
    public async Task ArchivingAndTrashing_SaveFirstAndCloseTheWindow()
    {
        var archived = Create("guardar");
        var archivedClosed = false;
        archived.CloseRequested += () => archivedClosed = true;
        archived.Content = "guardar isto";

        await archived.ArchiveCommand.ExecuteAsync(null);

        _runner.Invoked.Should().Equal(typeof(EditStickyNoteHandler), typeof(ArchiveStickyNoteHandler));
        archivedClosed.Should().BeTrue();

        _runner.Invoked.Clear();
        var trashed = Create("jogar fora");
        var trashedClosed = false;
        trashed.CloseRequested += () => trashedClosed = true;

        await trashed.MoveToTrashCommand.ExecuteAsync(null);

        _runner.Invoked.Should().Equal(typeof(MoveStickyNoteToTrashHandler));
        trashedClosed.Should().BeTrue();
    }

    [Fact]
    public async Task ARefusedArchive_KeepsTheWindowOpen()
    {
        var viewModel = Create("algo");
        var closed = false;
        viewModel.CloseRequested += () => closed = true;
        _runner.FailuresByHandler[typeof(ArchiveStickyNoteHandler)] = new DomainException("Este post-it já está arquivado.");

        await viewModel.ArchiveCommand.ExecuteAsync(null);

        closed.Should().BeFalse();
        viewModel.ErrorMessage.Should().Be("Este post-it já está arquivado.");
    }

    [Fact]
    public async Task APlacementThatFails_IsQuietAndNotRetried()
    {
        var viewModel = Create();
        viewModel.TrackGeometry(new StickyNoteGeometry(1, 2, 300, 200));
        _runner.NextFailure = new IOException("travado");

        await viewModel.SaveGeometryAsync();
        await viewModel.SaveGeometryAsync();

        viewModel.HasError.Should().BeFalse();
        _runner.Invoked.Should().Equal(typeof(PlaceStickyNoteHandler));
    }

    [Fact]
    public void CtrlShiftN_AsksForAnotherNote()
    {
        var viewModel = Create();
        var asked = false;
        viewModel.NewNoteRequested += () => asked = true;

        viewModel.NewNoteCommand.Execute(null);

        asked.Should().BeTrue();
    }
}
