using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.StickyNotes;

/// <summary>Abrir ou fechar a janela do post-it. Fechar é esconder, nunca excluir.</summary>
public sealed record SetStickyNoteOpen(Guid NoteId, bool Open);

/// <param name="Discarded">
/// O post-it estava em branco e foi apagado ao fechar: "Novo Post-it" e logo X
/// não deixa uma linha vazia na lista.
/// </param>
public sealed record SetStickyNoteOpenResult(bool Discarded);

public sealed class SetStickyNoteOpenHandler(
    IStickyNoteRepository notes,
    IUnitOfWork unitOfWork,
    ILogger<SetStickyNoteOpenHandler> logger)
{
    public async Task<SetStickyNoteOpenResult> HandleAsync(
        SetStickyNoteOpen command,
        CancellationToken cancellationToken = default)
    {
        var note = await notes.GetByIdAsync(command.NoteId, cancellationToken);

        if (command.Open)
        {
            note.Open();
        }
        else if (note.IsBlank && !note.IsOutOfTheMainList)
        {
            note.EnsurePermanentDeletionIsAllowed();
            notes.Remove(note);

            await unitOfWork.SaveChangesAsync(cancellationToken);

            logger.LogInformation("StickyNoteDiscardedBlank {NoteId}", note.Id);

            return new SetStickyNoteOpenResult(Discarded: true);
        }
        else
        {
            note.Close();
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogDebug("StickyNoteOpenChanged {NoteId} {Open}", note.Id, command.Open);

        return new SetStickyNoteOpenResult(Discarded: false);
    }
}
