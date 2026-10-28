using System.Globalization;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Media;
using MyTaskApp.Application.TimeTracking;

namespace MyTaskApp.Desktop.Reports;

/// <summary>
/// O ano inteiro de uma vez (ADR-097): um quadrado por dia, uma coluna por
/// semana (de domingo a sábado), e a cor pelas faixas fixas de
/// <see cref="HoursLevels"/>. Clicar num dia abre o mês dele, com o dia
/// escolhido.
/// </summary>
/// <remarks>
/// Os níveis são o mesmo pincel de destaque com opacidades diferentes: o mapa
/// segue qualquer um dos temas sem uma paleta própria.
/// </remarks>
public sealed class HoursHeatmap : Control
{
    public static readonly StyledProperty<IReadOnlyList<HoursBucket>?> DaysProperty =
        AvaloniaProperty.Register<HoursHeatmap, IReadOnlyList<HoursBucket>?>(nameof(Days));

    /// <summary>Clique ou Enter num dia: recebe a data dele.</summary>
    public static readonly StyledProperty<ICommand?> DayCommandProperty =
        AvaloniaProperty.Register<HoursHeatmap, ICommand?>(nameof(DayCommand));

    public static readonly StyledProperty<IBrush?> EmptyBrushProperty =
        AvaloniaProperty.Register<HoursHeatmap, IBrush?>(nameof(EmptyBrush));

    public static readonly StyledProperty<IBrush?> LevelBrushProperty =
        AvaloniaProperty.Register<HoursHeatmap, IBrush?>(nameof(LevelBrush));

    public static readonly StyledProperty<IBrush?> TextBrushProperty =
        AvaloniaProperty.Register<HoursHeatmap, IBrush?>(nameof(TextBrush));

    public static readonly StyledProperty<IBrush?> TodayBrushProperty =
        AvaloniaProperty.Register<HoursHeatmap, IBrush?>(nameof(TodayBrush));

    public static readonly StyledProperty<IBrush?> TipBackgroundBrushProperty =
        AvaloniaProperty.Register<HoursHeatmap, IBrush?>(nameof(TipBackgroundBrush));

    public static readonly StyledProperty<IBrush?> TipBorderBrushProperty =
        AvaloniaProperty.Register<HoursHeatmap, IBrush?>(nameof(TipBorderBrush));

    public static readonly StyledProperty<IBrush?> TipTextBrushProperty =
        AvaloniaProperty.Register<HoursHeatmap, IBrush?>(nameof(TipTextBrush));

    public static readonly StyledProperty<IBrush?> FocusBrushProperty =
        AvaloniaProperty.Register<HoursHeatmap, IBrush?>(nameof(FocusBrush));

    public static readonly StyledProperty<FontFamily> FontFamilyProperty =
        TextElement.FontFamilyProperty.AddOwner<HoursHeatmap>();

    /// <summary>A opacidade de cada nível, do 1 ao 4.</summary>
    internal static readonly double[] LevelOpacity = [0, 0.3, 0.5, 0.75, 1];

    /// <summary>A coluna dos dias da semana, à esquerda.</summary>
    internal const double WeekdayRoom = 30;

    /// <summary>A linha dos meses, em cima.</summary>
    internal const double MonthRoom = 16;

    private const double MaxPitch = 16;

    private const double Gap = 3;

    private const double TextSize = 10;

    private static readonly string[] WeekdayLabels = ["", "seg", "", "qua", "", "sex", ""];

    private int _hovered = -1;

    private int _focused = -1;

    static HoursHeatmap()
    {
        AffectsRender<HoursHeatmap>(
            DaysProperty,
            EmptyBrushProperty,
            LevelBrushProperty,
            TextBrushProperty,
            TodayBrushProperty,
            TipBackgroundBrushProperty,
            TipBorderBrushProperty,
            TipTextBrushProperty,
            FocusBrushProperty,
            FontFamilyProperty,
            IsFocusedProperty);

        AffectsMeasure<HoursHeatmap>(DaysProperty);
        FocusableProperty.OverrideDefaultValue<HoursHeatmap>(true);
    }

    public HoursHeatmap()
    {
        ClipToBounds = true;
    }

    /// <summary>Os dias de um ano, de 1º de janeiro a 31 de dezembro.</summary>
    public IReadOnlyList<HoursBucket>? Days
    {
        get => GetValue(DaysProperty);
        set => SetValue(DaysProperty, value);
    }

    public ICommand? DayCommand
    {
        get => GetValue(DayCommandProperty);
        set => SetValue(DayCommandProperty, value);
    }

    /// <summary>O dia sem horas.</summary>
    public IBrush? EmptyBrush
    {
        get => GetValue(EmptyBrushProperty);
        set => SetValue(EmptyBrushProperty, value);
    }

    /// <summary>O dia com horas, mais forte quanto mais horas.</summary>
    public IBrush? LevelBrush
    {
        get => GetValue(LevelBrushProperty);
        set => SetValue(LevelBrushProperty, value);
    }

    public IBrush? TextBrush
    {
        get => GetValue(TextBrushProperty);
        set => SetValue(TextBrushProperty, value);
    }

    /// <summary>O contorno do dia de hoje.</summary>
    public IBrush? TodayBrush
    {
        get => GetValue(TodayBrushProperty);
        set => SetValue(TodayBrushProperty, value);
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

    /// <summary>Quantas colunas o primeiro dia empurra: 1º de janeiro numa quarta começa na quarta linha.</summary>
    private int Lead => Days is { Count: > 0 } days ? (int)days[0].From.DayOfWeek : 0;

    private int Weeks => Days is { Count: > 0 } days ? (int)Math.Ceiling((days.Count + Lead) / 7d) : 53;

    private double Pitch(double width) =>
        Math.Clamp(Math.Floor((width - WeekdayRoom) / Weeks), 6, MaxPitch);

    /// <summary>O dia sob o ponto, contando o vão entre os quadrados; -1 fora do ano.</summary>
    internal int IndexAt(Point point)
    {
        if (Days is not { Count: > 0 } days)
        {
            return -1;
        }

        var pitch = Pitch(Bounds.Width);
        var column = (int)Math.Floor((point.X - WeekdayRoom) / pitch);
        var row = (int)Math.Floor((point.Y - MonthRoom) / pitch);

        if (column < 0 || column >= Weeks || row < 0 || row > 6)
        {
            return -1;
        }

        var index = (column * 7) + row - Lead;
        return index >= 0 && index < days.Count ? index : -1;
    }

    /// <summary>O quadrado do dia, em coordenadas do controle.</summary>
    internal Rect CellRect(int index)
    {
        var pitch = Pitch(Bounds.Width);
        var slot = index + Lead;

        return new Rect(WeekdayRoom + (slot / 7 * pitch), MonthRoom + (slot % 7 * pitch), pitch - Gap, pitch - Gap);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? WeekdayRoom + (Weeks * MaxPitch) : availableSize.Width;
        return new Size(width, MonthRoom + (7 * Pitch(width)));
    }

    public override void Render(DrawingContext context)
    {
        if (Days is not { Count: > 0 } days)
        {
            return;
        }

        var pitch = Pitch(Bounds.Width);

        // Os meses, em cima da coluna onde cada um começa.
        for (var index = 0; index < days.Count; index++)
        {
            var date = days[index].From;

            if (date.Day == 1 && Text(ReportCalendar.MonthAbbreviation(date.Month), TextBrush) is { } month)
            {
                context.DrawText(month, new Point(CellRect(index).X, 0));
            }
        }

        for (var row = 0; row < 7; row++)
        {
            if (WeekdayLabels[row].Length > 0 && Text(WeekdayLabels[row], TextBrush) is { } weekday)
            {
                context.DrawText(weekday, new Point(0, MonthRoom + (row * pitch) + ((pitch - Gap - weekday.Height) / 2)));
            }
        }

        for (var index = 0; index < days.Count; index++)
        {
            var day = days[index];
            var cell = CellRect(index);

            if (day.Level > 0 && LevelBrush is { } level)
            {
                using (context.PushOpacity(LevelOpacity[day.Level]))
                {
                    context.DrawRectangle(level, null, cell, 2, 2);
                }
            }
            else if (EmptyBrush is { } empty)
            {
                using (context.PushOpacity(day.IsFuture ? 0.4 : 1))
                {
                    context.DrawRectangle(empty, null, cell, 2, 2);
                }
            }

            if (day.IsCurrent && TodayBrush is { } today)
            {
                context.DrawRectangle(null, new Pen(today, 1.5), cell.Inflate(0.75), 2, 2);
            }
        }

        if (IsFocused && _focused >= 0 && _focused < days.Count && FocusBrush is { } focus)
        {
            context.DrawRectangle(null, new Pen(focus, 2), CellRect(_focused).Inflate(1.5), 3, 3);
        }

        var tip = _hovered >= 0 ? _hovered : IsFocused ? _focused : -1;

        if (tip >= 0 && tip < days.Count)
        {
            DrawTip(context, days[tip], CellRect(tip));
        }
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        var index = IndexAt(e.GetPosition(this));

        if (index != _hovered)
        {
            _hovered = index;
            Cursor = index >= 0 && !Days![index].IsFuture ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
            InvalidateVisual();
        }
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        _hovered = -1;
        Cursor = Cursor.Default;
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);

        var index = IndexAt(e.GetPosition(this));

        if (index >= 0 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            _focused = index;
            e.Handled = true;
            Open(index);
        }
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);

        if (_focused < 0 && Days is { Count: > 0 } days)
        {
            var today = days.ToList().FindIndex(day => day.IsCurrent);
            _focused = today >= 0 ? today : 0;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);

        if (Days is not { Count: > 0 } days || _focused < 0)
        {
            return;
        }

        var step = e.Key switch
        {
            Key.Right => 7,
            Key.Left => -7,
            Key.Down => 1,
            Key.Up => -1,
            _ => 0,
        };

        if (step != 0)
        {
            _focused = Math.Clamp(_focused + step, 0, days.Count - 1);
            InvalidateVisual();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Open(_focused);
            e.Handled = true;
        }
    }

    private void Open(int index)
    {
        var day = Days![index];

        if (!day.IsFuture && DayCommand is { } command && command.CanExecute(day.From))
        {
            command.Execute(day.From);
        }
    }

    private void DrawTip(DrawingContext context, HoursBucket day, Rect cell)
    {
        if (Text(day.Summary, TipTextBrush) is not { } text)
        {
            return;
        }

        var width = text.Width + 14;
        var height = text.Height + 8;
        var x = Math.Clamp(cell.Center.X - (width / 2), 0, Math.Max(0, Bounds.Width - width));

        // Em cima do quadrado; nas duas primeiras linhas, embaixo, para não sair do controle.
        var y = cell.Top - height - 4 >= 0 ? cell.Top - height - 4 : cell.Bottom + 4;
        var box = new Rect(x, y, width, height);

        context.DrawRectangle(TipBackgroundBrush, TipBorderBrush is { } border ? new Pen(border, 1) : null, box, 5, 5);
        context.DrawText(text, new Point(box.X + 7, box.Y + 4));
    }

    private FormattedText? Text(string text, IBrush? brush) =>
        brush is null
            ? null
            : new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface(FontFamily), TextSize, brush);
}
