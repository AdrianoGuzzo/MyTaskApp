using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.StickyNotes;

/// <summary>
/// Onde a janela do post-it está: canto superior esquerdo em pixels físicos e
/// tamanho em DIP — a mesma convenção do <c>widget.json</c> (ADR-017).
/// </summary>
public sealed record PlaceStickyNote(Guid NoteId, int? X, int? Y, double Width, double Height);

public sealed class PlaceStickyNoteHandler(
    IStickyNoteRepository notes,
    IUnitOfWork unitOfWork,
    ILogger<PlaceStickyNoteHandler> logger)
{
    public async Task HandleAsync(
        PlaceStickyNote command,
        CancellationToken cancellationToken = default)
    {
        // Sem "não encontrado": a gravação atrasada de um arrasto pode chegar
        // depois de o post-it em branco ter sido descartado ao fechar, e isso
        // não é erro que alguém precise ler.
        if (await notes.FindByIdAsync(command.NoteId, cancellationToken) is not { } note)
        {
            logger.LogDebug("StickyNotePlacementSkipped {NoteId}", command.NoteId);
            return;
        }

        note.Place(command.X, command.Y, command.Width, command.Height);

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
