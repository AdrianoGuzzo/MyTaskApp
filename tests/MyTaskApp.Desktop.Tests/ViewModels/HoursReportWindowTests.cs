using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Desktop.Reports;
using MyTaskApp.Desktop.Tests.StickyNotes;
using MyTaskApp.Desktop.Theming;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A janela "Relatório de horas" sobe de verdade (ADR-097): binding de XAML só
/// falha em runtime, e esta tela liga dois controles desenhados, a tabela e os
/// indicadores ao mesmo ViewModel.
/// </summary>
public class HoursReportWindowTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);

    private static async Task<(HoursReportWindow Window, HoursReportViewModel ViewModel)> ShowAsync(bool withHours = true)
    {
        var time = new FakeTimeProvider(Now);
        var clock = new UserClock(time, Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" }), NullLogger<UserClock>.Instance);
        var query = new FakeTimeEntryReportQuery();

        if (withHours)
        {
            var start = clock.ToInstant(new DateOnly(2026, 10, 14), new TimeOnly(9, 0));
            query.Rows.Add(new TimeEntryReportRow(
                Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), "Relatório", null, [],
                start, start.AddHours(2), TimeEntrySource.Manual, null, start, null, false, false));
        }

        var runner = new FakeUseCaseRunner();
        runner.Handlers[typeof(GetHoursHistoryHandler)] = new GetHoursHistoryHandler(query, clock, time);
        runner.Handlers[typeof(TimeReportHandlers)] = new TimeReportHandlers(query, clock, time);
        runner.ResultsByHandler[typeof(GetTagsHandler)] = new List<TagRow>();

        var viewModel = new HoursReportViewModel(runner, NullLogger<HoursReportViewModel>.Instance);
        var window = new HoursReportWindow(viewModel);
        window.Show();
        await viewModel.ShowAsync(CancellationToken.None);
        window.UpdateLayout();

        return (window, viewModel);
    }

    private static T Named<T>(Window window, string name)
        where T : Control =>
        window.GetLogicalDescendants().OfType<T>().Single(control => control.Name == name);

    [AvaloniaFact]
    public async Task TheWindow_OpensWithTheIndicatorsAndTheChart()
    {
        var (window, viewModel) = await ShowAsync();

        Named<TextBlock>(window, "PeriodTitleText").Text.Should().Be("outubro de 2026");
        Named<TextBlock>(window, "TotalText").Text.Should().Be("2h");
        Named<TextBlock>(window, "BestText").Text.Should().Be("2h");
        Named<TextBlock>(window, "ActiveDaysText").Text.Should().Be("1 de 27");

        var chart = Named<HoursBarChart>(window, "Chart");
        chart.IsVisible.Should().BeTrue();
        chart.Items.Should().HaveCount(31);
        chart.BarBrush.Should().NotBeNull();
        Named<DockPanel>(window, "TablePanel").IsVisible.Should().BeFalse();
        Named<HoursHeatmap>(window, "Heatmap").IsEffectivelyVisible.Should().BeFalse();
        Named<StackPanel>(window, "EmptyState").IsVisible.Should().BeFalse();
        Named<ItemsControl>(window, "TaskList").ItemCount.Should().Be(1);

        viewModel.IsShown.Should().BeTrue();
        window.Close();
    }

    [AvaloniaFact]
    public async Task TheTable_SharesTheChoiceWithTheChart()
    {
        var (window, viewModel) = await ShowAsync();

        Named<Button>(window, "TableViewButton").Command!.Execute(HoursReportView.Table);
        window.UpdateLayout();

        var table = Named<ListBox>(window, "HoursTable");
        Named<DockPanel>(window, "TablePanel").IsVisible.Should().BeTrue();
        Named<HoursBarChart>(window, "Chart").IsVisible.Should().BeFalse();
        table.ItemCount.Should().Be(31);

        table.SelectedIndex = 13;

        viewModel.SelectedIndex.Should().Be(13);
        Named<HoursBarChart>(window, "Chart").SelectedIndex.Should().Be(13);
        Named<TextBlock>(window, "TaskPanelTitleText").Text.Should().Be("Horas por tarefa · qua, 14/10/2026");

        window.Close();
    }

    [AvaloniaFact]
    public async Task TheMonthScale_ShowsTheHeatmap()
    {
        var (window, viewModel) = await ShowAsync();

        await viewModel.SetScaleAsync(HoursScale.Month);
        window.UpdateLayout();

        var heatmap = Named<HoursHeatmap>(window, "Heatmap");
        heatmap.IsEffectivelyVisible.Should().BeTrue();
        heatmap.Days.Should().HaveCount(365);
        Named<Button>(window, "MonthScaleButton").Classes.Should().Contain("active");
        Named<Button>(window, "DayScaleButton").Classes.Should().NotContain("active");

        window.Close();
    }

    [AvaloniaFact]
    public async Task AMonthWithoutHours_ShowsTheEmptyState()
    {
        var (window, _) = await ShowAsync(withHours: false);

        Named<StackPanel>(window, "EmptyState").IsVisible.Should().BeTrue();

        window.Close();
    }

    [AvaloniaFact]
    public async Task TheX_HidesTheWindow_AndStopsTheRefreshes()
    {
        var (window, viewModel) = await ShowAsync();

        window.Close();

        window.IsVisible.Should().BeFalse();
        viewModel.IsShown.Should().BeFalse();
    }

    [AvaloniaTheory]
    [MemberData(nameof(Themes))]
    public async Task EveryTheme_PaintsTheChartWithItsOwnTokens(string themeId)
    {
        var (window, _) = await ShowAsync();
        var palette = ThemeResources.Build(ThemeCatalog.Find(themeId)!);
        window.Resources.MergedDictionaries.Add(palette);
        window.UpdateLayout();

        var chart = Named<HoursBarChart>(window, "Chart");

        ((ISolidColorBrush)chart.BarBrush!).Color.Should().Be(((ISolidColorBrush)palette["WidgetAccentBrush"]!).Color);
        ((ISolidColorBrush)chart.AxisTextBrush!).Color.Should().Be(((ISolidColorBrush)palette["WidgetTextLowBrush"]!).Color);
        ((ISolidColorBrush)Named<HoursHeatmap>(window, "Heatmap").LevelBrush!).Color
            .Should().Be(((ISolidColorBrush)palette["WidgetAccentBrush"]!).Color);

        window.Close();
    }

    public static TheoryData<string> Themes()
    {
        var data = new TheoryData<string>();

        foreach (var theme in ThemeCatalog.All)
        {
            data.Add(theme.Id);
        }

        return data;
    }
}

/// <summary>De onde o relatório abre: o menu, o Ctrl+Shift+H e o histórico de 7 dias (ADR-097).</summary>
public class HoursReportEntryPointTests
{
    private static TodayViewModel Today() =>
        new(
            new FakeUseCaseRunner { Result = new TodayBoard(new DateOnly(2026, 10, 27), [], [], [], [], []) },
            new FakeConfirmationDialog(),
            new FakeClipboardWriter(),
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);

    [AvaloniaFact]
    public void TheMenu_AndCtrlShiftH_AskForTheReport()
    {
        var today = Today();
        var window = new MainWindow { DataContext = today };
        var asked = 0;
        today.HoursReportRequested += () => asked++;
        window.Show();

        // O flyout fechado ainda não ligou o comando: o item vale pelo texto e pelo atalho que mostra.
        var item = window.GetLogicalDescendantsOfMenus().Single(candidate => candidate.Header as string == "Relatório de horas…");
        item.InputGesture.Should().Be(new KeyGesture(Key.H, KeyModifiers.Control | KeyModifiers.Shift));
        today.OpenHoursReportCommand.Execute(null);

        var binding = window.KeyBindings.Single(
            candidate => candidate.Gesture == new KeyGesture(Key.H, KeyModifiers.Control | KeyModifiers.Shift));
        binding.Command!.Execute(null);

        asked.Should().Be(2);
    }

    [Fact]
    public async Task TheList_SaysWhenItReloads()
    {
        var today = Today();
        var loaded = 0;
        today.BoardLoaded += () => loaded++;

        await today.LoadAsync(CancellationToken.None);

        loaded.Should().Be(1);
    }
}
