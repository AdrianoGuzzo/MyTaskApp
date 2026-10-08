namespace MyTaskApp.Domain.StickyNotes;

/// <summary>
/// A matiz de cada cor da paleta. São as mesmas de <see cref="Tags.TagColor.Palette"/>:
/// um post-it "azul" e uma etiqueta azul são o mesmo azul. A matiz é só a
/// semente — quem decide o fundo de verdade, com contraste medido, é a tela, em
/// cima do tema ativo (ADR-054).
/// </summary>
public static class StickyNotePalette
{
    public static IReadOnlyList<StickyNotePaletteColor> All { get; } =
    [
        StickyNotePaletteColor.Yellow,
        StickyNotePaletteColor.Blue,
        StickyNotePaletteColor.Green,
        StickyNotePaletteColor.Red,
        StickyNotePaletteColor.Purple,
        StickyNotePaletteColor.Orange,
        StickyNotePaletteColor.Gray,
    ];

    public static string HexOf(StickyNotePaletteColor color) => color switch
    {
        StickyNotePaletteColor.Yellow => "#EAB308",
        StickyNotePaletteColor.Blue => "#3B82F6",
        StickyNotePaletteColor.Green => "#22C55E",
        StickyNotePaletteColor.Red => "#EF4444",
        StickyNotePaletteColor.Purple => "#8B5CF6",
        StickyNotePaletteColor.Orange => "#F97316",
        StickyNotePaletteColor.Gray => "#94A3B8",
        _ => throw new DomainException("Cor de post-it desconhecida."),
    };

    public static string NameOf(StickyNotePaletteColor color) => color switch
    {
        StickyNotePaletteColor.Yellow => "Amarelo",
        StickyNotePaletteColor.Blue => "Azul",
        StickyNotePaletteColor.Green => "Verde",
        StickyNotePaletteColor.Red => "Vermelho",
        StickyNotePaletteColor.Purple => "Roxo",
        StickyNotePaletteColor.Orange => "Laranja",
        StickyNotePaletteColor.Gray => "Cinza",
        _ => throw new DomainException("Cor de post-it desconhecida."),
    };
}
