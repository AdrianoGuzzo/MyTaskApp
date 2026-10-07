using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.StickyNotes;

/// <summary>
/// O texto do post-it, gravado sozinho enquanto se escreve — sem botão Salvar.
/// Quem chama espera uma pausa na digitação; aqui chega o texto inteiro.
/// </summary>
public sealed record EditStickyNote(Guid NoteId, string? Content);

public sealed class EditStickyNoteHandler(
    IStickyNoteRepository notes,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<EditStickyNoteHandler> logger)
{
    public async Task HandleAsync(
        EditStickyNote command,
        CancellationToken cancellationToken = default)
    {
        var note = await notes.GetByIdAsync(command.NoteId, cancellationToken);

        note.Edit(command.Content, timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Debug: a cada pausa da digitação, um Information afogaria o log.
        logger.LogDebug("StickyNoteEdited {NoteId} {Length}", note.Id, note.Content.Length);
    }
}
