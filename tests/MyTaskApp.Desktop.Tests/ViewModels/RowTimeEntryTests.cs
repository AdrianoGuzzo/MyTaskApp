using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;
using NSubstitute;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// "Lançar horas" no menu da linha (ADR-052): os atalhos gravam o período que
/// acabou agora, num clique, e "Outro período…" abre o diálogo da aba Tempo. O
/// caso de uso é o de verdade, para conferir o período que chegou ao banco.
/// </summary>
public class RowTimeEntryTests
{
    // Terça, 06/10/2026, 14:00:37 em São Paulo.
    private static readonly DateTimeOffset At14 = new(2026, 10, 6, 17, 0, 37, TimeSpan.Zero);
    private static readonly DateOnly Date = new(2026, 10, 6);
    private static readonly TimeSpan SaoPaulo = TimeSpan.FromHours(-3);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();
    private readonly FakeTimeEntryEditor _editor = new();
    private readonly FakeTimeProvider _time = new(At14);
    private readonly ITimeEntryRepository _entries = Substitute.For<ITimeEntryRepository>();
    private readonly TaskItem _task = TaskItem.Create("Implementar autenticação", At14.AddDays(-1));
    private readonly UserClock _clock;

    public RowTimeEntryTests()
    {
        _clock = new UserClock(_time, Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" }), NullLogger<UserClock>.Instance);

        var tasks = Substitute.For<ITaskItemRepository>();
        tasks.FindByOccurrenceIdAsync(Occurrence, Arg.Any<CancellationToken>()).Returns(_task);
        _entries.ListForOccurrenceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);

        _runner.Handlers[typeof(AddTimeEntryHandler)] = new AddTimeEntryHandler(
            tasks, _entries, Substitute.For<IUnitOfWork>(), Substitute.For<ITaskAuditLog>(), Substitute.For<ICurrentUser>(),
            _clock, _time, NullLogger<AddTimeEntryHandler>.Instance);
        _runner.Result = new TodayBoard(Date, [], [], [], [Row()], []);
    }

    private Guid Occurrence => _task.Occurrences.Single().Id;

    private TodayTask Row() =>
        new(Occurrence, _task.Id, _task.Title, TaskPriority.Normal, Date, null, false);

    private async Task<(TodayViewModel ViewModel, TaskRowViewModel Row)> TodayAsync()
    {
        var viewModel = new TodayViewModel(
            _runner, new FakeConfirmationDialog(), new FakeClipboardWriter(), _time, NullLogger<TodayViewModel>.Instance,
            activeTimer: new ActiveTimerViewModel(_time), userClock: _clock, timeEntryEditor: _editor);
        await viewModel.LoadAsync(Ct);

        return (viewModel, viewModel.Sections.Single().Items.Single());
    }

    private static DateTimeOffset Local(int day, int hour, int minute) =>
        new(2026, 10, day, hour, minute, 0, SaoPaulo);

    [Fact]
    public async Task AShortcut_LogsThePeriodThatJustEnded_ToTheMinute()
    {
        var (viewModel, row) = await TodayAsync();

        await viewModel.LogRecentTimeCommand.ExecuteAsync(new RecentTimeRequest(row, TimeSpan.FromMinutes(30)));

        await _entries.Received(1).AddAsync(
            Arg.Is<TimeEntry>(entry =>
                entry.TaskOccurrenceId == Occurrence
                && entry.StartedAt == Local(6, 13, 30)
                && entry.EndedAt == Local(6, 14, 0)
                && entry.Source == TimeEntrySource.Manual),
            Arg.Any<CancellationToken>());
        viewModel.ErrorMessage.Should().BeNull();
        viewModel.StatusMessage.Should().Be("30min lançados (13:30 → 14:00).");
        _runner.LastInvoked.Should().Be(typeof(GetTodayBoardHandler), "o total da linha precisa aparecer");
    }

    /// <summary>Duas horas às 00:40 começam ontem: o período atravessa a meia-noite, não é cortado nela.</summary>
    [Fact]
    public async Task AShortcut_JustAfterMidnight_StartsYesterday()
    {
        _time.SetUtcNow(new DateTimeOffset(2026, 10, 7, 3, 40, 0, TimeSpan.Zero));
        var (viewModel, row) = await TodayAsync();

        await viewModel.LogRecentTimeCommand.ExecuteAsync(new RecentTimeRequest(row, TimeSpan.FromHours(2)));

        await _entries.Received(1).AddAsync(
            Arg.Is<TimeEntry>(entry => entry.StartedAt == Local(6, 22, 40) && entry.EndedAt == Local(7, 0, 40)),
            Arg.Any<CancellationToken>());
        viewModel.StatusMessage.Should().Be("2h lançados (06/10 22:40 → 00:40).");
    }

    [Fact]
    public async Task AShortcut_OverlappingAnotherPeriod_ShowsTheRefusal_AndSavesNothing()
    {
        _entries.ListForOccurrenceAsync(Occurrence, Arg.Any<CancellationToken>())
            .Returns([TimeEntry.StartTimer(Occurrence, At14.AddMinutes(-10))]);
        var (viewModel, row) = await TodayAsync();

        await viewModel.LogRecentTimeCommand.ExecuteAsync(new RecentTimeRequest(row, TimeSpan.FromMinutes(15)));

        await _entries.DidNotReceive().AddAsync(Arg.Any<TimeEntry>(), Arg.Any<CancellationToken>());
        viewModel.ErrorMessage.Should().NotBeNullOrEmpty();
        viewModel.StatusMessage.Should().BeNull();
    }

    [Fact]
    public async Task AnotherPeriod_OpensTheAddDialog_AndSavesWhatWasTyped()
    {
        _editor.Answer = draft => draft with { StartTime = new TimeOnly(9, 0), EndTime = new TimeOnly(10, 30), Note = "Revisão" };
        var (viewModel, row) = await TodayAsync();

        await viewModel.AddTimeEntryCommand.ExecuteAsync(row);

        var asked = _editor.Asked.Single();
        asked.Heading.Should().Be("Adicionar tempo");
        asked.Initial.Should().Be(new TimeEntryDraft(Date, new TimeOnly(13, 0), Date, new TimeOnly(14, 0), null));
        await _entries.Received(1).AddAsync(
            Arg.Is<TimeEntry>(entry =>
                entry.StartedAt == Local(6, 9, 0) && entry.EndedAt == Local(6, 10, 30) && entry.Note == "Revisão"),
            Arg.Any<CancellationToken>());
        viewModel.StatusMessage.Should().Be("1h 30min lançados (09:00 → 10:30).");
    }

    [Fact]
    public async Task AnotherPeriod_Refused_KeepsTheDialogOpen_WithTheMessage()
    {
        _entries.ListForOccurrenceAsync(Occurrence, Arg.Any<CancellationToken>())
            .Returns([TimeEntry.StartTimer(Occurrence, At14.AddMinutes(-10))]);
        _editor.Answer = draft => draft;
        var (viewModel, row) = await TodayAsync();

        await viewModel.AddTimeEntryCommand.ExecuteAsync(row);

        _editor.LastError.Should().NotBeNullOrEmpty();
        viewModel.StatusMessage.Should().BeNull();
    }

    [Fact]
    public async Task AnotherPeriod_Cancelled_SavesNothing()
    {
        var (viewModel, row) = await TodayAsync();

        await viewModel.AddTimeEntryCommand.ExecuteAsync(row);

        _editor.Asked.Should().ContainSingle();
        _runner.Invoked.Should().NotContain(typeof(AddTimeEntryHandler));
        viewModel.StatusMessage.Should().BeNull();
    }
}
