namespace MyTaskApp.Desktop.Widget;

/// <summary>Onde o HUD pousa na tela em que a janela está.</summary>
public enum HudPosition
{
    TopLeft = 0,
    TopRight = 1,
    BottomLeft = 2,
    BottomRight = 3,
    CenterLeft = 4,
    CenterRight = 5,

    /// <summary>Onde o usuário arrastou o HUD pela última vez.</summary>
    Custom = 6,
}

/// <summary>Três degraus de densidade do HUD — ver <see cref="HudMetrics"/>.</summary>
public enum HudSize
{
    Compact = 0,
    Normal = 1,
    Expanded = 2,
}

/// <summary>
/// As preferências do HUD, aninhadas no <see cref="WidgetState"/> para o
/// <c>widget.json</c> mostrar o que é de quem.
/// </summary>
public sealed record HudSettings
{
    public const double MinOpacity = 0.7;

    public const double DefaultOpacity = 0.92;

    public static readonly HudSettings Default = new();

    public HudPosition Position { get; init; } = HudPosition.TopLeft;

    public HudSize Size { get; init; } = HudSize.Compact;

    /// <summary>
    /// Opacidade do <b>fundo</b> do cartão, nunca do texto. O piso existe
    /// porque o problema que o HUD resolve é justamente a janela que some.
    /// </summary>
    public double Opacity { get; init; } = DefaultOpacity;

    /// <summary>"Manter o HUD sempre visível". Independe do "sempre no topo" da janela normal.</summary>
    public bool AlwaysOnTop { get; init; } = true;

    /// <summary>O HUD vive como pílula e abre ao passar o mouse.</summary>
    public bool UseCollapsed { get; init; }

    /// <summary>Canto superior esquerdo em pixels físicos, quando <see cref="Position"/> é <see cref="HudPosition.Custom"/>.</summary>
    public int? X { get; init; }

    public int? Y { get; init; }

    /// <summary>A explicação da primeira vez já foi confirmada com "Entendi".</summary>
    public bool IntroSeen { get; init; }

    /// <summary>Arquivo editado à mão não pode deixar o HUD invisível nem fora do catálogo.</summary>
    public HudSettings Sanitized() => this with
    {
        Position = Enum.IsDefined(Position) ? Position : HudPosition.TopLeft,
        Size = Enum.IsDefined(Size) ? Size : HudSize.Compact,
        Opacity = ClampOpacity(Opacity),
    };

    public static double ClampOpacity(double opacity) =>
        double.IsFinite(opacity) ? Math.Clamp(opacity, MinOpacity, 1) : DefaultOpacity;
}
