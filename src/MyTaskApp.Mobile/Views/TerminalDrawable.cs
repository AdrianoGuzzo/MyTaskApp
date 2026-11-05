using MyTaskApp.Application.Terminals;
using Font = Microsoft.Maui.Graphics.Font;

namespace MyTaskApp.Mobile.Views;

/// <summary>
/// Desenha a <see cref="VtScreen"/> do terminal do Claude no celular (ADR-101),
/// como o <c>TerminalView</c> do desktop: as cores de <see cref="TerminalColors"/>,
/// o fundo de cada trecho, negrito, inverso, sublinhado e o cursor. Cada glifo
/// vai na sua célula, para uma fonte de reserva (as caixas, os emojis) não
/// empurrar o resto da linha.
/// </summary>
/// <remarks>Só roda na thread da tela, como quem alimenta a tela.</remarks>
internal sealed class TerminalDrawable : IDrawable
{
    /// <summary>A fonte de largura fixa de cada sistema.</summary>
    private static readonly string Family = DeviceInfo.Platform == DevicePlatform.iOS ? "Menlo" : "monospace";

    private static readonly Font Regular = new(Family);

    private static readonly Font Bold = new(Family, FontWeights.Bold);

    private double _measuredFor = -1;

    public VtScreen? Screen { get; set; }

    public float FontSize { get; set; } = 11;

    /// <summary>Linhas do histórico acima da tela; 0 é a tela de agora.</summary>
    public int ScrollOffset { get; set; }

    /// <summary>Com a vez de digitar, o cursor aparece.</summary>
    public bool ShowCursor { get; set; }

    /// <summary>A largura de uma célula, medida na fonte de agora; antes do primeiro desenho, a aproximação.</summary>
    public float CellWidth { get; private set; } = 11 * 0.6f;

    public float CellHeight { get; private set; } = 11 * 1.25f;

    /// <summary>Quantas colunas e linhas cabem numa área, na fonte de agora.</summary>
    public TerminalSize SizeFor(double width, double height) =>
        new(Math.Max(20, (int)(width / CellWidth)), Math.Max(5, (int)(height / CellHeight)));

    public void Draw(ICanvas canvas, RectF dirtyRect)
    {
        Measure(canvas);
        canvas.FillColor = ColorOf(TerminalColors.Background);
        canvas.FillRectangle(dirtyRect);

        if (Screen is not { } screen)
        {
            return;
        }

        var offset = Math.Min(ScrollOffset, screen.ScrollbackCount);
        var top = screen.ScrollbackCount - offset;

        for (var row = 0; row < screen.Size.Rows; row++)
        {
            var line = top + row;
            var cells = line < screen.ScrollbackCount ? screen.ScrollbackRow(line) : screen.Row(line - screen.ScrollbackCount);
            DrawLine(canvas, cells, row * CellHeight);
        }

        if (offset == 0 && ShowCursor && screen.CursorVisible)
        {
            var foreground = ColorOf(TerminalColors.Foreground);
            canvas.FillColor = foreground.WithAlpha(0.6f);
            canvas.FillRectangle(screen.CursorColumn * CellWidth, screen.CursorRow * CellHeight, CellWidth, CellHeight);
        }
    }

    private void Measure(ICanvas canvas)
    {
        if (Math.Abs(_measuredFor - FontSize) < 0.01)
        {
            return;
        }

        canvas.Font = Regular;
        canvas.FontSize = FontSize;
        var size = canvas.GetStringSize("MMMMMMMMMM", Regular, FontSize);
        CellWidth = size.Width > 0 ? size.Width / 10 : FontSize * 0.6f;
        CellHeight = MathF.Ceiling(size.Height > 0 ? size.Height * 1.15f : FontSize * 1.25f);
        _measuredFor = FontSize;
    }

    private void DrawLine(ICanvas canvas, IReadOnlyList<TerminalCell> cells, float y)
    {
        for (var column = 0; column < cells.Count; column++)
        {
            var cell = cells[column];

            if (cell.IsContinuation)
            {
                continue;
            }

            var style = cell.Style;
            var foreground = TerminalColors.Resolve(style.Foreground, foreground: true);
            var background = TerminalColors.Resolve(style.Background, foreground: false);

            if (style.Flags.HasFlag(TerminalCellFlags.Inverse))
            {
                (foreground, background) = (background, foreground);
            }

            var width = Math.Max(1, (int)cell.Width) * CellWidth;
            var x = column * CellWidth;

            if (background != TerminalColors.Background)
            {
                canvas.FillColor = ColorOf(background);
                canvas.FillRectangle(x, y, width, CellHeight);
            }

            if (cell.Text is " " or "" || style.Flags.HasFlag(TerminalCellFlags.Hidden))
            {
                continue;
            }

            var color = ColorOf(foreground);

            if (style.Flags.HasFlag(TerminalCellFlags.Dim))
            {
                color = color.WithAlpha(0.6f);
            }

            var font = style.Flags.HasFlag(TerminalCellFlags.Bold) ? Bold : Regular;
            canvas.Font = font;
            canvas.FontSize = FontSize;
            canvas.FontColor = color;
            canvas.DrawString(cell.Text, x, y, width + CellWidth, CellHeight, HorizontalAlignment.Left, VerticalAlignment.Center);

            if (style.Flags.HasFlag(TerminalCellFlags.Underline))
            {
                canvas.StrokeColor = color;
                canvas.StrokeSize = 1;
                canvas.DrawLine(x, y + CellHeight - 1, x + width, y + CellHeight - 1);
            }

            if (style.Flags.HasFlag(TerminalCellFlags.Strikethrough))
            {
                canvas.StrokeColor = color;
                canvas.StrokeSize = 1;
                canvas.DrawLine(x, y + (CellHeight / 2), x + width, y + (CellHeight / 2));
            }
        }
    }

    private static Color ColorOf(TerminalRgb rgb) => Color.FromRgb(rgb.Red, rgb.Green, rgb.Blue);
}
