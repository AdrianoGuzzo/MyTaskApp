using Avalonia;

namespace MyTaskApp.Desktop.Widget;

/// <summary>
/// Onde o HUD pousa. Função pura sobre retângulos, como o
/// <see cref="WidgetPlacement"/>: a janela descobre a tela e o DPI, e pergunta
/// aqui. Tudo em pixels físicos — quem converte de DIP é quem chama, com a
/// escala da tela de destino.
/// </summary>
public static class HudPlacement
{
    /// <summary>
    /// O retângulo do HUD na área de trabalho dada. Chamado de novo a cada
    /// mudança de altura: é isso que mantém um HUD ancorado embaixo crescendo
    /// para cima, em vez de escorregar para fora da tela.
    /// </summary>
    /// <param name="custom">O ponto salvo do arrasto; só vale com <see cref="HudPosition.Custom"/>.</param>
    /// <param name="workingAreas">Todas as telas ligadas, para trazer de volta um ponto salvo num monitor que sumiu.</param>
    public static PixelRect Resolve(
        HudPosition position,
        PixelRect workingArea,
        PixelSize size,
        int margin,
        PixelPoint? custom,
        IReadOnlyList<PixelRect> workingAreas)
    {
        var width = Math.Min(size.Width, Math.Max(0, workingArea.Width - (2 * margin)));
        var height = Math.Min(size.Height, Math.Max(0, workingArea.Height - (2 * margin)));

        if (position == HudPosition.Custom && custom is { } point)
        {
            return WidgetPlacement.Clamp(
                new PixelRect(point.X, point.Y, size.Width, size.Height),
                workingAreas.Count > 0 ? workingAreas : [workingArea]);
        }

        var left = workingArea.X + margin;
        var right = workingArea.Right - margin - width;
        var top = workingArea.Y + margin;
        var bottom = workingArea.Bottom - margin - height;
        var middle = workingArea.Y + ((workingArea.Height - height) / 2);

        var (x, y) = position switch
        {
            HudPosition.TopRight => (right, top),
            HudPosition.BottomLeft => (left, bottom),
            HudPosition.BottomRight => (right, bottom),
            HudPosition.CenterLeft => (left, middle),
            HudPosition.CenterRight => (right, middle),

            // Custom sem ponto salvo cai no padrão.
            _ => (left, top),
        };

        return new PixelRect(x, y, width, height);
    }

    /// <summary>A folga da borda na escala da tela: 12 DIP são 18 pixels a 150%.</summary>
    public static int MarginFor(double scaling) =>
        (int)Math.Round(HudMetrics.ScreenMargin * (double.IsFinite(scaling) && scaling > 0 ? scaling : 1));
}
