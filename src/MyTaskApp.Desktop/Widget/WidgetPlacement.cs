using Avalonia;

namespace MyTaskApp.Desktop.Widget;

/// <summary>
/// Onde o painel pode pousar. Função pura sobre retângulos: a janela só
/// descobre as áreas de trabalho e pergunta aqui. Existe separada porque a
/// regra que importa — "o widget nunca abre fora da tela" — precisa de teste, e
/// um teste não tem monitor para desligar.
/// </summary>
public static class WidgetPlacement
{
    /// <summary>
    /// Primeiro uso: perto do relógio, que é onde o usuário já olha para saber
    /// das coisas.
    /// </summary>
    public static PixelRect DefaultCorner(PixelRect workingArea, PixelSize size)
    {
        var width = Math.Min(size.Width, workingArea.Width);
        var height = Math.Min(size.Height, workingArea.Height);

        var desired = new PixelRect(
            workingArea.Right - width - WidgetMetrics.ScreenMargin,
            workingArea.Bottom - height - WidgetMetrics.ScreenMargin,
            width,
            height);

        return Clamp(desired, [workingArea]);
    }

    /// <summary>
    /// Traz o retângulo de volta para dentro de alguma tela ligada agora. Cobre
    /// o caso real: fechar o app no monitor secundário, desconectar, e reabrir
    /// só com o notebook — sem isto o painel volta em coordenadas que não
    /// existem mais e o usuário acha que o app não abriu.
    /// </summary>
    public static PixelRect Clamp(PixelRect desired, IReadOnlyList<PixelRect> workingAreas)
    {
        if (workingAreas.Count == 0)
        {
            return desired;
        }

        var target = BestFit(desired, workingAreas);

        // Encolher antes de mover: numa tela menor que o painel, empurrar sem
        // redimensionar deixaria a borda de baixo inalcançável.
        var width = Math.Min(desired.Width, target.Width);
        var height = Math.Min(desired.Height, target.Height);

        return new PixelRect(
            Math.Clamp(desired.X, target.X, target.Right - width),
            Math.Clamp(desired.Y, target.Y, target.Bottom - height),
            width,
            height);
    }

    /// <summary>A tela que mais cobre o painel; sem interseção, a primeira.</summary>
    private static PixelRect BestFit(PixelRect desired, IReadOnlyList<PixelRect> workingAreas)
    {
        var best = workingAreas[0];
        var bestOverlap = -1L;

        foreach (var area in workingAreas)
        {
            var width = Math.Min(area.Right, desired.Right) - Math.Max(area.X, desired.X);
            var height = Math.Min(area.Bottom, desired.Bottom) - Math.Max(area.Y, desired.Y);
            var overlap = (long)Math.Max(0, width) * Math.Max(0, height);

            if (overlap > bestOverlap)
            {
                bestOverlap = overlap;
                best = area;
            }
        }

        return best;
    }
}
