using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.StickyNotes;
using MyTaskApp.Domain.StickyNotes;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Infrastructure.Persistence.Queries;

/// <summary>
/// As leituras do post-it (ADR-054), com a etiqueta juntada na própria consulta:
/// nome e cor moram só em <c>Tags</c>, então recolorir a etiqueta aparece no
/// post-it na próxima leitura, sem sincronizar cópia nenhuma.
/// </summary>
internal sealed class StickyNoteQuery(MyTaskAppDbContext context) : IStickyNoteQuery
{
    /// <remarks>
    /// A lista leva só o começo do texto, cortado no SQL (<c>substr</c>): dezenas
    /// de linhas não precisam de 20 mil caracteres cada para se desenhar.
    /// </remarks>
    public async Task<IReadOnlyList<StickyNoteRow>> ListAsync(
        StickyNoteScope scope,
        CancellationToken cancellationToken = default)
    {
        var notes = context.StickyNotes.AsNoTracking();

        notes = scope switch
        {
            StickyNoteScope.Archived => notes
                .Where(note => note.DeletedAt == null && note.ArchivedAt != null)
                .OrderByDescending(note => note.ArchivedAt),
            StickyNoteScope.Trashed => notes
                .Where(note => note.DeletedAt != null)
                .OrderByDescending(note => note.DeletedAt),
            _ => notes
                .Where(note => note.DeletedAt == null && note.ArchivedAt == null)
                .OrderByDescending(note => note.UpdatedAt),
        };

        return await WithTag(notes)
            .Select(row => new StickyNoteRow(
                row.Note.Id,
                row.Note.Content.Substring(0, StickyNoteRow.PreviewLength),
                row.Tag == null ? null : row.Tag.Name,
                new StickyNoteColor(
                    row.Note.ColorMode,
                    row.Note.PaletteColor,
                    row.Tag == null ? null : row.Tag.ColorHex),
                row.Note.Emphasis,
                row.Note.IsPinned,
                row.Note.UpdatedAt,
                row.Note.ArchivedAt,
                row.Note.DeletedAt,
                row.Note.ConvertedTaskId))
            .ToListAsync(cancellationToken);
    }

    public async Task<StickyNoteView?> FindAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var rows = await Views(context.StickyNotes.AsNoTracking().Where(note => note.Id == id))
            .ToListAsync(cancellationToken);

        return rows.SingleOrDefault();
    }

    public async Task<IReadOnlyList<StickyNoteView>> ListStartupAsync(CancellationToken cancellationToken = default) =>
        await Views(context.StickyNotes
                .AsNoTracking()
                .Where(note =>
                    note.IsOpen
                    && note.IsPinned
                    && note.ArchivedAt == null
                    && note.DeletedAt == null)
                .OrderBy(note => note.CreatedAt))
            .ToListAsync(cancellationToken);

    /// <summary>
    /// O agregado vem inteiro e vira DTO na projeção final — o único ponto em
    /// que o EF avalia no cliente, e que por isso não vira consulta por linha.
    /// </summary>
    private IQueryable<StickyNoteView> Views(IQueryable<StickyNote> notes) =>
        WithTag(notes).Select(row => StickyNoteView.From(
            row.Note,
            row.Tag == null ? null : row.Tag.Name,
            row.Tag == null ? null : row.Tag.ColorHex));

    private IQueryable<NoteWithTag> WithTag(IQueryable<StickyNote> notes) =>
        notes.LeftJoin(
            context.Tags.AsNoTracking(),
            note => note.TagId,
            tag => (Guid?)tag.Id,
            (note, tag) => new NoteWithTag { Note = note, Tag = tag });

    /// <summary>
    /// Inicialização por membro, e não por construtor: é a forma que o EF sabe
    /// compor depois — um <c>Where</c>/<c>Select</c> sobre <c>row.Note</c>.
    /// </summary>
    private sealed class NoteWithTag
    {
        public required StickyNote Note { get; init; }

        public Tag? Tag { get; init; }
    }
}
