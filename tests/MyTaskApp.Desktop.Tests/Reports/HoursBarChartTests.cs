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

/// <summary>As barras do relatório de horas, desenhadas e clicadas de verdade (ADR-097).</summary>
public class HoursBarChartTests
{
    private static HoursBucket Bucket(int day, double hours, bool future = false, bool current = false) =>
        new($"2026-10-{day:00}", new DateOnly(2026, 10, day), new DateOnly(2026, 10, day), day.ToString(System.Globalization.CultureInfo.InvariantCulture),
            $"dia {day}", $"dia {day} · {hours}h", TimeSpan.FromHours(hours), hours > 0 ? 1 : 0, hours > 0 ? 1 : 0,
            day % 7 == 0, current, future, HoursLevels.Of(TimeSpan.FromHours(hours)));

    /// <summary>Quatro dias: 2h, nada, 6h (hoje) e um no futuro.</summary>
    private static readonly IReadOnlyList<HoursBucket> FourDays =
        [Bucket(1, 2), Bucket(2, 0), Bucket(3, 6, current: true), Bucket(4, 0, future: true)];

    private static (Window Window, HoursBarChart Chart) Show(IReadOnlyList<HoursBucket>? items = null, bool brushes = true)
    {
        var chart = new HoursBarChart { Items = items ?? FourDays, Average = TimeSpan.FromHours(4) };

        if (brushes)
        {
            chart.BarBrush = Brushes.SteelBlue;
            chart.BarHoverBrush = Brushes.LightSteelBlue;
            chart.SelectedBarBrush = Brushes.Navy;
            chart.GridBrush = Brushes.Gray;
            chart.AxisTextBrush = Brushes.DimGray;
            chart.CurrentTextBrush = Brushes.Orange;
            chart.AverageBrush = Brushes.Black;
            chart.WeekendBrush = Brushes.WhiteSmoke;
            chart.TipBackgroundBrush = Brushes.White;
            chart.TipBorderBrush = Brushes.Gray;
            chart.TipTextBrush = Brushes.Black;
            chart.FocusBrush = Brushes.Orange;
        }

        var window = new Window { Width = 600, Height = 300, Content = chart };
        window.Show();
        window.UpdateLayout();

        return (window, chart);
    }

    private static Point ColumnCenter(HoursBarChart chart, int index)
    {
        var plot = chart.Plot;
        var band = plot.Width / chart.Items!.Count;

        return new Point(plot.Left + (band * index) + (band / 2), plot.Bottom - 1);
    }

    [AvaloniaFact]
    public void TheWholeColumn_AnswersForTheBlock()
    {
        var (window, chart) = Show();
        var plot = chart.Plot;
        var band = plot.Width / 4;

        chart.IndexAt(new Point(plot.Left + 1, 1)).Should().Be(0);
        chart.IndexAt(new Point(plot.Left + band + 1, plot.Bottom + 10)).Should().Be(1);
        chart.IndexAt(new Point(plot.Right - 1, plot.Top)).Should().Be(3);
        chart.IndexAt(new Point(10, plot.Bottom - 5)).Should().Be(-1);

        window.Close();
    }

    [AvaloniaFact]
    public void TheBars_AreProportional_AndTheEmptyAndFutureOnesHaveNone()
    {
        var (window, chart) = Show();
        var plot = chart.Plot;

        // O eixo vai até 6h, de 2 em 2: a barra de 6h é a altura toda; a de 2h, um terço.
        chart.BarRect(2).Height.Should().BeApproximately(plot.Height, 0.01);
        chart.BarRect(2).Bottom.Should().BeApproximately(plot.Bottom, 0.01);
        chart.BarRect(0).Height.Should().BeApproximately(plot.Height / 3, 0.01);
        chart.BarRect(1).Should().Be(default(Rect));
        chart.BarRect(3).Should().Be(default(Rect));

        window.Close();
    }

    [AvaloniaFact]
    public void TheMouse_HoversAndChooses_AndClickingTheChosenAgainLetsGo()
    {
        var (window, chart) = Show();

        window.MouseMove(ColumnCenter(chart, 2));
        chart.HoveredIndex.Should().Be(2);

        window.MouseDown(ColumnCenter(chart, 2), MouseButton.Left);
        window.MouseUp(ColumnCenter(chart, 2), MouseButton.Left);
        chart.SelectedIndex.Should().Be(2);

        // O futuro não se escolhe.
        window.MouseDown(ColumnCenter(chart, 3), MouseButton.Left);
        window.MouseUp(ColumnCenter(chart, 3), MouseButton.Left);
        chart.SelectedIndex.Should().Be(2);

        window.MouseDown(ColumnCenter(chart, 0), MouseButton.Left);
        window.MouseUp(ColumnCenter(chart, 0), MouseButton.Left);
        chart.SelectedIndex.Should().Be(0);

        window.Close();
    }

    [AvaloniaFact]
    public void TheKeyboard_WalksTheBlocks_SkippingTheFuture()
    {
        var (window, chart) = Show();
        int? activated = null;
        chart.ActivateCommand = new RelayCommand<int>(index => activated = index);
        chart.Focus();

        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        chart.SelectedIndex.Should().Be(0);

        window.KeyPress(Key.End, RawInputModifiers.None, PhysicalKey.End, null);
        chart.SelectedIndex.Should().Be(2);

        window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
        chart.SelectedIndex.Should().Be(2);

        window.KeyPress(Key.Left, RawInputModifiers.None, PhysicalKey.ArrowLeft, null);
        chart.SelectedIndex.Should().Be(1);

        window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
        activated.Should().Be(1);

        window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        chart.SelectedIndex.Should().Be(-1);

        window.KeyPress(Key.Home, RawInputModifiers.None, PhysicalKey.Home, null);
        chart.SelectedIndex.Should().Be(0);

        window.Close();
    }

    [Theory]
    [InlineData(0, "0min")]
    [InlineData(380, "6h 20min")]
    [InlineData(599, "9h 59min")]
    [InlineData(600, "10h")]
    [InlineData(5088, "85h")]
    public void TheValueOverTheBar_DropsTheMinutesFromTenHoursUp(int minutes, string text) =>
        HoursBarChart.Compact(TimeSpan.FromMinutes(minutes)).Should().Be(text);

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void Drawing_WorksWithAndWithoutBrushes(bool brushes)
    {
        var month = Enumerable.Range(1, 31).Select(day => Bucket(day, day % 3, future: day > 27, current: day == 27)).ToList();
        var (window, chart) = Show(month, brushes);
        chart.SelectedIndex = 4;
        chart.HoveredIndex = 5;
        chart.Focus();

        using var bitmap = new RenderTargetBitmap(new PixelSize(600, 300));
        var draw = () => bitmap.Render(chart);

        draw.Should().NotThrow();

        chart.Items = [];
        draw.Should().NotThrow();

        window.Close();
    }
}
