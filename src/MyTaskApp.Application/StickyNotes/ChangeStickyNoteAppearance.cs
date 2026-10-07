using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.StickyNotes;

namespace MyTaskApp.Application.StickyNotes;

/// <summary>
/// Etiqueta, cor, destaque e opacidade — o estado final inteiro, como o
/// <c>SetTaskTags</c>: repetir a chamada não muda nada, e duas mudanças rápidas
/// não se misturam pela metade.
/// </summary>
public sealed record ChangeStickyNoteAppearance(
    Guid NoteId,
    Guid? TagId,
    StickyNoteColorMode ColorMode,
    StickyNotePaletteColor? PaletteColor,
    StickyNoteEmphasis Emphasis,
    int Opacity);

public sealed class ChangeStickyNoteAppearanceHandler(
    IStickyNoteRepository notes,
    ITagRepository tags,
    IUnitOfWork unitOfWork,
    IStickyNoteQuery query,
    ILogger<ChangeStickyNoteAppearanceHandler> logger)
{
    /// <summary>Devolve o post-it relido, com nome e cor da etiqueta para a tela desenhar.</summary>
    public async Task<StickyNoteView> HandleAsync(
        ChangeStickyNoteAppearance command,
        CancellationToken cancellationToken = default)
    {
        var note = await notes.GetByIdAsync(command.NoteId, cancellationToken);

        // Etiqueta excluída em outra janela vira a mensagem certa, não erro de FK.
        await tags.GetExistingAsync(command.TagId is { } tagId ? [tagId] : [], cancellationToken);

        note.ChangeAppearance(
            command.TagId,
            command.ColorMode,
            command.PaletteColor,
            command.Emphasis,
            command.Opacity);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "StickyNoteAppearanceChanged {NoteId} {ColorMode} {Emphasis}",
            note.Id,
            note.ColorMode,
            note.Emphasis);

        return await query.FindAsync(note.Id, cancellationToken)
            ?? StickyNoteView.From(note, tagName: null, tagColorHex: null);
    }
}
