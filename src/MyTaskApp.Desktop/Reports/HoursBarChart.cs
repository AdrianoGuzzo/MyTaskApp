using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Media;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Desktop.Reports;

/// <summary>
/// As barras do relatório de horas (ADR-097), desenhadas aqui, sem pacote de
/// gráfico. Cada cor é uma propriedade que o XAML liga a um
/// <c>DynamicResource Widget…Brush</c>: trocar o tema troca o pincel, e o
/// gráfico se redesenha — o mesmo caminho do <c>MarkdownView</c>.
/// </summary>
/// <remarks>
/// O clique vale para a coluna inteira, e não só para a barra: o dia sem horas
/// também se escolhe. A tabela ao lado é a leitura acessível completa; aqui o
/// teclado anda pelos blocos (←/→, Home, End), Enter desce um nível e Esc
/// desfaz a escolha.
/// </remarks>
public sealed class HoursBarChart : Control
{
    public static readonly StyledProperty<IReadOnlyList<HoursBucket>?> ItemsProperty =
        AvaloniaProperty.Register<HoursBarChart, IReadOnlyList<HoursBucket>?>(nameof(Items));

    public static readonly StyledProperty<TimeSpan> AverageProperty =
        AvaloniaProperty.Register<HoursBarChart, TimeSpan>(nameof(Average));

    public static readonly StyledProperty<int> SelectedIndexProperty =
        AvaloniaProperty.Register<HoursBarChart, int>(nameof(SelectedIndex), -1, defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<int> HoveredIndexProperty =
        AvaloniaProperty.Register<HoursBarChart, int>(nameof(HoveredIndex), -1);

    /// <summary>Duplo clique ou Enter num bloco: recebe o índice dele.</summary>
    public static readonly StyledProperty<ICommand?> ActivateCommandProperty =
        AvaloniaProperty.Register<HoursBarChart, ICommand?>(nameof(ActivateCommand));

    public static readonly StyledProperty<IBrush?> BarBrushProperty =
        AvaloniaProperty.Register<HoursBarChart, IBrush?>(nameof(BarBrush));

    public static readonly StyledProperty<IBrush?> BarHoverBrushProperty =
        AvaloniaProperty.Register<HoursBarChart, IBrush?>(nameof(BarHoverBrush));

    public static readonly StyledProperty<IBrush?> SelectedBarBrushProperty =
        AvaloniaProperty.Register<HoursBarChart, IBrush?>(nameof(SelectedBarBrush));

    public static readonly StyledProperty<IBrush?> GridBrushProperty =
        AvaloniaProperty.Register<HoursBarChart, IBrush?>(nameof(GridBrush));

    public static readonly StyledProperty<IBrush?> AxisTextBrushProperty =
        AvaloniaProperty.Register<HoursBarChart, IBrush?>(nameof(AxisTextBrush));

    public static readonly StyledProperty<IBrush?> CurrentTextBrushProperty =
        AvaloniaProperty.Register<HoursBarChart, IBrush?>(nameof(CurrentTextBrush));

    public static readonly StyledProperty<IBrush?> AverageBrushProperty =
        AvaloniaProperty.Register<HoursBarChart, IBrush?>(nameof(AverageBrush));

    public static readonly StyledProperty<IBrush?> WeekendBrushProperty =
        AvaloniaProperty.Register<HoursBarChart, IBrush?>(nameof(WeekendBrush));

    public static readonly StyledProperty<IBrush?> TipBackgroundBrushProperty =
        AvaloniaProperty.Register<HoursBarChart, IBrush?>(nameof(TipBackgroundBrush));

    public static readonly StyledProperty<IBrush?> TipBorderBrushProperty =
        AvaloniaProperty.Register<HoursBarChart, IBrush?>(nameof(TipBorderBrush));

    public static readonly StyledProperty<IBrush?> TipTextBrushProperty =
        AvaloniaProperty.Register<HoursBarChart, IBrush?>(nameof(TipTextBrush));

    public static readonly StyledProperty<IBrush?> FocusBrushProperty =
        AvaloniaProperty.Register<HoursBarChart, IBrush?>(nameof(FocusBrush));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<HoursBarChart>();

    /// <summary>A margem do eixo de horas, à esquerda.</summary>
    internal const double AxisWidth = 48;

    /// <summary>Espaço acima das barras: o valor da mais alta e o balão.</summary>
    internal const double TopRoom = 22;

    /// <summary>Os rótulos dos blocos, embaixo.</summary>
    internal const double LabelRoom = 22;

    internal const double RightRoom = 8;

    private const double TextSize = 11;

    static HoursBarChart()
    {
        AffectsRender<HoursBarChart>(
            ItemsProperty,
            AverageProperty,
            SelectedIndexProperty,
            HoveredIndexProperty,
            BarBrushProperty,
            BarHoverBrushProperty,
            SelectedBarBrushProperty,
            GridBrushProperty,
            AxisTextBrushProperty,
            CurrentTextBrushProperty,
            AverageBrushProperty,
            WeekendBrushProperty,
            TipBackgroundBrushProperty,
            TipBorderBrushProperty,
            TipTextBrushProperty,
            FocusBrushProperty,
            FontFamilyProperty,
            IsFocusedProperty);

        FocusableProperty.OverrideDefaultValue<HoursBarChart>(true);
    }

    public HoursBarChart()
    {
        ClipToBounds = true;
    }

    public IReadOnlyList<HoursBucket>? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    /// <summary>A linha tracejada de média; zero, ela some.</summary>
    public TimeSpan Average
    {
        get => GetValue(AverageProperty);
        set => SetValue(AverageProperty, value);
    }

    public int SelectedIndex
    {
        get => GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    /// <summary>O bloco sob o mouse; -1 fora. Só sai do controle (OneWayToSource).</summary>
    public int HoveredIndex
    {
        get => GetValue(HoveredIndexProperty);
        set => SetValue(HoveredIndexProperty, value);
    }

    public ICommand? ActivateCommand
    {
        get => GetValue(ActivateCommandProperty);
        set => SetValue(ActivateCommandProperty, value);
    }

    public IBrush? BarBrush
    {
        get => GetValue(BarBrushProperty);
        set => SetValue(BarBrushProperty, value);
    }

    public IBrush? BarHoverBrush
    {
        get => GetValue(BarHoverBrushProperty);
        set => SetValue(BarHoverBrushProperty, value);
    }

    public IBrush? SelectedBarBrush
    {
        get => GetValue(SelectedBarBrushProperty);
        set => SetValue(SelectedBarBrushProperty, value);
    }

    public IBrush? GridBrush
    {
        get => GetValue(GridBrushProperty);
        set => SetValue(GridBrushProperty, value);
    }

    public IBrush? AxisTextBrush
    {
        get => GetValue(AxisTextBrushProperty);
        set => SetValue(AxisTextBrushProperty, value);
    }

    /// <summary>O rótulo do bloco de hoje, deste mês ou deste ano.</summary>
    public IBrush? CurrentTextBrush
    {
        get => GetValue(CurrentTextBrushProperty);
        set => SetValue(CurrentTextBrushProperty, value);
    }

    public IBrush? AverageBrush
    {
        get => GetValue(AverageBrushProperty);
        set => SetValue(AverageBrushProperty, value);
    }

    /// <summary>O fundo de sábado e domingo, na escala de dias.</summary>
    public IBrush? WeekendBrush
    {
        get => GetValue(WeekendBrushProperty);
        set => SetValue(WeekendBrushProperty, value);
    }

    public IBrush? TipBackgroundBrush
    {
        get => GetValue(TipBackgroundBrushProperty);
        set => SetValue(TipBackgroundBrushProperty, value);
    }

    public IBrush? TipBorderBrush
    {
        get => GetValue(TipBorderBrushProperty);
        set => SetValue(TipBorderBrushProperty, value);
    }

    public IBrush? TipTextBrush
    {
        get => GetValue(TipTextBrushProperty);
        set => SetValue(TipTextBrushProperty, value);
    }

    public IBrush? FocusBrush
    {
        get => GetValue(FocusBrushProperty);
        set => SetValue(FocusBrushProperty, value);
    }

    public FontFamily FontFamily
    {
        get => GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    private int Count => Items?.Count ?? 0;

    /// <summary>A área das barras, sem os eixos.</summary>
    internal Rect Plot => new(
        AxisWidth,
        TopRoom,
        Math.Max(0, Bounds.Width - AxisWidth - RightRoom),
        Math.Max(0, Bounds.Height - TopRoom - LabelRoom));

    private double Band => Count == 0 ? 0 : Plot.Width / Count;

    /// <summary>O bloco na coluna do ponto — a coluna inteira, para o dia sem horas também responder.</summary>
    internal int IndexAt(Point point)
    {
        var plot = Plot;

        if (Count == 0 || point.X < plot.Left || point.X >= plot.Right || point.Y < 0 || point.Y > Bounds.Height)
        {
            return -1;
        }

        return Math.Clamp((int)((point.X - plot.Left) / Band), 0, Count - 1);
    }

    /// <summary>A barra desenhada do bloco, em coordenadas do controle; vazia sem horas.</summary>
    internal Rect BarRect(int index)
    {
        var items = Items;

        if (items is null || index < 0 || index >= items.Count || items[index].IsFuture || items[index].Total <= TimeSpan.Zero)
        {
            return default;
        }

        var plot = Plot;
        var (top, _) = Scale(items);
        var band = Band;
        var width = Math.Max(2, Math.Min(band * 0.68, 56));
        var height = Math.Max(2, items[index].Total.TotalHours / top * plot.Height);

        return new Rect(plot.Left + (index * band) + ((band - width) / 2), plot.Bottom - height, width, height);
    }

    protected override Size MeasureOverride(Size availableSize) => new(
        double.IsInfinity(availableSize.Width) ? 600 : availableSize.Width,
        double.IsInfinity(availableSize.Height) ? 260 : availableSize.Height);

    public override void Render(DrawingContext context)
    {
        var items = Items;
        var plot = Plot;

        if (plot.Width <= 0 || plot.Height <= 0)
        {
            return;
        }

        var (top, step) = Scale(items ?? []);
        var band = Band;
        double Y(double hours) => plot.Bottom - (hours / top * plot.Height);

        // Fim de semana: uma faixa de fundo, atrás de tudo.
        if (items is not null && WeekendBrush is { } weekend)
        {
            for (var index = 0; index < items.Count; index++)
            {
                if (items[index].IsWeekend)
                {
                    context.FillRectangle(weekend, new Rect(plot.Left + (index * band), plot.Top, band, plot.Height));
                }
            }
        }

        // Grade e rótulos do eixo de horas.
        var gridPen = GridBrush is { } grid ? new Pen(grid, 1) : null;

        for (var hours = 0d; hours <= top + 0.0001; hours += step)
        {
            var y = Math.Round(Y(hours)) + 0.5;

            if (gridPen is not null)
            {
                context.DrawLine(gridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            }

            var label = Text(hours <= 0 ? "0h" : WorkTimeFormatter.Duration(TimeSpan.FromHours(hours)), AxisTextBrush);

            if (label is not null)
            {
                context.DrawText(label, new Point(plot.Left - 6 - label.Width, y - (label.Height / 2)));
            }
        }

        if (items is null || items.Count == 0)
        {
            return;
        }

        var showValues = items.Count <= 12;
        var choosing = SelectedIndex >= 0 && SelectedIndex < items.Count;

        for (var index = 0; index < items.Count; index++)
        {
            var bar = BarRect(index);

            if (bar.Width <= 0)
            {
                continue;
            }

            var brush = index == SelectedIndex ? SelectedBarBrush ?? BarBrush
                : index == HoveredIndex ? BarHoverBrush ?? BarBrush
                : BarBrush;

            // Com um bloco escolhido, os outros recuam: a escolha se vê de longe.
            using (context.PushOpacity(choosing && index != SelectedIndex && index != HoveredIndex ? 0.45 : 1))
            {
                if (brush is not null)
                {
                    context.DrawRectangle(brush, null, new RoundedRect(bar, 3, 3, 0, 0));
                }

                if (showValues && Text(Compact(items[index].Total), AxisTextBrush) is { } value && value.Width <= band - 2)
                {
                    context.DrawText(value, new Point(bar.Center.X - (value.Width / 2), bar.Top - value.Height - 2));
                }
            }
        }

        // A média, tracejada, com o valor na ponta direita.
        if (Average > TimeSpan.Zero && AverageBrush is { } averageBrush)
        {
            var y = Math.Round(Y(Average.TotalHours)) + 0.5;
            context.DrawLine(new Pen(averageBrush, 1, DashStyle.Dash), new Point(plot.Left, y), new Point(plot.Right, y));

            if (Text($"média {WorkTimeFormatter.Duration(Average)}", averageBrush) is { } label)
            {
                var spot = new Rect(plot.Right - label.Width - 6, y - label.Height - 3, label.Width + 4, label.Height);

                if (TipBackgroundBrush is { } back)
                {
                    context.FillRectangle(back, spot);
                }

                context.DrawText(label, new Point(spot.X + 2, spot.Y));
            }
        }

        DrawLabels(context, items, plot, band);

        // O anel do foco do teclado, em volta da coluna escolhida.
        if (IsFocused && SelectedIndex >= 0 && SelectedIndex < items.Count && FocusBrush is { } focus)
        {
            context.DrawRectangle(null, new Pen(focus, 1.5), new Rect(plot.Left + (SelectedIndex * band) + 1, plot.Top, band - 2, plot.Height + LabelRoom - 2), 3, 3);
        }

        if (HoveredIndex >= 0 && HoveredIndex < items.Count)
        {
            DrawTip(context, items[HoveredIndex], HoveredIndex, plot, band);
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var index = IndexAt(e.GetPosition(this));

        if (index != HoveredIndex)
        {
            SetCurrentValue(HoveredIndexProperty, index);
            Cursor = index >= 0 && Items![index].IsFuture is false ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        SetCurrentValue(HoveredIndexProperty, -1);
        Cursor = Cursor.Default;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        var index = IndexAt(e.GetPosition(this));

        if (index < 0 || Items![index].IsFuture)
        {
            return;
        }

        Focus();
        e.Handled = true;

        if (e.ClickCount >= 2)
        {
            Activate(index);
            return;
        }

        // Clicar de novo no escolhido desfaz: as horas por tarefa voltam ao período inteiro.
        SetCurrentValue(SelectedIndexProperty, index == SelectedIndex ? -1 : index);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (Items is not { Count: > 0 } items)
        {
            return;
        }

        var last = LastSelectable(items);
        var next = e.Key switch
        {
            Key.Right => SelectedIndex < 0 ? 0 : Math.Min(SelectedIndex + 1, last),
            Key.Left => SelectedIndex < 0 ? last : Math.Max(SelectedIndex - 1, 0),
            Key.Home => 0,
            Key.End => last,
            _ => (int?)null,
        };

        if (next is { } index && last >= 0)
        {
            SetCurrentValue(SelectedIndexProperty, index);
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Enter && SelectedIndex >= 0)
        {
            Activate(SelectedIndex);
            e.Handled = true;
        }
        else if (e.Key == Key.Escape && SelectedIndex >= 0)
        {
            SetCurrentValue(SelectedIndexProperty, -1);
            e.Handled = true;
        }
    }

    /// <summary>O último bloco que não está no futuro.</summary>
    private static int LastSelectable(IReadOnlyList<HoursBucket> items)
    {
        for (var index = items.Count - 1; index >= 0; index--)
        {
            if (!items[index].IsFuture)
            {
                return index;
            }
        }

        return -1;
    }

    private void Activate(int index)
    {
        if (ActivateCommand is { } command && command.CanExecute(index))
        {
            command.Execute(index);
        }
    }

    /// <summary>O valor em cima da barra: "84h" de relance, e os minutos só abaixo de 10 horas.</summary>
    internal static string Compact(TimeSpan total) =>
        total.TotalHours >= 10
            ? string.Create(CultureInfo.InvariantCulture, $"{Math.Round(total.TotalHours, MidpointRounding.AwayFromZero)}h")
            : WorkTimeFormatter.Duration(total);

    /// <summary>O topo cobre a barra mais alta e a média.</summary>
    private (double Top, double Step) Scale(IReadOnlyList<HoursBucket> items)
    {
        var max = items.Count == 0 ? 0 : items.Max(item => item.Total.TotalHours);
        return ChartAxis.For(Math.Max(max, Average.TotalHours));
    }

    /// <summary>
    /// Os rótulos de baixo. Na escala de dias, se não cabem todos, saem de dois
    /// em dois (ou mais) — mas o de hoje e o escolhido aparecem sempre.
    /// </summary>
    private void DrawLabels(DrawingContext context, IReadOnlyList<HoursBucket> items, Rect plot, double band)
    {
        var widest = items.Max(item => Text(item.Label, AxisTextBrush)?.Width ?? 0);
        var every = Math.Max(1, (int)Math.Ceiling((widest + 6) / Math.Max(band, 1)));

        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index];

            if (index % every != 0 && !item.IsCurrent && index != SelectedIndex)
            {
                continue;
            }

            var brush = item.IsCurrent ? CurrentTextBrush ?? AxisTextBrush : AxisTextBrush;
            var label = Text(item.Label, brush, item.IsCurrent ? FontWeight.Bold : FontWeight.Normal);

            if (label is null)
            {
                continue;
            }

            if (item.IsFuture)
            {
                using (context.PushOpacity(0.5))
                {
                    context.DrawText(label, new Point(plot.Left + (index * band) + ((band - label.Width) / 2), plot.Bottom + 4));
                }
            }
            else
            {
                context.DrawText(label, new Point(plot.Left + (index * band) + ((band - label.Width) / 2), plot.Bottom + 4));
            }
        }
    }

    /// <summary>O balão do bloco sob o mouse, acima da barra e dentro do controle.</summary>
    private void DrawTip(DrawingContext context, HoursBucket item, int index, Rect plot, double band)
    {
        if (Text(item.Summary, TipTextBrush) is not { } text)
        {
            return;
        }

        var bar = BarRect(index);
        var anchorY = bar.Width > 0 ? bar.Top : plot.Bottom;
        var width = text.Width + 16;
        var height = text.Height + 10;
        var x = Math.Clamp(plot.Left + (index * band) + (band / 2) - (width / 2), 2, Math.Max(2, Bounds.Width - width - 2));
        var y = Math.Max(2, anchorY - height - 6);
        var box = new Rect(x, y, width, height);

        context.DrawRectangle(TipBackgroundBrush, TipBorderBrush is { } border ? new Pen(border, 1) : null, box, 6, 6);
        context.DrawText(text, new Point(box.X + 8, box.Y + 5));
    }

    private FormattedText? Text(string text, IBrush? brush, FontWeight weight = FontWeight.Normal) =>
        brush is null
            ? null
            : new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily, FontStyle.Normal, weight), TextSize, brush);
}
