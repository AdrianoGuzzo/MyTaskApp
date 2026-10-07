using MyTaskApp.Domain;

namespace MyTaskApp.Application.StickyNotes;

/// <summary>A lista de um escopo, para a janela de post-its.</summary>
public sealed record GetStickyNotes(StickyNoteScope Scope);

public sealed class GetStickyNotesHandler(IStickyNoteQuery query)
{
    public Task<IReadOnlyList<StickyNoteRow>> HandleAsync(
        GetStickyNotes command,
        CancellationToken cancellationToken = default) =>
        query.ListAsync(command.Scope, cancellationToken);
}

/// <summary>Um post-it inteiro, para abrir a janela dele.</summary>
public sealed record GetStickyNote(Guid NoteId);

public sealed class GetStickyNoteHandler(IStickyNoteQuery query)
{
    public async Task<StickyNoteView> HandleAsync(
        GetStickyNote command,
        CancellationToken cancellationToken = default) =>
        await query.FindAsync(command.NoteId, cancellationToken)
        ?? throw new DomainException("Post-it não encontrado.");
}

/// <summary>Os post-its que reabrem quando o app sobe: só os fixados (ADR-054).</summary>
public sealed record GetStartupStickyNotes;

public sealed class GetStartupStickyNotesHandler(IStickyNoteQuery query)
{
    public Task<IReadOnlyList<StickyNoteView>> HandleAsync(
        GetStartupStickyNotes command,
        CancellationToken cancellationToken = default) =>
        query.ListStartupAsync(cancellationToken);
}
