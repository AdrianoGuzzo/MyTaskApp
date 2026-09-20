using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.Tests.ViewModels;

public class TodayViewModelTests
{
    private static readonly DateOnly Date = new(2026, 9, 17);
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();

    private TodayViewModel ViewModel() =>
        new(_runner, TimeProvider.System, NullLogger<TodayViewModel>.Instance);

    private static TodayTask Row(string title, TimeOnly? time = null, bool isLate = false) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, TaskPriority.Normal, Date, time, isLate);

    private static TodayBoard Board(
        IReadOnlyList<TodayTask>? overdue = null,
        IReadOnlyList<TodayTask>? now = null,
        IReadOnlyList<TodayTask>? today = null,
        IReadOnlyList<TodayTask>? unscheduled = null,
        IReadOnlyList<TodayTask>? completed = null) =>
        new(Date, overdue ?? [], now ?? [], today ?? [], unscheduled ?? [], completed ?? []);

    [Fact]
    public async Task Load_ShowsTheDayInTheHeader()
    {
        _runner.Result = Board();

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.Title.Should().Be("HOJE — 17/09/2026");
    }

    [Fact]
    public async Task Load_KeepsTheSectionOrderFromTheSpec()
    {
        _runner.Result = Board(
            overdue: [Row("Revisar documentação")],
            now: [Row("Verificar chamados", new TimeOnly(14, 0))],
            today: [Row("Deploy", new TimeOnly(15, 30))],
            unscheduled: [Row("Organizar documentação")],
            completed: [Row("Revisar PR")]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Select(section => section.Header)
            .Should().Equal("ATRASADAS", "AGORA", "HOJE", "SEM HORÁRIO", "CONCLUÍDAS");
    }

    [Fact]
    public async Task Load_HidesEmptySectionsInsteadOfShowingEmptyHeaders()
    {
        _runner.Result = Board(today: [Row("Deploy", new TimeOnly(15, 30))]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Select(section => section.Header).Should().Equal("HOJE");
    }

    [Fact]
    public async Task Load_ShowsTheScheduledTimeBesideTheTask()
    {
        _runner.Result = Board(today: [Row("Deploy", new TimeOnly(15, 30))]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Single().Items.Single().TimeLabel.Should().Be("15:30");
    }

    [Fact]
    public async Task Load_LeavesTheTimeBlankForWorkWithoutAnHour()
    {
        _runner.Result = Board(unscheduled: [Row("Organizar documentação")]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Single().Items.Single().TimeLabel.Should().BeEmpty();
    }

    [Fact]
    public async Task Load_CarriesTheLateFlagToTheRow()
    {
        _runner.Result = Board(today: [Row("Daily", new TimeOnly(9, 0), isLate: true)]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Single().Items.Single().IsLate.Should().BeTrue();
    }

    [Fact]
    public async Task Load_MarksCompletedRowsAsDone()
    {
        _runner.Result = Board(completed: [Row("Revisar PR")]);

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Single().Items.Single().IsCompleted.Should().BeTrue();
    }

    [Fact]
    public async Task Load_ReplacesThePreviousContentInsteadOfAppending()
    {
        _runner.Result = Board(today: [Row("Deploy", new TimeOnly(15, 30))]);
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);
        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Should().ContainSingle();
        viewModel.Sections.Single().Items.Should().ContainSingle();
    }

    [Fact]
    public async Task Load_ClearsTheBusyFlagWhenItFinishes()
    {
        _runner.Result = Board();

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task TogglingAPendingRow_AsksToCompleteIt()
    {
        _runner.Result = Board(today: [Row("Deploy", new TimeOnly(15, 30))]);
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        await viewModel.ToggleAsync(viewModel.Sections.Single().Items.Single(), Ct);

        _runner.Invoked.Should().Contain(typeof(CompleteOccurrenceHandler));
    }

    [Fact]
    public async Task TogglingACompletedRow_AsksToReopenIt()
    {
        _runner.Result = Board(completed: [Row("Revisar PR")]);
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        await viewModel.ToggleAsync(viewModel.Sections.Single().Items.Single(), Ct);

        _runner.Invoked.Should().Contain(typeof(ReopenOccurrenceHandler));
    }

    [Fact]
    public async Task Toggling_RefreshesTheBoardSoSectionsStayAccurate()
    {
        _runner.Result = Board(today: [Row("Deploy", new TimeOnly(15, 30))]);
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        await viewModel.ToggleAsync(viewModel.Sections.Single().Items.Single(), Ct);

        _runner.LastInvoked.Should().Be(typeof(GetTodayBoardHandler));
    }

    [Fact]
    public async Task BusinessFailure_IsShownToTheUserAsWritten()
    {
        _runner.Result = Board();
        _runner.NextFailure = new DomainException("Esta ocorrência já foi concluída.");

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.ErrorMessage.Should().Be("Esta ocorrência já foi concluída.");
    }

    [Fact]
    public async Task InfrastructureFailure_IsReplacedByAMessageTheUserUnderstands()
    {
        _runner.Result = Board();
        _runner.NextFailure = new InvalidOperationException("SQLite Error 14: unable to open database file");

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.ErrorMessage.Should().Be("Não foi possível carregar suas tarefas.");
        viewModel.ErrorMessage.Should().NotContain("SQLite");
    }

    [Fact]
    public async Task Failure_DoesNotLeaveTheScreenStuckLoading()
    {
        _runner.Result = Board();
        _runner.NextFailure = new InvalidOperationException("falha qualquer");

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.IsBusy.Should().BeFalse();
    }

    [Fact]
    public async Task ASuccessfulReload_ClearsAPreviousError()
    {
        _runner.Result = Board();
        var viewModel = ViewModel();
        _runner.NextFailure = new DomainException("erro anterior");
        await viewModel.LoadAsync(Ct);

        await viewModel.LoadAsync(Ct);

        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task EmptyDay_IsReportedInsteadOfShowingNothing()
    {
        _runner.Result = Board();

        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.Sections.Should().BeEmpty();
        viewModel.IsEmpty.Should().BeTrue();
    }
}
