using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Desktop.StickyNotes;
using MyTaskApp.Desktop.Tests.ViewModels;
using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.Tests.StickyNotes;

/// <summary>Uma janela por post-it, nunca duas (ADR-054).</summary>
public class StickyNoteWindowManagerTests
{
    private readonly FakeUseCaseRunner _runner = new();

    private StickyNoteWindowManager Create() =>
        new(_runner, NullLogger<StickyNoteWindowManager>.Instance);

    [AvaloniaFact]
    public async Task NewNote_IsSavedAndOpened()
    {
        var view = TestStickyNotes.View();
        _runner.ResultsByHandler[typeof(CreateStickyNoteHandler)] = view;
        var manager = Create();
        var changed = 0;
        manager.NotesChanged += () => changed++;

        var window = await manager.CreateNewAsync();

        window.Should().NotBeNull();
        window!.IsVisible.Should().BeTrue();
        manager.IsOpen(view.Id).Should().BeTrue();
        _runner.Invoked.Should().Equal(typeof(CreateStickyNoteHandler));
        changed.Should().Be(1);
    }

    [AvaloniaFact]
    public async Task SeveralNotes_EachHaveTheirOwnWindow()
    {
        var manager = Create();

        _runner.ResultsByHandler[typeof(CreateStickyNoteHandler)] = TestStickyNotes.View();
        var first = await manager.CreateNewAsync();
        _runner.ResultsByHandler[typeof(CreateStickyNoteHandler)] = TestStickyNotes.View();
        var second = await manager.CreateNewAsync();

        first.Should().NotBeSameAs(second);
        manager.OpenNoteIds.Should().HaveCount(2);
    }

    [AvaloniaFact]
    public async Task OpeningAnOpenNote_BringsTheSameWindowBack()
    {
        var view = TestStickyNotes.View("algo");
        _runner.ResultsByHandler[typeof(CreateStickyNoteHandler)] = view;
        var manager = Create();
        var created = await manager.CreateNewAsync();

        var opened = await manager.OpenAsync(view.Id);

        opened.Should().BeSameAs(created);
        _runner.Invoked.Should().Equal(typeof(CreateStickyNoteHandler));
    }

    [AvaloniaFact]
    public async Task OpeningAClosedNote_MarksItOpenAndRestoresIt()
    {
        var view = TestStickyNotes.View("Falar com Marcelo", pinned: true, x: 300, y: 200, width: 320, height: 240);
        _runner.ResultsByHandler[typeof(SetStickyNoteOpenHandler)] = new SetStickyNoteOpenResult(false);
        _runner.ResultsByHandler[typeof(GetStickyNoteHandler)] = view;
        var manager = Create();

        var window = await manager.OpenAsync(view.Id);
        Dispatcher.UIThread.RunJobs();

        _runner.Invoked.Should().Equal(typeof(SetStickyNoteOpenHandler), typeof(GetStickyNoteHandler));
        window!.Topmost.Should().BeTrue();
        window.Width.Should().Be(320);
        window.Height.Should().Be(240);
    }

    [AvaloniaFact]
    public async Task ClosingTheWindow_LetsItBeOpenedAgain()
    {
        var view = TestStickyNotes.View("algo");
        _runner.ResultsByHandler[typeof(CreateStickyNoteHandler)] = view;
        _runner.ResultsByHandler[typeof(SetStickyNoteOpenHandler)] = new SetStickyNoteOpenResult(false);
        var manager = Create();
        var window = await manager.CreateNewAsync();

        await window!.ViewModel!.CloseCommand.ExecuteAsync(null);

        manager.IsOpen(view.Id).Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task AtStartup_OnlyTheReturnedNotesOpen_WithoutStealingFocus()
    {
        var pinned = TestStickyNotes.View("fixado", pinned: true);
        _runner.ResultsByHandler[typeof(GetStartupStickyNotesHandler)] = (IReadOnlyList<StickyNoteView>)[pinned];
        var manager = Create();

        await manager.OpenStartupNotesAsync();

        manager.OpenNoteIds.Should().Equal(pinned.Id);
        manager.Find(pinned.Id)!.ShowActivated.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Dismissing_ClosesWithoutSavingAnything()
    {
        var view = TestStickyNotes.View("algo");
        _runner.ResultsByHandler[typeof(CreateStickyNoteHandler)] = view;
        var manager = Create();
        await manager.CreateNewAsync();
        _runner.Invoked.Clear();

        manager.Dismiss(view.Id);

        manager.IsOpen(view.Id).Should().BeFalse();
        _runner.Invoked.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task AFailure_IsReportedInsteadOfOpeningAnEmptyWindow()
    {
        _runner.NextFailure = new DomainException("Post-it não encontrado.");
        var manager = Create();
        string? failure = null;
        manager.Failed += message => failure = message;

        var window = await manager.OpenAsync(Guid.CreateVersion7());

        window.Should().BeNull();
        failure.Should().Be("Post-it não encontrado.");
        manager.OpenNoteIds.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task AnUnexpectedFailure_IsReportedGenerically()
    {
        _runner.NextFailure = new IOException("travado");
        var manager = Create();
        string? failure = null;
        manager.Failed += message => failure = message;

        (await manager.CreateNewAsync()).Should().BeNull();

        failure.Should().Be("Não foi possível abrir o post-it.");
    }

    [AvaloniaFact]
    public async Task CtrlShiftNInsideANote_OpensAnotherOne()
    {
        _runner.ResultsByHandler[typeof(CreateStickyNoteHandler)] = TestStickyNotes.View();
        var manager = Create();
        var first = await manager.CreateNewAsync();
        _runner.ResultsByHandler[typeof(CreateStickyNoteHandler)] = TestStickyNotes.View();

        first!.ViewModel!.NewNoteCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        manager.OpenNoteIds.Should().HaveCount(2);
    }
}
