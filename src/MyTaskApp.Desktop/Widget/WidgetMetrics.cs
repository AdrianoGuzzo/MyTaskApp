namespace MyTaskApp.Desktop.Widget;

/// <summary>
/// As medidas do painel em um só lugar. A janela e os testes leem daqui, senão
/// "quanto mede o modo recolhido" vira três respostas diferentes.
/// </summary>
public static class WidgetMetrics
{
    public const double DefaultWidth = 360;
    public const double DefaultHeight = 560;

    /// <summary>Abaixo disso a linha da tarefa começa a cortar o título.</summary>
    public const double MinWidth = 288;

    public const double MinExpandedHeight = 320;

    public const double CompactHeight = 420;

    /// <summary>A pílula: largura fixa, altura de uma linha.</summary>
    public const double CollapsedWidth = 268;

    public const double CollapsedHeight = 66;

    /// <summary>Folga entre o painel e a borda da tela no primeiro uso.</summary>
    public const int ScreenMargin = 24;
}
