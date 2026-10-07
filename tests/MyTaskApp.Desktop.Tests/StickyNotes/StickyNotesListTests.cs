using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Desktop.StickyNotes;
using MyTaskApp.Desktop.Tests.ViewModels;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.StickyNotes;

namespace MyTaskApp.Desktop.Tests.StickyNotes;

/// <summary>A lista de post-its (ADR-054): o caminho de volta, e as ações sem tela pesada.</summary>
public class StickyNotesListTests
{
    // 14:00 UTC.
    private static readonly DateTimeOffset Now = TestStickyNotes.Now;

    private readonly FakeUseCaseRunner _runner = new();
    private readonly FakeConfirmationDialog _confirmation = new();
    private readonly FakeTimeProvider _time = new(Now);
    private readonly StickyNoteWindowManager _windows;

    public StickyNotesListTests()
    {
        _time.SetLocalTimeZone(TimeZoneInfo.Utc);
        _windows = new StickyNoteWindowManager(_runner, NullLogger<StickyNoteWindowManager>.Instance);
        _runner.ResultsByHandler[typeof(SetStickyNoteOpenHandler)] = new SetStickyNoteOpenResult(false);
    }

    private StickyNotesViewModel Create() =>
        new(_runner, _windows, _confirmation, _time, NullLogger<StickyNotesViewModel>.Instance);

    private static StickyNoteRow Row(
        string preview,
        DateTimeOffset? updated = null,
        bool pinned = false,
        string? tag = null,
        StickyNoteColor? color = null,
        Guid? converted = null,
        StickyNoteEmphasis emphasis = StickyNoteEmphasis.Normal,
        Guid? id = null) =>
        new(
            id ?? Guid.CreateVersion7(),
            preview,
            tag,
            color ?? StickyNoteColor.Theme,
            emphasis,
            pinned,
            updated ?? Now,
            ArchivedAt: null,
            DeletedAt: null,
            converted);

    private void Lists(params StickyNoteRow[] rows) =>
        _runner.ResultsByHandler[typeof(GetStickyNotesHandler)] = (IReadOnlyList<StickyNoteRow>)rows;

    [Fact]
    public async Task TheList_ShowsTitleColorAndWhatHappened()
    {
        Lists(
            Row("Falar com Marcelo\nsobre o deploy", Now.AddMinutes(-5), pinned: true, tag: "ECO CORE",
                color: new StickyNoteColor(StickyNoteColorMode.Tag, null, "#3B82F6"), emphasis: StickyNoteEmphasis.Attention),
            Row("Ideia de cache", Now.AddHours(-3)),
            Row(string.Empty, Now.AddDays(-1)),
            Row("Virou tarefa", Now.AddDays(-10), converted: Guid.CreateVersion7()));
        var list = Create();

        await list.LoadAsync();

        list.Rows.Select(row => row.Title).Should().Equal("Falar com Marcelo", "Ideia de cache", "Post-it vazio", "Virou tarefa");
        list.Rows[0].Meta.Should().Be("há 5 min · ECO CORE");
        list.Rows[0].HasDot.Should().BeTrue();
        list.Rows[0].IsPinned.Should().BeTrue();
        list.Rows[0].IsAttention.Should().BeTrue();
        list.Rows[0].PinLabel.Should().Be("Soltar da tela");
        list.Rows[1].Meta.Should().Be("hoje 11:00");
        list.Rows[1].HasDot.Should().BeFalse();
        list.Rows[1].PinLabel.Should().Be("Fixar na tela");
        list.Rows[2].Meta.Should().Be("ontem");
        list.Rows[3].Meta.Should().Be($"{Now.AddDays(-10):dd/MM} · virou tarefa");
        list.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task AnEmptyList_SaysHowToCreateOne()
    {
        Lists();
        var list = Create();

        await list.LoadAsync();

        list.IsEmpty.Should().BeTrue();
        list.EmptyMessage.Should().Contain("Ctrl+Shift+N");

        await list.ShowScopeCommand.ExecuteAsync(StickyNoteScope.Trashed);
        list.EmptyMessage.Should().Be("A lixeira está vazia.");
        list.IsTrashScope.Should().BeTrue();

        await list.ShowScopeCommand.ExecuteAsync(StickyNoteScope.Archived);
        list.EmptyMessage.Should().Be("Nenhum post-it arquivado.");
        list.IsArchivedScope.Should().BeTrue();
    }

    [Fact]
    public async Task AFailedLoad_SaysSo()
    {
        _runner.NextFailure = new IOException("travado");
        var list = Create();

        await list.LoadAsync();

        list.ErrorMessage.Should().Be("Não foi possível carregar os post-its.");
    }

    [Fact]
    public async Task ClosedNotes_AreChangedThroughTheUseCases()
    {
        var row = Row("Ideia");
        Lists(row);
        var list = Create();
        await list.LoadAsync();
        var item = list.Rows.Single();

        await list.TogglePinCommand.ExecuteAsync(item);
        await list.ArchiveCommand.ExecuteAsync(item);
        await list.MoveToTrashCommand.ExecuteAsync(item);

        _runner.Invoked.Should().ContainInOrder(
            typeof(PinStickyNoteHandler),
            typeof(ArchiveStickyNoteHandler),
            typeof(MoveStickyNoteToTrashHandler));
    }

    [Fact]
    public async Task Restoring_DependsOnTheTab()
    {
        Lists(Row("Guardado"));
        var list = Create();

        await list.ShowScopeCommand.ExecuteAsync(StickyNoteScope.Archived);
        await list.RestoreCommand.ExecuteAsync(list.Rows.Single());
        await list.ShowScopeCommand.ExecuteAsync(StickyNoteScope.Trashed);
        await list.RestoreCommand.ExecuteAsync(list.Rows.Single());

        _runner.Invoked.Should().Contain(typeof(RestoreStickyNoteHandler))
            .And.Contain(typeof(RestoreStickyNoteFromTrashHandler));
    }

    [Fact]
    public async Task PurgingAsksFirst_AndARefusalDoesNothing()
    {
        Lists(Row("Lixo"));
        var list = Create();
        await list.ShowScopeCommand.ExecuteAsync(StickyNoteScope.Trashed);

        await list.PurgeCommand.ExecuteAsync(list.Rows.Single());

        _confirmation.LastAsked!.IsIrreversible.Should().BeTrue();
        _runner.Invoked.Should().NotContain(typeof(PurgeStickyNoteHandler));

        _confirmation.Answer = true;
        await list.PurgeCommand.ExecuteAsync(list.Rows.Single());

        _runner.Invoked.Should().Contain(typeof(PurgeStickyNoteHandler));
    }

    [Fact]
    public async Task ConvertingAClosedNote_CreatesTheTaskAndSaysSo()
    {
        Lists(Row("Verificar logs"));
        _runner.ResultsByHandler[typeof(ConvertStickyNoteToTaskHandler)] =
            new ConvertStickyNoteToTaskResult(Guid.CreateVersion7(), "Verificar logs", NoteArchived: true);
        var list = Create();
        await list.LoadAsync();
        ConvertStickyNoteToTaskResult? created = null;
        list.TaskCreated += result => created = result;

        await list.ConvertToTaskCommand.ExecuteAsync(list.Rows.Single());

        created!.Title.Should().Be("Verificar logs");
        list.StatusMessage.Should().Be("Post-it convertido em tarefa: Verificar logs");
    }

    [Fact]
    public async Task ARefusedAction_ShowsTheReason()
    {
        Lists(Row("algo"));
        var list = Create();
        await list.LoadAsync();
        _runner.FailuresByHandler[typeof(ArchiveStickyNoteHandler)] = new DomainException("Este post-it já está arquivado.");

        await list.ArchiveCommand.ExecuteAsync(list.Rows.Single());

        list.ErrorMessage.Should().Be("Este post-it já está arquivado.");

        _runner.FailuresByHandler[typeof(ArchiveStickyNoteHandler)] = new IOException("travado");
        await list.ArchiveCommand.ExecuteAsync(list.Rows.Single());

        list.ErrorMessage.Should().Be("Não foi possível concluir. Tente de novo.");
    }

    /// <summary>Um post-it aberto pode ter texto não gravado: a lista passa pela janela dele.</summary>
    [AvaloniaFact]
    public async Task AnOpenNote_IsChangedThroughItsWindow()
    {
        var view = TestStickyNotes.View("Aberto");
        _runner.ResultsByHandler[typeof(CreateStickyNoteHandler)] = view;
        var window = await _windows.CreateNewAsync();
        window!.ViewModel!.Content = "Aberto, com a última frase";
        Lists(Row("Aberto", id: view.Id));
        var list = Create();
        await list.LoadAsync();
        _runner.Invoked.Clear();

        list.Rows.Single().IsOpen.Should().BeTrue();
        list.Rows.Single().Meta.Should().Contain("aberto");

        await list.ArchiveCommand.ExecuteAsync(list.Rows.Single());
        Dispatcher.UIThread.RunJobs();

        // O texto pendente foi gravado antes de arquivar, e a janela fechou.
        _runner.Invoked.Should().ContainInOrder(typeof(EditStickyNoteHandler), typeof(ArchiveStickyNoteHandler));
        _windows.IsOpen(view.Id).Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task OpenAndPin_OnAnOpenNote_GoThroughItsWindow()
    {
        var view = TestStickyNotes.View("Aberto");
        _runner.ResultsByHandler[typeof(CreateStickyNoteHandler)] = view;
        var window = await _windows.CreateNewAsync();
        Lists(Row("Aberto", id: view.Id));
        var list = Create();
        await list.LoadAsync();

        await list.TogglePinCommand.ExecuteAsync(list.Rows.Single());
        await list.OpenCommand.ExecuteAsync(list.Rows.Single());

        window!.ViewModel!.IsPinned.Should().BeTrue();
        window.Topmost.Should().BeTrue();
    }

    /// <summary>Cada pausa da digitação de um post-it avisa a lista; escondida, ela não relê.</summary>
    [AvaloniaFact]
    public async Task AHiddenList_DoesNotRereadOnEveryKeystrokePause()
    {
        var view = TestStickyNotes.View("algo");
        _runner.ResultsByHandler[typeof(CreateStickyNoteHandler)] = view;
        Lists(Row("algo", id: view.Id));
        var list = Create();
        var window = new StickyNotesWindow(list);
        var note = await _windows.CreateNewAsync();
        _runner.Invoked.Clear();

        note!.ViewModel!.Content = "algo mais";
        await note.ViewModel.FlushAsync();

        _runner.Invoked.Should().NotContain(typeof(GetStickyNotesHandler));

        window.Show();
        list.IsShown.Should().BeTrue();
        note.ViewModel.Content = "algo mais, e mais";
        await note.ViewModel.FlushAsync();

        _runner.Invoked.Should().Contain(typeof(GetStickyNotesHandler));
    }

    [AvaloniaFact]
    public async Task TheWindow_DrawsTheRowsAndHidesOnX()
    {
        Lists(Row("Falar com Marcelo"), Row("Ideia de cache"));
        var list = Create();
        var window = new StickyNotesWindow(list);
        window.Show();

        await window.RevealAsync(StickyNoteScope.Active);
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();

        window.GetVisualDescendants().OfType<TextBlock>().Select(block => block.Text)
            .Should().Contain(["Falar com Marcelo", "Ideia de cache"]);

        window.Close();

        // Singleton do contêiner: o X esconde, e a janela continua utilizável.
        window.IsVisible.Should().BeFalse();
        window.Show();
        window.IsVisible.Should().BeTrue();
    }
}
