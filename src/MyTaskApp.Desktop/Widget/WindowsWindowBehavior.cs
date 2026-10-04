using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Microsoft.Extensions.Logging;

namespace MyTaskApp.Desktop.Widget;

/// <summary>
/// A região clicável pelo <c>SetWindowRgn</c> (ADR-048). No Windows o pixel
/// transparente <b>não</b> deixa o clique passar: quem decide é o retângulo da
/// janela. A região encolhe esse retângulo para o desenho do cartão, e o
/// sistema entrega o resto — de outro processo, inclusive — à janela de trás.
/// </summary>
/// <remarks>
/// <c>WS_EX_TRANSPARENT</c> foi descartado: ele torna a janela <b>inteira</b>
/// transparente ao mouse, e alterná-lo pela posição do cursor exigiria um
/// timer lendo o ponteiro global — exatamente o "capturar o mouse" que o HUD
/// não pode fazer. <c>HTTRANSPARENT</c> no <c>WM_NCHITTEST</c> também não
/// serve: só repassa o clique a janelas da mesma thread.
/// </remarks>
internal sealed partial class WindowsWindowBehavior(ILogger<WindowsWindowBehavior> logger)
    : IWindowBehaviorService
{
    /// <summary>O último recorte aplicado: refazer a região com o mesmo tamanho faz a janela piscar.</summary>
    private (nint Handle, PixelRect Rect, int Diameter)? _applied;

    public bool SupportsClickThrough => OperatingSystem.IsWindows();

    public void SetInteractiveRegion(Window window, Rect? region, double cornerRadius)
    {
        if (!OperatingSystem.IsWindows()
            || window.TryGetPlatformHandle() is not { HandleDescriptor: "HWND" } handle)
        {
            return;
        }

        try
        {
            Apply(handle.Handle, region, cornerRadius, window.RenderScaling);
        }
        catch (Exception exception) when (
            exception is DllNotFoundException or EntryPointNotFoundException)
        {
            // Sem recorte o HUD funciona igual; só os cantos deixam de repassar o clique.
            logger.LogWarning(exception, "WindowRegionUnavailable");
        }
    }

    [SupportedOSPlatform("windows")]
    private void Apply(nint hwnd, Rect? region, double cornerRadius, double scaling)
    {
        if (region is not { } rect)
        {
            if (_applied is { } previous && previous.Handle == hwnd)
            {
                SetWindowRgn(hwnd, 0, redraw: true);
            }

            _applied = null;
            return;
        }

        var pixels = new PixelRect(
            (int)Math.Floor(rect.X * scaling),
            (int)Math.Floor(rect.Y * scaling),
            (int)Math.Ceiling(rect.Width * scaling),
            (int)Math.Ceiling(rect.Height * scaling));

        var diameter = (int)Math.Round(2 * cornerRadius * scaling);

        if (_applied == (hwnd, pixels, diameter))
        {
            return;
        }

        // O retângulo do CreateRoundRectRgn exclui a borda direita e a de
        // baixo: passar Right/Bottom cobre exatamente os pixels do cartão.
        var hrgn = CreateRoundRectRgn(pixels.X, pixels.Y, pixels.Right, pixels.Bottom, diameter, diameter);

        if (hrgn == 0)
        {
            logger.LogWarning("WindowRegionNotCreated");
            return;
        }

        // Com sucesso a região passa a ser do sistema; só se apaga a que sobrou.
        if (SetWindowRgn(hwnd, hrgn, redraw: true) == 0)
        {
            DeleteObject(hrgn);
            logger.LogWarning("WindowRegionNotApplied");
            return;
        }

        _applied = (hwnd, pixels, diameter);
    }

    [LibraryImport("gdi32.dll")]
    private static partial nint CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeleteObject(nint handle);

    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(nint hwnd, nint region, [MarshalAs(UnmanagedType.Bool)] bool redraw);
}
