using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.StickyNotes;

namespace MyTaskApp.Application.StickyNotes;

/// <summary>
/// Um post-it novo, em branco e aberto (ADR-054). Sem título, sem etiqueta, sem
/// pergunta nenhuma: a captura tem de custar menos do que o pensamento.
/// </summary>
public sealed record CreateStickyNote;

public sealed class CreateStickyNoteHandler(
    IStickyNoteRepository notes,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<CreateStickyNoteHandler> logger)
{
    public async Task<StickyNoteView> HandleAsync(
        CreateStickyNote command,
        CancellationToken cancellationToken = default)
    {
        var note = StickyNote.Create(timeProvider.GetUtcNow());

        await notes.AddAsync(note, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("StickyNoteCreated {NoteId}", note.Id);

        return StickyNoteView.From(note, tagName: null, tagColorHex: null);
    }
}
