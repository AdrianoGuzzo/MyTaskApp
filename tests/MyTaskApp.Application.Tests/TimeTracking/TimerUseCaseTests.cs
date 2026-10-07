using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Application.Tests.TimeTracking;

/// <summary>
/// ▶ e ⏹ (ADR-052): um cronômetro no app inteiro, conferido contra o estado
/// gravado, e um tempo que não depende do processo ficar aberto.
/// </summary>
public class TimerUseCaseTests
{
    // Terça, 06/10/2026, 14:00 em São Paulo.
    private static readonly DateTimeOffset At14 = new(2026, 10, 6, 17, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(At14);
    private readonly FakeTaskItemRepository _tasks = new();
    private readonly FakeTimeEntryRepository _entries = new();

    private StartTimerHandler Start() =>
        new(_tasks, _entries, _tasks, TestClock.Over(_time), _time, NullLogger<StartTimerHandler>.Instance);

    private StopTimerHandler Stop() =>
        new(_entries, _tasks, _time, NullLogger<StopTimerHandler>.Instance);

    private GetActiveTimerHandler Active() => new(new FakeActiveTimerQuery(_entries, _tasks));

    private (TaskItem Task, Guid OccurrenceId) Seed(string title)
    {
        var task = TaskItem.Create(title, At14.AddDays(-1));
        _tasks.Seed(task);
        return (task, task.Occurrences.Single().Id);
    }

    [Fact]
    public async Task Start_PersistsAnOpenTimerEntry_RightAway()
    {
        var (task, occurrenceId) = Seed("Implementar autenticação");

        var view = await Start().HandleAsync(new StartTimer(occurrenceId), Ct);

        var entry = _entries.Entries.Single();
        entry.TaskOccurrenceId.Should().Be(occurrenceId);
        entry.StartedAt.Should().Be(At14);
        entry.EndedAt.Should().BeNull();
        entry.Source.Should().Be(TimeEntrySource.Timer);
        _tasks.SaveCount.Should().Be(1, "o início não fica só em memória");

        view.Should().Be(new ActiveTimerView(entry.Id, occurrenceId, task.Id, "Implementar autenticação", At14));
    }

    [Fact]
    public async Task StartingTwice_KeepsTheFirstStart()
    {
        var (_, occurrenceId) = Seed("Implementar autenticação");
        await Start().HandleAsync(new StartTimer(occurrenceId), Ct);
        _time.Advance(TimeSpan.FromSeconds(1));

        var second = await Start().HandleAsync(new StartTimer(occurrenceId), Ct);

        _entries.Entries.Should().ContainSingle();
        second.StartedAt.Should().Be(At14);
        _tasks.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task StartingAnotherTask_WhileOneRuns_IsRefused_AndNothingIsSaved()
    {
        var (_, first) = Seed("Implementar autenticação");
        var (_, second) = Seed("Corrigir dashboard");
        await Start().HandleAsync(new StartTimer(first), Ct);

        var start = () => Start().HandleAsync(new StartTimer(second), Ct);

        await start.Should().ThrowAsync<DomainException>().WithMessage("*\"Implementar autenticação\"*");
        _entries.Entries.Should().ContainSingle().Which.IsActive.Should().BeTrue();
        _tasks.SaveCount.Should().Be(1);
    }

    [Fact]
    public async Task StopAndStart_ClosesTheRunningOne_AndStartsTheNew_InOneSave()
    {
        var (_, first) = Seed("Implementar autenticação");
        var (_, second) = Seed("Corrigir dashboard");
        await Start().HandleAsync(new StartTimer(first), Ct);
        _time.Advance(TimeSpan.FromMinutes(40));

        var view = await Start().HandleAsync(new StartTimer(second, ReplaceRunning: true), Ct);

        var previous = _entries.Entries.Single(entry => entry.TaskOccurrenceId == first);
        previous.EndedAt.Should().Be(At14.AddMinutes(40));
        var current = _entries.Entries.Single(entry => entry.TaskOccurrenceId == second);
        current.IsActive.Should().BeTrue();
        current.StartedAt.Should().Be(At14.AddMinutes(40));
        view.TaskTitle.Should().Be("Corrigir dashboard");
        _entries.Entries.Count(entry => entry.IsActive).Should().Be(1);
        _tasks.SaveCount.Should().Be(2);
    }

    [Fact]
    public async Task ACompletedTask_CannotBeStarted()
    {
        var (task, occurrenceId) = Seed("Implementar autenticação");
        task.CompleteOccurrence(occurrenceId, At14);

        var start = () => Start().HandleAsync(new StartTimer(occurrenceId), Ct);

        await start.Should().ThrowAsync<DomainException>();
        _entries.Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Stop_ClosesThePeriod_AtNow()
    {
        var (_, occurrenceId) = Seed("Implementar autenticação");
        await Start().HandleAsync(new StartTimer(occurrenceId), Ct);
        _time.Advance(TimeSpan.FromMinutes(76));

        await Stop().HandleAsync(new StopTimer(occurrenceId), Ct);

        var entry = _entries.Entries.Single();
        entry.EndedAt.Should().Be(At14.AddMinutes(76));
        entry.Duration(_time.GetUtcNow()).Should().Be(TimeSpan.FromMinutes(76));
        _tasks.SaveCount.Should().Be(2);
    }

    [Fact]
    public async Task StopWithNothingRunning_IsNotAnError()
    {
        var (_, occurrenceId) = Seed("Implementar autenticação");

        await Stop().HandleAsync(new StopTimer(occurrenceId), Ct);

        _tasks.SaveCount.Should().Be(0);
    }

    [Fact]
    public async Task StopOnAnotherTask_LeavesTheRunningOneAlone()
    {
        var (_, running) = Seed("Implementar autenticação");
        var (_, other) = Seed("Corrigir dashboard");
        await Start().HandleAsync(new StartTimer(running), Ct);

        await Stop().HandleAsync(new StopTimer(other), Ct);

        _entries.Entries.Single().IsActive.Should().BeTrue();
    }

    [Fact]
    public async Task StopAfterTheClockWentBack_DiscardsThePeriod_InsteadOfTrappingTheUser()
    {
        var (_, occurrenceId) = Seed("Implementar autenticação");

        // Iniciado "às 14:05" e parado às 14:00: o relógio do sistema voltou
        // cinco minutos no meio (o FakeTimeProvider não volta, então o início
        // vai para a frente).
        _entries.Seed(TimeEntry.StartTimer(occurrenceId, At14.AddMinutes(5)));

        await Stop().HandleAsync(new StopTimer(occurrenceId), Ct);

        _entries.Entries.Should().BeEmpty();
        (await Active().HandleAsync(Ct)).Should().BeNull();
    }

    /// <summary>
    /// 14:00 inicia; 15:30 o app fecha; 16:00 abre de novo. O que sobrevive é o
    /// que estava gravado — os handlers são outros, o processo é outro.
    /// </summary>
    [Fact]
    public async Task ClosingAndReopeningTheApp_KeepsTheTimerRunningFromItsStart()
    {
        var (task, occurrenceId) = Seed("Implementar autenticação");
        await Start().HandleAsync(new StartTimer(occurrenceId), Ct);

        _time.SetUtcNow(At14.AddMinutes(90)); // o app é encerrado aqui; nada é escrito
        _time.SetUtcNow(At14.AddHours(2)); // e aberto aqui

        var restored = await new GetActiveTimerHandler(new FakeActiveTimerQuery(_entries, _tasks)).HandleAsync(Ct);

        restored.Should().NotBeNull();
        restored!.StartedAt.Should().Be(At14);
        restored.TaskId.Should().Be(task.Id);
        _entries.Entries.Single().EndedAt.Should().BeNull();
        (_time.GetUtcNow() - restored.StartedAt).Should().Be(TimeSpan.FromHours(2));
        _tasks.SaveCount.Should().Be(1, "nada é gravado enquanto o cronômetro corre");
    }

    /// <summary>14:00 inicia; 14:30 o computador suspende; 15:30 volta; ⏹.</summary>
    [Fact]
    public async Task Suspending_CountsTheWholeSpan_NotJustTheAwakeTime()
    {
        var (_, occurrenceId) = Seed("Implementar autenticação");
        await Start().HandleAsync(new StartTimer(occurrenceId), Ct);

        _time.Advance(TimeSpan.FromMinutes(30));
        _time.Advance(TimeSpan.FromHours(1)); // dormindo: nenhum tique, nenhum timer

        await Stop().HandleAsync(new StopTimer(occurrenceId), Ct);

        _entries.Entries.Single().Duration(_time.GetUtcNow()).Should().Be(TimeSpan.FromMinutes(90));
    }

    [Fact]
    public async Task GetActiveTimer_WithNothingRunning_IsNull() =>
        (await Active().HandleAsync(Ct)).Should().BeNull();
}
