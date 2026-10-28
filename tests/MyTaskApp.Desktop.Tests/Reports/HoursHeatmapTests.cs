using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.Input;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Desktop.Reports;

namespace MyTaskApp.Desktop.Tests.Reports;

/// <summary>O mapa de calor do ano, desenhado e clicado de verdade (ADR-097).</summary>
public class HoursHeatmapTests
{
    private static readonly DateOnly Today = new(2026, 10, 27);

    /// <summary>2026 inteiro; 1º de janeiro de 2026 é uma quinta.</summary>
    private static IReadOnlyList<HoursBucket> Year2026() =>
        Enumerable.Range(0, 365)
            .Select(offset =>
            {
                var date = new DateOnly(2026, 1, 1).AddDays(offset);
                var hours = TimeSpan.FromHours(offset % 10);

                return new HoursBucket(
                    date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture), date, date, "", "", $"dia {date:dd/MM}",
                    hours, 1, 1, false, date == Today, date > Today, HoursLevels.Of(hours));
            })
            .ToList();

    private static (Window Window, HoursHeatmap Map) Show(bool brushes = true)
    {
        var map = new HoursHeatmap { Days = Year2026() };

        if (brushes)
        {
            map.EmptyBrush = Brushes.WhiteSmoke;
            map.LevelBrush = Brushes.SeaGreen;
            map.TextBrush = Brushes.Gray;
            map.TodayBrush = Brushes.Black;
            map.TipBackgroundBrush = Brushes.White;
            map.TipBorderBrush = Brushes.Gray;
            map.TipTextBrush = Brushes.Black;
            map.FocusBrush = Brushes.Orange;
        }

        var window = new Window { Width = 900, Height = 200, Content = new StackPanel { Children = { map } } };
        window.Show();
        window.UpdateLayout();

        return (window, map);
    }

    [AvaloniaFact]
    public void TheWeeks_StartOnSunday()
    {
        var (window, map) = Show();

        // 01/01 é quinta: primeira coluna, quinta linha (dom = 0).
        var first = map.CellRect(0);
        map.IndexAt(first.Center).Should().Be(0);
        first.Y.Should().BeApproximately(HoursHeatmap.MonthRoom + (4 * (first.Height + 3)), 0.01);

        // 04/01 é domingo: segunda coluna, primeira linha.
        var sunday = map.CellRect(3);
        sunday.X.Should().BeGreaterThan(first.X);
        sunday.Y.Should().BeApproximately(HoursHeatmap.MonthRoom, 0.01);
        map.IndexAt(sunday.Center).Should().Be(3);

        // Antes de 01/01, na mesma coluna, não há dia.
        map.IndexAt(new Point(first.Center.X, HoursHeatmap.MonthRoom + 1)).Should().Be(-1);
        map.IndexAt(new Point(2, 2)).Should().Be(-1);

        window.Close();
    }

    [AvaloniaFact]
    public void ClickingADay_OpensIt_ButNotADayInTheFuture()
    {
        var (window, map) = Show();
        DateOnly? opened = null;
        map.DayCommand = new RelayCommand<DateOnly>(day => opened = day);

        window.MouseDown(map.TranslatePoint(map.CellRect(40).Center, window)!.Value, MouseButton.Left);
        opened.Should().Be(new DateOnly(2026, 2, 10));

        opened = null;
        window.MouseDown(map.TranslatePoint(map.CellRect(350).Center, window)!.Value, MouseButton.Left);
        opened.Should().BeNull();

        window.Close();
    }

    [AvaloniaFact]
    public void TheKeyboard_StartsOnToday_AndMovesByDayAndWeek()
    {
        var (window, map) = Show();
        DateOnly? opened = null;
        map.DayCommand = new RelayCommand<DateOnly>(day => opened = day);
        map.Focus();

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        opened.Should().Be(Today);

        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        opened.Should().Be(Today.AddDays(-8));

        window.Close();
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Drawing_WorksWithAndWithoutBrushes(bool brushes)
    {
        var (window, map) = Show(brushes);
        map.Focus();
        window.MouseMove(map.TranslatePoint(map.CellRect(100).Center, window)!.Value);

        using var bitmap = new RenderTargetBitmap(new PixelSize(900, 200));
        var draw = () => bitmap.Render(map);

        draw.Should().NotThrow();

        map.Days = [];
        draw.Should().NotThrow();

        window.Close();
    }
}
