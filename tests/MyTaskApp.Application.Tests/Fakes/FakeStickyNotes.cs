using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Domain.StickyNotes;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>Post-its em memória, com a unidade de trabalho junto, como os outros fakes.</summary>
internal sealed class FakeStickyNoteRepository : IStickyNoteRepository, IUnitOfWork
{
    private readonly Dictionary<Guid, StickyNote> _notes = [];

    public IReadOnlyCollection<StickyNote> Notes => _notes.Values;

    public int SaveCount { get; private set; }

    public void Seed(params StickyNote[] notes)
    {
        foreach (var note in notes)
        {
            _notes[note.Id] = note;
        }
    }

    public Task AddAsync(StickyNote note, CancellationToken cancellationToken = default)
    {
        _notes[note.Id] = note;
        return Task.CompletedTask;
    }

    public Task<StickyNote?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_notes.GetValueOrDefault(id));

    public void Remove(StickyNote note) => _notes.Remove(note.Id);

    public Task SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        SaveCount++;
        return Task.CompletedTask;
    }
}

/// <summary>A consulta lida direto do repositório falso, com a etiqueta juntada como o banco faz.</summary>
internal sealed class FakeStickyNoteQuery(
    FakeStickyNoteRepository notes,
    FakeTagRepository? tags = null) : IStickyNoteQuery
{
    public Task<IReadOnlyList<StickyNoteRow>> ListAsync(
        StickyNoteScope scope,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StickyNoteRow>>(
        [
            .. notes.Notes
                .Where(note => (int)note.Lifecycle == (int)scope)
                .OrderByDescending(note => note.UpdatedAt)
                .Select(note =>
                {
                    var view = View(note);
                    return new StickyNoteRow(
                        note.Id,
                        note.Content,
                        view.TagName,
                        view.Color,
                        note.Emphasis,
                        note.IsPinned,
                        note.UpdatedAt,
                        note.ArchivedAt,
                        note.DeletedAt,
                        note.ConvertedTaskId);
                }),
        ]);

    public Task<StickyNoteView?> FindAsync(Guid id, CancellationToken cancellationToken = default) =>
        Task.FromResult(notes.Notes.FirstOrDefault(note => note.Id == id) is { } note ? View(note) : null);

    public Task<IReadOnlyList<StickyNoteView>> ListStartupAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<StickyNoteView>>(
        [
            .. notes.Notes
                .Where(note => note.Lifecycle == StickyNoteLifecycle.Active && note.IsOpen && note.IsPinned)
                .Select(View),
        ]);

    private StickyNoteView View(StickyNote note)
    {
        var tag = note.TagId is { } tagId ? tags?.Tags.FirstOrDefault(t => t.Id == tagId) : null;

        return StickyNoteView.From(note, tag?.Name, tag?.ColorHex);
    }
}
