namespace MyTaskApp.Desktop.Widget;

/// <summary>
/// Que tipo de janela o app é agora (ADR-048). É o único estado que decide a
/// forma da janela; ficar no topo, opacidade do fundo e região clicável são
/// preferências separadas, e nenhuma delas é deduzida daqui.
/// </summary>
public enum WindowMode
{
    /// <summary>A janela de sempre: arrastável, redimensionável, com os modos do <see cref="WidgetMode"/>.</summary>
    Normal = 0,

    /// <summary>Cartão compacto num canto da tela, só com o que falta fazer.</summary>
    Hud = 1,

    /// <summary>O HUD reduzido a uma pílula; passar o mouse ou clicar devolve o cartão.</summary>
    HudCollapsed = 2,
}
