namespace MyTaskApp.Desktop.Widget;

/// <summary>
/// As medidas do HUD, em unidades independentes de DPI. A largura é fixa por
/// tamanho; a altura acompanha o conteúdo até o teto — um HUD com uma tarefa
/// não ocupa a altura de dez, e cada pixel que ele não ocupa é clique que
/// chega na janela de trás.
/// </summary>
public static class HudMetrics
{
    /// <summary>Folga até a borda da área de trabalho.</summary>
    public const double ScreenMargin = 12;

    public const double CornerRadius = 10;

    public const double CollapsedWidth = 184;

    public const double CollapsedHeight = 40;

    /// <summary>Abaixo disso o cartão não cabe cabeçalho e uma linha.</summary>
    public const double MinHeight = 72;

    public static double WidthFor(HudSize size) => size switch
    {
        HudSize.Normal => 300,
        HudSize.Expanded => 340,
        _ => 260,
    };

    public static double MaxHeightFor(HudSize size) => size switch
    {
        HudSize.Normal => 400,
        HudSize.Expanded => 600,
        _ => 240,
    };
}
