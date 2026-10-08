namespace MyTaskApp.Domain.StickyNotes;

/// <summary>
/// Onde o post-it está no ciclo de vida (ADR-054). Mesma derivação do
/// <see cref="Lifecycle.TaskLifecycle"/>: lixeira vence arquivado, e não existe
/// valor para "excluído definitivamente" — isso é a ausência da linha.
/// </summary>
public enum StickyNoteLifecycle
{
    Active = 0,

    Archived = 1,

    Trashed = 2,
}

/// <summary>De onde vem a cor do post-it. Os valores vão para o banco: não reordene.</summary>
public enum StickyNoteColorMode
{
    /// <summary>O cartão do tema, sem matiz nenhuma.</summary>
    Theme = 0,

    /// <summary>Uma das cores de <see cref="StickyNotePaletteColor"/>.</summary>
    Palette = 1,

    /// <summary>A cor da etiqueta, lida a cada carga: recolorir a etiqueta recolore o post-it.</summary>
    Tag = 2,
}

/// <summary>As cores de um clique. Os valores vão para o banco: não reordene.</summary>
public enum StickyNotePaletteColor
{
    Yellow = 1,

    Blue = 2,

    Green = 3,

    Red = 4,

    Purple = 5,

    Orange = 6,

    Gray = 7,
}

/// <summary>Discreto ou chamativo. Os valores vão para o banco: não reordene.</summary>
public enum StickyNoteEmphasis
{
    Normal = 0,

    Attention = 1,
}
