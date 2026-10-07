using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.History;
using MyTaskApp.Application.Planning;
using MyTaskApp.Desktop.ViewModels;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>O painel "Histórico · 7 dias" sem tela (ADR-053).</summary>
public class ActivityHistoryViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();

    private ActivityHistoryViewModel ViewModel() => new(_runner, NullLogger.Instance);

    private static readonly ActivityItemView Line =
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), "Corrigir API", true, "Concluída 14:31");

    [Fact]
    public async Task Loading_CopiesTheDaysAndTheSummary()
    {
        var day = new ActivityDayView(new DateOnly(2026, 10, 6), "HOJE · TER 06/10", [Line]);
        _runner.ResultsByHandler[typeof(GetActivityHistoryHandler)] =
            new ActivityHistoryView([day], 1, TimeSpan.Zero, "1 concluída · 0min");
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);

        viewModel.Days.Should().Equal(day);
        viewModel.Summary.Should().Be("1 concluída · 0min");
        viewModel.IsLoading.Should().BeFalse();
        viewModel.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task AFailedLoad_SaysSo_WithoutTheTechnicalDetail()
    {
        _runner.FailuresByHandler[typeof(GetActivityHistoryHandler)] = new InvalidOperationException("SQLite Error 5");
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);

        viewModel.ErrorMessage.Should().Be("Não foi possível carregar o histórico.");
        viewModel.IsLoading.Should().BeFalse();
    }

    [Fact]
    public async Task ATaskThatIsGone_StaysInThePanel_WithAMessage()
    {
        _runner.Enqueue<GetTodayBoardHandler>((object?)null);
        var viewModel = ViewModel();
        var closed = false;
        var opened = false;
        viewModel.CloseRequested += () => closed = true;
        viewModel.OpenRequested += _ => opened = true;

        await viewModel.OpenAsync(Line, Ct);

        viewModel.ErrorMessage.Should().Be("Esta tarefa não está mais disponível.");
        closed.Should().BeFalse();
        opened.Should().BeFalse();
    }

    [Fact]
    public async Task Opening_AsksForTheOccurrenceOfThatLine_NotTheSeries()
    {
        var query = new RecordingQuery();
        var options = Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" });
        var clock = new UserClock(TimeProvider.System, options, NullLogger<UserClock>.Instance);
        _runner.Handlers[typeof(GetTodayBoardHandler)] = new GetTodayBoardHandler(query, clock, TimeProvider.System, options);

        await ViewModel().OpenAsync(Line, Ct);

        query.Requested.Should().Be(Line.OccurrenceId);
    }

    [Fact]
    public void TheHeading_IsTheWeek()
    {
        ViewModel().Heading.Should().Be("Histórico · 7 dias");
    }

    private sealed class RecordingQuery : ITodayQuery
    {
        public Guid? Requested { get; private set; }

        public Task<IReadOnlyList<TodayOccurrenceRow>> GetCandidatesAsync(
            DateOnly today,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<TodayOccurrenceRow>>([]);

        public Task<TodayOccurrenceRow?> FindOccurrenceAsync(
            Guid occurrenceId,
            CancellationToken cancellationToken = default)
        {
            Requested = occurrenceId;
            return Task.FromResult<TodayOccurrenceRow?>(null);
        }
    }
}
