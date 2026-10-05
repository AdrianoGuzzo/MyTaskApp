using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Deadlines;

/// <summary>"Personalizado": o dia e a hora escolhidos no editor (§8).</summary>
public sealed record SetDeadline(Guid OccurrenceId, DateOnly Date, TimeOnly Time);

/// <summary>Um atalho do menu da linha ou do aviso: "Amanhã", "+3 dias" (§9, §23).</summary>
public sealed record SetDeadlineShortcut(Guid OccurrenceId, DeadlineShortcut Shortcut);

public sealed class SetDeadlineHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    IDeadlineSettingsStore settings,
    IDeadlineAlertPresenter presenter,
    IUserClock clock,
    TimeProvider timeProvider,
    ILogger<SetDeadlineHandler> logger)
{
    public Task<TaskDeadlineView> HandleAsync(
        SetDeadline command,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(command.OccurrenceId, _ => new TaskDeadline(command.Date, command.Time), cancellationToken);

    public Task<TaskDeadlineView> HandleAsync(
        SetDeadlineShortcut command,
        CancellationToken cancellationToken = default) =>
        ApplyAsync(
            command.OccurrenceId,
            context => DeadlineShortcuts.Resolve(
                command.Shortcut,
                clock.Today,
                clock.CurrentTime,
                // "+1 dia" não mexe na hora que o usuário já escolheu.
                context.Current?.Time ?? context.Settings.DefaultTime),
            cancellationToken);

    private async Task<TaskDeadlineView> ApplyAsync(
        Guid occurrenceId,
        Func<(TaskDeadline? Current, DeadlineSettings Settings), TaskDeadline> choose,
        CancellationToken cancellationToken)
    {
        var task = await tasks.GetByOccurrenceIdAsync(occurrenceId, cancellationToken);
        var stored = await settings.GetAsync(cancellationToken);
        var deadline = choose((task.GetOccurrence(occurrenceId).Deadline, stored));

        // Um prazo que já nasce vencido não é prazo, é engano de data — e
        // aceitá-lo faria a tarefa cair em ATRASADAS no mesmo clique.
        if (deadline.HasPassed(clock.Today, clock.CurrentTime))
        {
            throw new DomainException("O prazo precisa ficar no futuro.");
        }

        var now = timeProvider.GetUtcNow();
        var initialStage = DeadlineAlerting.CurrentStage(
            stored.Alerts.StagesFor(task.DeadlineAlerts),
            clock.ToInstant(deadline.Date, deadline.Time),
            now);

        task.SetOccurrenceDeadline(occurrenceId, deadline, initialStage);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        // O aviso do prazo antigo, se estiver na tela, fala de um prazo que não existe mais.
        await presenter.DismissAsync(occurrenceId, cancellationToken);

        logger.LogInformation(
            "DeadlineSet {TaskId} {OccurrenceId} {Deadline} {InitialStage}",
            task.Id,
            occurrenceId,
            deadline,
            initialStage);

        return TaskDeadlineView.Describe(deadline, clock, now);
    }
}

/// <summary>"Remover prazo": a tarefa volta a ser uma tarefa comum.</summary>
public sealed record ClearDeadline(Guid OccurrenceId);

public sealed class ClearDeadlineHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    IDeadlineAlertPresenter presenter,
    ILogger<ClearDeadlineHandler> logger)
{
    public async Task HandleAsync(ClearDeadline command, CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByOccurrenceIdAsync(command.OccurrenceId, cancellationToken);

        task.ClearOccurrenceDeadline(command.OccurrenceId);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await presenter.DismissAsync(command.OccurrenceId, cancellationToken);

        logger.LogInformation("DeadlineCleared {TaskId} {OccurrenceId}", task.Id, command.OccurrenceId);
    }
}

/// <summary>
/// "Adiar 1 h" no aviso do prazo. Adia o <b>aviso</b>, e o prazo fica onde
/// estava (§21) — quem quer mais tempo muda o prazo.
/// </summary>
public sealed record SnoozeDeadlineAlert(Guid OccurrenceId, TimeSpan For);

public sealed class SnoozeDeadlineAlertHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    IDeadlineAlertPresenter presenter,
    TimeProvider timeProvider,
    ILogger<SnoozeDeadlineAlertHandler> logger)
{
    public static readonly TimeSpan MinSnooze = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxSnooze = TimeSpan.FromDays(1);

    public async Task HandleAsync(SnoozeDeadlineAlert command, CancellationToken cancellationToken = default)
    {
        if (command.For < MinSnooze || command.For > MaxSnooze)
        {
            throw new DomainException("O aviso do prazo pode ser adiado de 1 minuto a 24 horas.");
        }

        var task = await tasks.GetByOccurrenceIdAsync(command.OccurrenceId, cancellationToken);
        var until = timeProvider.GetUtcNow() + command.For;

        task.SnoozeDeadlineAlert(command.OccurrenceId, until);

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await presenter.DismissAsync(command.OccurrenceId, cancellationToken);

        logger.LogInformation(
            "DeadlineAlertSnoozed {TaskId} {OccurrenceId} {Until}",
            task.Id,
            command.OccurrenceId,
            until);
    }
}

/// <summary>
/// Os avisos de prazo desta tarefa (§20). <c>null</c> volta ao padrão global,
/// <see cref="DeadlineAlertStage.None"/> silencia.
/// </summary>
public sealed record SetTaskDeadlineAlerts(Guid TaskId, DeadlineAlertStage? Alerts);

public sealed class SetTaskDeadlineAlertsHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork)
{
    public async Task HandleAsync(SetTaskDeadlineAlerts command, CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);

        task.ChangeDeadlineAlerts(command.Alerts);

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>A próxima ação e a estimativa de uma tarefa longa (§14, §15).</summary>
public sealed record UpdateTaskPlan(Guid TaskId, string? NextAction, TimeSpan? Estimate);

public sealed class UpdateTaskPlanHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork)
{
    public async Task HandleAsync(UpdateTaskPlan command, CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);

        task.ChangePlan(command.NextAction, command.Estimate);

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
