using MyTaskApp.Domain;
using MyTaskApp.Domain.StickyNotes;

namespace MyTaskApp.Application.Abstractions;

/// <summary>Acesso ao agregado <see cref="StickyNote"/>. Estreita por necessidade (ADR-005).</summary>
public interface IStickyNoteRepository
{
    Task AddAsync(StickyNote note, CancellationToken cancellationToken = default);

    Task<StickyNote?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    void Remove(StickyNote note);
}

internal static class StickyNoteRepositoryExtensions
{
    /// <summary>Carrega o post-it por id, ou falha com mensagem exibível.</summary>
    public static async Task<StickyNote> GetByIdAsync(
        this IStickyNoteRepository notes,
        Guid noteId,
        CancellationToken cancellationToken) =>
        await notes.FindByIdAsync(noteId, cancellationToken)
        ?? throw new DomainException("Post-it não encontrado.");
}
