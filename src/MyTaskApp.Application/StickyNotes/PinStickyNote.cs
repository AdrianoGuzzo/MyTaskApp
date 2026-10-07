using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.StickyNotes;

/// <summary>Sempre no topo, só deste post-it. Não mexe em posição nem tamanho.</summary>
public sealed record PinStickyNote(Guid NoteId, bool Pinned);

public sealed class PinStickyNoteHandler(
    IStickyNoteRepository notes,
    IUnitOfWork unitOfWork,
    ILogger<PinStickyNoteHandler> logger)
{
    public async Task HandleAsync(
        PinStickyNote command,
        CancellationToken cancellationToken = default)
    {
        var note = await notes.GetByIdAsync(command.NoteId, cancellationToken);

        note.Pin(command.Pinned);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("StickyNotePinned {NoteId} {Pinned}", note.Id, command.Pinned);
    }
}
