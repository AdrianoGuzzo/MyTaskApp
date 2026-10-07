using Avalonia.Headless.XUnit;
using Avalonia.Threading;
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
/// O cronômetro na lista (ADR-052): a linha só copia o que a Application montou,
/// a troca de tarefa pergunta pelo nome das duas, e o relógio tica sem pedir
/// nada a ninguém.
/// </summary>
public class TimeTrackingViewModelTests
{
    private static readonly DateOnly Date = new(2026, 10, 6);
    private static readonly DateTimeOffset At14 = new(2026, 10, 6, 17, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeUseCaseRunner _runner = new();
    private readonly FakeConfirmationDialog _confirmation = new();
    private readonly FakeTimeProvider _time = new(At14);

    private TodayViewModel Today() =>
        new(_runner, _confirmation, new FakeClipboardWriter(), _time, NullLogger<TodayViewModel>.Instance,
            activeTimer: new ActiveTimerViewModel(_time));

    private static TodayTask Task(
        string title,
        TimeSpan logged = default,
        DateTimeOffset? timerStartedAt = null) =>
        new(Guid.CreateVersion7(), Guid.CreateVersion7(), title, TaskPriority.Normal, Date, null, false,
            Logged: logged, TimerStartedAt: timerStartedAt);

    private static TodayBoard Board(IReadOnlyList<TodayTask> unscheduled, ActiveTimerView? active = null) =>
        new(Date, [], [], [], unscheduled, []) { ActiveTimer = active };

    private static ActiveTimerView Running(TodayTask task, DateTimeOffset since) =>
        new(Guid.CreateVersion7(), task.OccurrenceId, task.TaskId, task.Title, since);

    [Fact]
    public void AnUntrackedRow_IsJustThePlayIcon()
    {
        var row = new TaskRowViewModel(Task("Comprar pão"), isCompleted: false);

        row.IsTiming.Should().BeFalse();
        row.HasLoggedTime.Should().BeFalse();
        row.LoggedText.Should().BeEmpty();
        row.TimerGlyph.Should().Be(TimerGlyphs.Play);
        row.TimerTip.Should().Be("Iniciar o cronômetro");
        row.CanToggleTimer.Should().BeTrue();
    }

    [Fact]
    public void ARowWithTime_ShowsTheTotal_AndARunningOneShowsStop()
    {
        var logged = new TaskRowViewModel(Task("Autenticação", TimeSpan.FromMinutes(272)), isCompleted: false);
        var running = new TaskRowViewModel(Task("Dashboard", timerStartedAt: At14), isCompleted: false);

        logged.LoggedText.Should().Be("4h 32min");
        logged.TimerTip.Should().Be("Iniciar o cronômetro · 4h 32min registrados");
        running.IsTiming.Should().BeTrue();
        running.TimerGlyph.Should().Be(TimerGlyphs.Stop);
        running.TimerTip.Should().Be("Parar o cronômetro");
    }

    [Fact]
    public void ACompletedRow_ShowsItsTotal_ButCannotStart()
    {
        var withTime = new TaskRowViewModel(Task("Autenticação", TimeSpan.FromHours(2)), isCompleted: true);
        var without = new TaskRowViewModel(Task("Pão"), isCompleted: true);

        withTime.CanToggleTimer.Should().BeFalse();
        withTime.ShowsTimer.Should().BeTrue();
        withTime.TimerTip.Should().Be("Tempo gasto: 2h");
        without.ShowsTimer.Should().BeFalse();
    }

    [Fact]
    public async Task Play_WithNothingRunning_StartsWithoutAsking()
    {
        var task = Task("Implementar autenticação");
        _runner.Result = Board([task]);
        _runner.Enqueue<GetActiveTimerHandler>((object?)null);
        _runner.ResultsByHandler[typeof(StartTimerHandler)] = Running(task, At14);
        var viewModel = Today();
        await viewModel.LoadAsync(Ct);

        await viewModel.ToggleTimerCommand.ExecuteAsync(viewModel.Sections.Single().Items.Single());

        _confirmation.Asked.Should().BeEmpty();
        _runner.Invoked.Should().ContainInOrder(typeof(GetActiveTimerHandler), typeof(StartTimerHandler), typeof(GetTodayBoardHandler));
    }

    [Fact]
    public async Task Play_WhileAnotherRuns_AsksByName_AndCancellingStartsNothing()
    {
        var running = Task("Implementar autenticação", timerStartedAt: At14);
        var next = Task("Corrigir dashboard");
        _runner.Result = Board([running, next]);
        _runner.Enqueue<GetActiveTimerHandler>(Running(running, At14));
        _confirmation.Answer = false;
        var viewModel = Today();
        await viewModel.LoadAsync(Ct);

        await viewModel.ToggleTimerCommand.ExecuteAsync(viewModel.Sections.Single().Items[1]);

        var asked = _confirmation.LastAsked!;
        asked.ConfirmLabel.Should().Be("Parar e iniciar");
        asked.Message.Should().Contain("Você está trabalhando em:")
            .And.Contain("\"Implementar autenticação\"")
            .And.Contain("Deseja parar essa tarefa e iniciar:")
            .And.Contain("\"Corrigir dashboard\"?");
        _runner.Invoked.Should().NotContain(typeof(StartTimerHandler));
    }

    /// <summary>
    /// O "Parar e iniciar" chega ao caso de uso de verdade como troca: sem a
    /// flag, ele recusaria — o outro cronômetro está correndo no banco.
    /// </summary>
    [Fact]
    public async Task Play_WhileAnotherRuns_AndConfirming_ReplacesIt()
    {
        var first = TaskItem.Create("Implementar autenticação", At14.AddDays(-1));
        var second = TaskItem.Create("Corrigir dashboard", At14.AddDays(-1));
        var firstOccurrence = first.Occurrences.Single().Id;
        var secondOccurrence = second.Occurrences.Single().Id;
        var runningEntry = TimeEntry.StartTimer(firstOccurrence, At14.AddHours(-1));

        var tasks = Substitute.For<ITaskItemRepository>();
        tasks.FindByOccurrenceIdAsync(firstOccurrence, Arg.Any<CancellationToken>()).Returns(first);
        tasks.FindByOccurrenceIdAsync(secondOccurrence, Arg.Any<CancellationToken>()).Returns(second);
        var entries = Substitute.For<ITimeEntryRepository>();
        entries.FindActiveAsync(Arg.Any<CancellationToken>()).Returns(runningEntry);
        entries.ListForOccurrenceAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        var clock = new UserClock(_time, Options.Create(new ApplicationOptions { TimeZoneId = "America/Sao_Paulo" }), NullLogger<UserClock>.Instance);
        _runner.Handlers[typeof(StartTimerHandler)] = new StartTimerHandler(
            tasks, entries, Substitute.For<IUnitOfWork>(), clock, _time, NullLogger<StartTimerHandler>.Instance);

        var runningRow = new TodayTask(firstOccurrence, first.Id, first.Title, TaskPriority.Normal, Date, null, false, TimerStartedAt: runningEntry.StartedAt);
        var nextRow = new TodayTask(secondOccurrence, second.Id, second.Title, TaskPriority.Normal, Date, null, false);
        _runner.Result = Board([runningRow, nextRow]);
        _runner.Enqueue<GetActiveTimerHandler>(new ActiveTimerView(runningEntry.Id, firstOccurrence, first.Id, first.Title, runningEntry.StartedAt));
        _confirmation.Answer = true;
        var viewModel = Today();
        await viewModel.LoadAsync(Ct);

        await viewModel.ToggleTimerCommand.ExecuteAsync(viewModel.Sections.Single().Items[1]);

        viewModel.ErrorMessage.Should().BeNull();
        runningEntry.EndedAt.Should().Be(At14);
        await entries.Received(1).AddAsync(
            Arg.Is<TimeEntry>(entry => entry.TaskOccurrenceId == secondOccurrence && entry.IsActive),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Stop_OnTheRunningRow_StopsThatTask()
    {
        var running = Task("Implementar autenticação", timerStartedAt: At14);
        _runner.Result = Board([running], Running(running, At14));
        var viewModel = Today();
        await viewModel.LoadAsync(Ct);

        await viewModel.ToggleTimerCommand.ExecuteAsync(viewModel.Sections.Single().Items.Single());

        _runner.Invoked.Should().Contain(typeof(StopTimerHandler));
        _runner.Invoked.Should().NotContain(typeof(StartTimerHandler));
    }

    [Fact]
    public async Task ARefusedStart_ShowsTheMessage()
    {
        var task = Task("Implementar autenticação");
        _runner.Result = Board([task]);
        _runner.Enqueue<GetActiveTimerHandler>((object?)null);
        _runner.FailuresByHandler[typeof(StartTimerHandler)] =
            new DomainException("Só é possível cronometrar uma tarefa pendente. Reabra-a primeiro.");
        var viewModel = Today();
        await viewModel.LoadAsync(Ct);

        await viewModel.ToggleTimerCommand.ExecuteAsync(viewModel.Sections.Single().Items.Single());

        viewModel.ErrorMessage.Should().Contain("pendente");
    }

    /// <summary>O app abre e o banco tem um cronômetro: o primeiro quadro já o restaura.</summary>
    [Fact]
    public async Task OpeningTheApp_RestoresTheRunningTimer_FromItsStart()
    {
        var running = Task("Implementar autenticação", timerStartedAt: At14.AddHours(-2));
        _runner.Result = Board([running], Running(running, At14.AddHours(-2)));
        var viewModel = Today();

        await viewModel.LoadAsync(Ct);

        viewModel.ActiveTimer.IsRunning.Should().BeTrue();
        viewModel.ActiveTimer.ElapsedText.Should().Be("02:00:00");
        viewModel.ActiveTimerRow!.Title.Should().Be("Implementar autenticação");
        viewModel.IsActiveTimerOffBoard.Should().BeFalse();
    }

    [Fact]
    public async Task ARunningTimer_OfATaskNotOnTheBoard_ShowsTheStrip_AndItsStopWorks()
    {
        var elsewhere = Task("Planejar sprint");
        _runner.Result = Board([Task("Comprar pão")], Running(elsewhere, At14));
        var viewModel = Today();
        await viewModel.LoadAsync(Ct);

        viewModel.IsActiveTimerOffBoard.Should().BeTrue();
        viewModel.ActiveTimer.TaskTitle.Should().Be("Planejar sprint");

        await viewModel.StopActiveTimerCommand.ExecuteAsync(null);

        _runner.Invoked.Should().Contain(typeof(StopTimerHandler));
    }

    [Fact]
    public async Task WithNothingRunning_ThereIsNoStrip()
    {
        _runner.Result = Board([Task("Comprar pão")]);
        var viewModel = Today();

        await viewModel.LoadAsync(Ct);

        viewModel.ActiveTimer.IsRunning.Should().BeFalse();
        viewModel.IsActiveTimerOffBoard.Should().BeFalse();
    }

    /// <summary>O relógio é subtração: avança com o tempo, e nenhum caso de uso é chamado no tique.</summary>
    [AvaloniaFact]
    public async Task TheClock_TicksEverySecond_WithoutTouchingTheDatabase()
    {
        var running = Task("Implementar autenticação", timerStartedAt: At14);
        _runner.Result = Board([running], Running(running, At14));
        var viewModel = Today();
        await viewModel.LoadAsync(Ct);
        var calls = _runner.Invoked.Count;

        _time.Advance(TimeSpan.FromSeconds(1));
        Dispatcher.UIThread.RunJobs();
        viewModel.ActiveTimer.ElapsedText.Should().Be("00:00:01");

        _time.Advance(TimeSpan.FromSeconds(37 * 60 + 41));
        Dispatcher.UIThread.RunJobs();
        viewModel.ActiveTimer.ElapsedText.Should().Be("00:37:42");

        _runner.Invoked.Should().HaveCount(calls, "o tique só redesenha");
    }

    [AvaloniaFact]
    public void StoppingTheClock_StopsTheTicks()
    {
        var clock = new ActiveTimerViewModel(_time);
        var task = Task("Implementar autenticação");
        clock.Show(Running(task, At14));
        clock.Show(null);

        _time.Advance(TimeSpan.FromMinutes(5));
        Dispatcher.UIThread.RunJobs();

        clock.ElapsedText.Should().Be("00:00:00");
        clock.IsRunning.Should().BeFalse();
    }

    [Fact]
    public void TheClock_SaysWhenTheTimerChangedTask()
    {
        var clock = new ActiveTimerViewModel(_time);
        var first = Running(Task("A"), At14);
        var changes = new List<(ActiveTimerView?, ActiveTimerView?)>();
        clock.Changed += (previous, current) => changes.Add((previous, current));

        clock.Show(first);
        clock.Show(first with { });
        clock.Show(null);

        changes.Should().Equal((null, first), (first, null));
        clock.Dispose();
    }
}
