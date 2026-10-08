using MyTaskApp.Domain.StickyNotes;

namespace MyTaskApp.Application.StickyNotes;

/// <summary>As três listas da janela de post-its, na mesma derivação do agregado.</summary>
public enum StickyNoteScope
{
    Active = 0,

    Archived = 1,

    Trashed = 2,
}

/// <summary>
/// A cor de um post-it como a tela precisa dela: o modo escolhido e, já
/// resolvida, a matiz de onde o desenho parte. A cor da etiqueta é lida a cada
/// carga — recolorir a etiqueta recolore o post-it sem sincronizar cópia nenhuma.
/// </summary>
public sealed record StickyNoteColor(
    StickyNoteColorMode Mode,
    StickyNotePaletteColor? PaletteColor,
    string? TagColorHex)
{
    public static StickyNoteColor Theme { get; } = new(StickyNoteColorMode.Theme, null, null);

    /// <summary>"Cor da etiqueta" sem etiqueta — excluída — volta ao tema.</summary>
    public StickyNoteColorMode EffectiveMode =>
        Mode == StickyNoteColorMode.Tag && TagColorHex is null ? StickyNoteColorMode.Theme
        : Mode == StickyNoteColorMode.Palette && PaletteColor is null ? StickyNoteColorMode.Theme
        : Mode;

    /// <summary>A matiz da cor, ou nula quando o post-it segue o tema.</summary>
    public string? HueHex => EffectiveMode switch
    {
        StickyNoteColorMode.Palette => StickyNotePalette.HexOf(PaletteColor!.Value),
        StickyNoteColorMode.Tag => TagColorHex,
        _ => null,
    };
}

/// <summary>Um post-it inteiro, como a janela dele o desenha.</summary>
public sealed record StickyNoteView(
    Guid Id,
    string Content,
    Guid? TagId,
    string? TagName,
    StickyNoteColor Color,
    StickyNoteEmphasis Emphasis,
    int Opacity,
    bool IsOpen,
    bool IsPinned,
    int? X,
    int? Y,
    double Width,
    double Height,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset? DeletedAt)
{
    public StickyNoteLifecycle Lifecycle =>
        DeletedAt is not null ? StickyNoteLifecycle.Trashed
        : ArchivedAt is not null ? StickyNoteLifecycle.Archived
        : StickyNoteLifecycle.Active;

    /// <summary>O agregado como a tela o vê, com a etiqueta já juntada.</summary>
    public static StickyNoteView From(StickyNote note, string? tagName, string? tagColorHex) =>
        new(
            note.Id,
            note.Content,
            note.TagId,
            note.TagId is null ? null : tagName,
            new StickyNoteColor(note.ColorMode, note.PaletteColor, note.TagId is null ? null : tagColorHex),
            note.Emphasis,
            note.Opacity,
            note.IsOpen,
            note.IsPinned,
            note.X,
            note.Y,
            note.Width,
            note.Height,
            note.CreatedAt,
            note.UpdatedAt,
            note.ArchivedAt,
            note.DeletedAt);
}

/// <summary>
/// Um post-it na lista: só o começo do texto. A lista pode ter dezenas de
/// linhas, e nenhuma delas precisa do texto inteiro para se desenhar.
/// </summary>
/// <param name="Preview">O começo do texto, cortado no banco (<see cref="PreviewLength"/>).</param>
public sealed record StickyNoteRow(
    Guid Id,
    string Preview,
    string? TagName,
    StickyNoteColor Color,
    StickyNoteEmphasis Emphasis,
    bool IsPinned,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset? DeletedAt,
    Guid? ConvertedTaskId)
{
    public const int PreviewLength = 160;

    /// <summary>A primeira linha com texto — o que identifica o post-it numa lista.</summary>
    public string Title => StickyNoteText.FirstLine(Preview) ?? "Post-it vazio";

    public bool IsBlank => string.IsNullOrWhiteSpace(Preview);
}

public interface IStickyNoteQuery
{
    /// <summary>Os post-its de um escopo, do mexido mais recentemente ao mais antigo.</summary>
    Task<IReadOnlyList<StickyNoteRow>> ListAsync(
        StickyNoteScope scope,
        CancellationToken cancellationToken = default);

    Task<StickyNoteView?> FindAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Os que reabrem sozinhos quando o app sobe: ativos, abertos e fixados.
    /// Um post-it solto que ficou aberto não volta — ele continua na lista, e a
    /// área de trabalho de quem abre o app não se enche sozinha (ADR-054).
    /// </summary>
    Task<IReadOnlyList<StickyNoteView>> ListStartupAsync(CancellationToken cancellationToken = default);
}
