using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A janela "Relatório de horas" sem tela (ADR-097), com o relatório de
/// verdade sobre períodos semeados: a navegação, a escolha e o filtro pedem o
/// que devem e mostram o que voltou.
/// </summary>
public class HoursReportViewModelTests
{
    /// <summary>Terça, 27/10/2026, 15:00 em São Paulo.</summary>
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 18, 0, 0, TimeSpan.Zero);

    private static readonly Guid Report = Guid.CreateVersion7();

    private static readonly Guid Review = Guid.CreateVersion7();

    private static readonly TagRow Client = new(Guid.CreateVersion7(), "Cliente", "#3366FF", 2);

    private readonly FakeTimeProvider _time = new(Now);

    private readonly UserClock _clock;

    private readonly FakeTimeEntryReportQuery _query = new();

    private readonly FakeUseCaseRunner _runner = new();

    public HoursReportViewModelTests()
    {
        _clock = new UserClock(_time, Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" }), NullLogger<UserClock>.Instance);
        _runner.Handlers[typeof(GetHoursHistoryHandler)] = new GetHoursHistoryHandler(_query, _clock, _time);
        _runner.Handlers[typeof(TimeReportHandlers)] = new TimeReportHandlers(_query, _clock, _time);
        _runner.ResultsByHandler[typeof(GetTagsHandler)] = new List<TagRow> { Client };
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HoursReportViewModel ViewModel() => new(_runner, NullLogger<HoursReportViewModel>.Instance);

    private void Add(DateOnly day, int fromHour, int toHour, Guid? task = null)
    {
        var start = _clock.ToInstant(day, new TimeOnly(fromHour, 0));
        var id = task ?? Report;

        _query.Rows.Add(new TimeEntryReportRow(
            Guid.CreateVersion7(), Guid.CreateVersion7(), id, id == Review ? "Revisão" : "Relatório", null, [],
            start, _clock.ToInstant(day, new TimeOnly(toHour, 0)), TimeEntrySource.Manual, null, start, null, false, false));
    }

    private static DateOnly Oct(int day) => new(2026, 10, day);

    [Fact]
    public async Task Opening_ShowsThisMonthByDay_WithTheTasksOfTheWholeMonth()
    {
        Add(Oct(14), 9, 11);
        Add(Oct(15), 9, 10, Review);
        var viewModel = ViewModel();

        await viewModel.ShowAsync(Ct);

        viewModel.IsShown.Should().BeTrue();
        viewModel.IsDayScale.Should().BeTrue();
        viewModel.PeriodTitle.Should().Be("outubro de 2026");
        viewModel.Rows.Should().HaveCount(31);
        viewModel.Rows[13].Duration.Should().Be("2h");
        viewModel.Rows[13].Ratio.Should().Be(1);
        viewModel.Rows[0].IsMuted.Should().BeTrue();
        viewModel.Rows[30].Duration.Should().BeEmpty();
        viewModel.Tags.Select(tag => tag.Name).Should().Equal("Todas as etiquetas", "Cliente");
        viewModel.TaskPanelTitle.Should().Be("Horas por tarefa · outubro de 2026");
        viewModel.TaskRows.Select(task => (task.Title, task.Duration, task.Share)).Should().Equal(
            ("Relatório", "2h", "67%"),
            ("Revisão", "1h", "33%"));
        viewModel.Crumbs.Select(crumb => crumb.Label).Should().Equal("Anos", "2026", "outubro");
        viewModel.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task TheIndicators_ReadThePeriod()
    {
        Add(Oct(14), 9, 13);
        Add(Oct(20), 9, 11);
        Add(new DateOnly(2026, 9, 10), 9, 12);
        var viewModel = ViewModel();

        await viewModel.ShowAsync(Ct);

        viewModel.TotalText.Should().Be("6h");
        viewModel.ComparisonText.Should().Be("▲ 100% vs 1–27 de setembro");
        viewModel.ComparisonDetail.Should().Be("No período anterior: 3h");
        viewModel.AverageText.Should().Be("3h");
        viewModel.ActiveDaysText.Should().Be("2 de 27");
        viewModel.BestCaption.Should().Be("Maior dia");
        viewModel.BestText.Should().Be("4h");
        viewModel.BestDetail.Should().Be("qua, 14/10/2026");
        viewModel.Readout.Should().Be("outubro de 2026 · 6h · 2 dias · 2 lançamentos");
        viewModel.IsEmpty.Should().BeFalse();
    }

    [Fact]
    public async Task AMonthWithoutHours_SaysSo()
    {
        var viewModel = ViewModel();

        await viewModel.ShowAsync(Ct);

        viewModel.IsEmpty.Should().BeTrue();
        viewModel.EmptyMessage.Should().Be("Nenhuma hora lançada em outubro de 2026.");
        viewModel.ComparisonText.Should().Be("vs 1–27 de setembro: sem horas");
        viewModel.BestText.Should().Be("—");
        viewModel.HasNoTaskRows.Should().BeTrue();
    }

    [Fact]
    public async Task PreviousAndNext_FollowTheReport_ButNotIntoTheFuture()
    {
        var viewModel = ViewModel();
        await viewModel.ShowAsync(Ct);

        viewModel.NextCommand.CanExecute(null).Should().BeFalse();

        await viewModel.PreviousAsync();
        viewModel.PeriodTitle.Should().Be("setembro de 2026");
        viewModel.NextCommand.CanExecute(null).Should().BeTrue();

        await viewModel.NextAsync();
        viewModel.PeriodTitle.Should().Be("outubro de 2026");

        await viewModel.PreviousAsync();
        await viewModel.TodayAsync();
        viewModel.PeriodTitle.Should().Be("outubro de 2026");
    }

    [Fact]
    public async Task ChangingTheScale_KeepsTheChosenDate()
    {
        Add(Oct(14), 9, 11);
        var viewModel = ViewModel();
        await viewModel.ShowAsync(Ct);
        viewModel.SelectedIndex = 13;

        await viewModel.SetScaleAsync(HoursScale.Month);

        viewModel.PeriodTitle.Should().Be("2026");
        viewModel.SelectedIndex.Should().Be(9);
        viewModel.IsHeatmapVisible.Should().BeTrue();
        viewModel.HeatmapDays.Should().HaveCount(365);

        await viewModel.SetScaleAsync(HoursScale.Day);

        viewModel.PeriodTitle.Should().Be("outubro de 2026");
        viewModel.SelectedIndex.Should().Be(0);
        viewModel.IsHeatmapVisible.Should().BeFalse();
    }

    [Fact]
    public async Task DrillingDown_GoesFromYearsToMonthsToDays()
    {
        Add(new DateOnly(2025, 3, 10), 9, 12);
        Add(Oct(14), 9, 11);
        var viewModel = ViewModel();
        await viewModel.ShowAsync(Ct);

        await viewModel.SetScaleAsync(HoursScale.Year);
        viewModel.Buckets.Select(bucket => bucket.Label).Should().Equal("2025", "2026");
        viewModel.Crumbs.Select(crumb => crumb.Label).Should().Equal("Todos os anos");

        await viewModel.DrillAsync(0);
        viewModel.PeriodTitle.Should().Be("2025");
        viewModel.IsMonthScale.Should().BeTrue();

        await viewModel.DrillAsync(2);
        viewModel.PeriodTitle.Should().Be("março de 2025");
        viewModel.IsDayScale.Should().BeTrue();

        // Nos dias não há para onde descer.
        await viewModel.DrillAsync(9);
        viewModel.PeriodTitle.Should().Be("março de 2025");
    }

    [Fact]
    public async Task ADayOfTheHeatmap_OpensItsMonth_WithTheDayChosen()
    {
        Add(new DateOnly(2026, 8, 5), 9, 11);
        var viewModel = ViewModel();
        await viewModel.ShowAsync(Ct);
        await viewModel.SetScaleAsync(HoursScale.Month);

        await viewModel.OpenDayAsync(new DateOnly(2026, 8, 5));

        viewModel.PeriodTitle.Should().Be("agosto de 2026");
        viewModel.SelectedIndex.Should().Be(4);
        viewModel.Readout.Should().Be("qua, 05/08 · 2h · 1 lançamento");
        viewModel.TaskPanelTitle.Should().Be("Horas por tarefa · qua, 05/08/2026");
        viewModel.TaskRows.Should().ContainSingle().Which.Duration.Should().Be("2h");
    }

    [Fact]
    public async Task ChoosingABlock_ShowsItsTasks_AndLettingGo_ShowsThePeriodAgain()
    {
        Add(Oct(14), 9, 11);
        Add(Oct(14), 13, 14, Review);
        Add(Oct(15), 9, 10);
        var viewModel = ViewModel();
        await viewModel.ShowAsync(Ct);

        viewModel.SelectedIndex = 13;

        viewModel.TaskPanelTitle.Should().Be("Horas por tarefa · qua, 14/10/2026");
        viewModel.TaskRows.Select(task => (task.Title, task.Duration)).Should().Equal(("Relatório", "2h"), ("Revisão", "1h"));
        _runner.Invoked.Should().Contain(typeof(TimeReportHandlers));

        viewModel.SelectedIndex = -1;

        viewModel.TaskPanelTitle.Should().Be("Horas por tarefa · outubro de 2026");
        viewModel.TaskRows.Select(task => (task.Title, task.Duration)).Should().Equal(("Relatório", "3h"), ("Revisão", "1h"));
    }

    [Fact]
    public async Task TheHoveredBlock_IsWhatTheReadoutSays()
    {
        Add(Oct(14), 9, 11);
        var viewModel = ViewModel();
        await viewModel.ShowAsync(Ct);

        viewModel.HoveredIndex = 13;

        viewModel.Readout.Should().Be("qua, 14/10 · 2h · 1 lançamento");
    }

    [Fact]
    public async Task TheTagFilter_ReloadsWithTheTag()
    {
        var viewModel = ViewModel();
        await viewModel.ShowAsync(Ct);

        viewModel.SelectedTag = viewModel.Tags[1];

        _query.LastCriteria!.TagId.Should().Be(Client.Id);
    }

    [Fact]
    public async Task AReload_KeepsTheChosenBlock()
    {
        Add(Oct(14), 9, 11);
        var viewModel = ViewModel();
        await viewModel.ShowAsync(Ct);
        viewModel.SelectedIndex = 13;

        Add(Oct(14), 13, 14);
        await viewModel.RefreshAsync(Ct);

        viewModel.SelectedIndex.Should().Be(13);
        viewModel.Rows[13].Duration.Should().Be("3h");
        viewModel.TaskRows.Should().ContainSingle().Which.Duration.Should().Be("3h");
    }

    [Fact]
    public async Task WhileHidden_ChangesFromOutside_DoNotReload()
    {
        var viewModel = ViewModel();

        await viewModel.RefreshAsync(Ct);

        _runner.Invoked.Should().BeEmpty();
    }

    [Fact]
    public async Task AnOldAnswer_DoesNotOverwriteANewerOne()
    {
        var viewModel = ViewModel();
        var gate = new TaskCompletionSource();
        _query.Gate = gate;

        var first = viewModel.LoadAsync(Ct);
        await viewModel.OpenDayAsync(new DateOnly(2026, 9, 10));
        gate.SetResult();
        await first;

        viewModel.PeriodTitle.Should().Be("setembro de 2026");
    }

    [Fact]
    public async Task AFailedLoad_SaysSo_WithoutTheTechnicalDetail()
    {
        _runner.Handlers.Remove(typeof(GetHoursHistoryHandler));
        _runner.FailuresByHandler[typeof(GetHoursHistoryHandler)] = new InvalidOperationException("SQLite Error 5");
        var viewModel = ViewModel();

        await viewModel.ShowAsync(Ct);

        viewModel.ErrorMessage.Should().Be("Não foi possível carregar o relatório.");
        viewModel.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task Exporting_SavesTheCsv_UnderTheSuggestedName()
    {
        Add(Oct(14), 9, 11);
        var viewModel = ViewModel();
        await viewModel.ShowAsync(Ct);
        (string Name, string Content)? saved = null;
        viewModel.SaveCsv = (name, content) =>
        {
            saved = (name, content);
            return Task.FromResult<string?>("horas.csv");
        };

        await viewModel.ExportCsvAsync();

        saved!.Value.Name.Should().Be("horas-2026-10-por-dia.csv");
        saved.Value.Content.Should().StartWith("Período;Início;Fim;Horas;Duração;Lançamentos;% do total\r\n");
        saved.Value.Content.Should().Contain("qua, 14/10/2026;2026-10-14;2026-10-14;2,00;2h;1;100,0");
        viewModel.StatusMessage.Should().Be("Exportado: horas.csv");
    }

    [Fact]
    public async Task GivingUpOnTheExport_SaysNothing()
    {
        var viewModel = ViewModel();
        await viewModel.ShowAsync(Ct);
        viewModel.SaveCsv = (_, _) => Task.FromResult<string?>(null);

        await viewModel.ExportCsvAsync();

        viewModel.StatusMessage.Should().BeNull();
    }

    [Fact]
    public void TheView_SwitchesBetweenChartAndTable()
    {
        var viewModel = ViewModel();

        viewModel.SetViewCommand.Execute(HoursReportView.Table);

        viewModel.IsTable.Should().BeTrue();
        viewModel.IsChart.Should().BeFalse();
    }
}
