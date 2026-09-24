using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Media;
using Avalonia.Media.Immutable;

namespace MyTaskApp.Desktop.SpellChecking;

/// <summary>
/// O sublinhado ondulado das palavras erradas, desenhado por cima do
/// <see cref="TextPresenter"/> da caixa (ADR-032).
/// </summary>
/// <remarks>
/// A <see cref="TextBox"/> não tem decoração por trecho de texto, e trocar o
/// template de todas as caixas para enfiar uma camada dentro seria caro de
/// manter. Um adorner fica na camada acima da janela, acompanha o presenter
/// quando ele rola e é recortado pela área visível da caixa — e as
/// coordenadas são as do <see cref="TextPresenter.TextLayout"/>, as mesmas do
/// cursor.
/// </remarks>
internal sealed class SpellingSquiggles : Control
{
    private const double WaveLength = 4;

    private const double WaveHeight = 1.5;

    private static readonly IPen Pen = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromRgb(0xF0, 0x5A, 0x5A)), 1);

    private readonly TextPresenter _presenter;

    private IReadOnlyList<WordSpan> _words = [];

    public SpellingSquiggles(TextPresenter presenter)
    {
        _presenter = presenter;
        IsHitTestVisible = false;
    }

    public IReadOnlyList<WordSpan> Words
    {
        get => _words;
        set
        {
            _words = value;
            InvalidateVisual();
        }
    }

    /// <summary>Os trechos sublinhados, em coordenadas do presenter — o que o teste confere.</summary>
    public IEnumerable<Rect> Underlines()
    {
        var length = _presenter.Text?.Length ?? 0;

        foreach (var word in _words)
        {
            // Entre a tecla e a próxima verificação o texto pode ter
            // encolhido; palavra fora dele não se desenha.
            if (word.End > length)
            {
                continue;
            }

            foreach (var rect in _presenter.TextLayout.HitTestTextRange(word.Start, word.Length))
            {
                if (rect.Width > 0)
                {
                    yield return rect;
                }
            }
        }
    }

    public override void Render(DrawingContext context)
    {
        foreach (var rect in Underlines())
        {
            context.DrawGeometry(null, Pen, Wave(rect.Left, rect.Right, rect.Bottom - WaveHeight));
        }
    }

    private static StreamGeometry Wave(double left, double right, double baseline)
    {
        var geometry = new StreamGeometry();

        using (var path = geometry.Open())
        {
            path.BeginFigure(new Point(left, baseline), isFilled: false);

            var up = true;

            for (var x = left + (WaveLength / 2); x <= right; x += WaveLength / 2)
            {
                path.LineTo(new Point(x, baseline + (up ? -WaveHeight : WaveHeight) / 2));
                up = !up;
            }

            path.EndFigure(isClosed: false);
        }

        return geometry;
    }
}
