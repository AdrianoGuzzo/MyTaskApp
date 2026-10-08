using Avalonia;
using MyTaskApp.Desktop.Widget;

namespace MyTaskApp.Desktop.StickyNotes;

/// <summary>
/// Onde um post-it pousa (ADR-054). Função pura sobre retângulos em pixels
/// físicos, como o <see cref="WidgetPlacement"/> — e é ele quem garante a regra
/// que importa: nenhum post-it volta fora da tela.
/// </summary>
public static class StickyNotePlacement
{
    /// <summary>Distância da borda da tela, em DIP.</summary>
    public const double ScreenMargin = 24;

    /// <summary>Quanto cada post-it novo desce e recua em relação ao anterior, em DIP.</summary>
    public const double CascadeStep = 28;

    /// <summary>Depois de tantos, a cascata recomeça do canto em vez de atravessar a tela.</summary>
    public const int CascadeLength = 8;

    /// <summary>
    /// Um post-it novo: no canto superior direito da tela, longe do relógio onde
    /// o widget costuma morar, em cascata para o segundo não cobrir o primeiro.
    /// </summary>
    public static PixelRect ForNew(PixelRect workingArea, PixelSize size, int index, double scaling)
    {
        var margin = (int)Math.Round(ScreenMargin * scaling);
        var step = (int)Math.Round(CascadeStep * scaling) * (Math.Max(0, index) % CascadeLength);

        var desired = new PixelRect(
            workingArea.Right - size.Width - margin - step,
            workingArea.Y + margin + step,
            size.Width,
            size.Height);

        return WidgetPlacement.Clamp(desired, [workingArea]);
    }

    /// <summary>
    /// Um post-it que já tinha lugar. Se o monitor dele sumiu, volta para a tela
    /// que mais o cobre — ou para a primeira, sem interseção nenhuma.
    /// </summary>
    public static PixelRect Restore(PixelPoint position, PixelSize size, IReadOnlyList<PixelRect> workingAreas) =>
        WidgetPlacement.Clamp(new PixelRect(position, size), workingAreas);

    /// <summary>DIP → pixels físicos na escala da tela de destino.</summary>
    public static PixelSize ToPixels(double width, double height, double scaling) =>
        new((int)Math.Round(width * scaling), (int)Math.Round(height * scaling));
}
