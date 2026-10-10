using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Application.Tags;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tasks;

/// <summary>
/// Um campo que muda, inclusive para "nenhum". <c>null</c> no lugar do
/// <see cref="Change{T}"/> é "não mexer"; <c>new Change&lt;string?&gt;(null)</c> é "apagar".
/// </summary>
public readonly record struct Change<T>(T Value);

/// <summary>
/// Editar só o que foi pedido de uma tarefa (ADR-059). A tela edita cada coisa
/// no seu card — título, prazo, etiquetas — e cada card chama o seu caso de uso;
/// quem edita tudo de uma vez, como o MCP, passa por aqui, que chama <b>os
/// mesmos</b> casos de uso numa transação só. Nenhuma regra é repetida: se o
/// prazo nasce vencido, quem recusa é o <see cref="SetDeadlineHandler"/>, e o
/// título que já tinha sido trocado volta junto.
/// </summary>
public sealed record EditTask(Guid TaskId)
{
    public string? Title { get; init; }

    public Change<string?>? Description { get; init; }

    public TaskPriority? Priority { get; init; }

    /// <summary>Data e hora juntas: hora sem data é recusada pelo domínio. <c>(null, null)</c> = sem data.</summary>
    public Change<(DateOnly? Date, TimeOnly? Time)>? Schedule { get; init; }

    /// <summary><c>Change(null)</c> remove o prazo.</summary>
    public Change<TaskDeadline?>? Deadline { get; init; }

    /// <summary><c>Change(null)</c> volta a seguir a configuração global.</summary>
    public Change<DeadlineAlertStage?>? DeadlineAlerts { get; init; }

    public Change<string?>? NextAction { get; init; }

    public Change<TimeSpan?>? Estimate { get; init; }

    /// <summary>O conjunto inteiro, como na tela: o que não estiver aqui sai.</summary>
    public IReadOnlyCollection<Guid>? TagIds { get; init; }

    public ReminderPolicy? Reminder { get; init; }

    public bool IsEmpty =>
        Title is null && Description is null && Priority is null && Schedule is null
        && Deadline is null && DeadlineAlerts is null && NextAction is null && Estimate is null
        && TagIds is null && Reminder is null;
}

public sealed class EditTaskHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    UpdateTaskHandler update,
    UpdateTaskPlanHandler plan,
    RescheduleOccurrenceHandler reschedule,
    SetDeadlineHandler setDeadline,
    ClearDeadlineHandler clearDeadline,
    SetTaskDeadlineAlertsHandler deadlineAlerts,
    SetTaskReminderHandler reminder,
    SetTaskTagsHandler tags,
    ILogger<EditTaskHandler> logger)
{
    public async Task HandleAsync(EditTask command, CancellationToken cancellationToken = default)
    {
        if (command.IsEmpty)
        {
            return;
        }

        await unitOfWork.ExecuteInTransactionAsync(
            token => ApplyAsync(command, token),
            cancellationToken);

        logger.LogInformation("TaskEdited {TaskId}", command.TaskId);
    }

    /// <summary>Sem transação própria: <see cref="CreateDetailedTaskHandler"/> chama de dentro da dele.</summary>
    internal async Task ApplyAsync(EditTask command, CancellationToken cancellationToken)
    {
        // O agregado rastreado do escopo: os casos de uso abaixo recebem o mesmo
        // objeto do repositório, e cada um enxerga o que o anterior mudou.
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);
        var occurrenceId = task.Occurrences.Single().Id;

        if (command.Title is not null || command.Description is not null || command.Priority is not null)
        {
            await update.HandleAsync(
                new UpdateTask(
                    task.Id,
                    command.Title ?? task.Title,
                    command.Description is { } description ? description.Value : task.Description,
                    command.Priority ?? task.Priority),
                cancellationToken);
        }

        if (command.NextAction is not null || command.Estimate is not null)
        {
            await plan.HandleAsync(
                new UpdateTaskPlan(
                    task.Id,
                    command.NextAction is { } nextAction ? nextAction.Value : task.NextAction,
                    command.Estimate is { } estimate ? estimate.Value : task.Estimate),
                cancellationToken);
        }

        if (command.Schedule is { } schedule)
        {
            await reschedule.HandleAsync(
                new RescheduleOccurrence(occurrenceId, schedule.Value.Date, schedule.Value.Time),
                cancellationToken);
        }

        // Os avisos antes do prazo: o estágio inicial do prazo novo é calculado
        // sobre os avisos que valem para a tarefa.
        if (command.DeadlineAlerts is { } alerts)
        {
            await deadlineAlerts.HandleAsync(new SetTaskDeadlineAlerts(task.Id, alerts.Value), cancellationToken);
        }

        if (command.Deadline is { } deadline)
        {
            if (deadline.Value is { } chosen)
            {
                await setDeadline.HandleAsync(new SetDeadline(occurrenceId, chosen.Date, chosen.Time), cancellationToken);
            }
            else if (task.Occurrences.Single().Deadline is not null)
            {
                await clearDeadline.HandleAsync(new ClearDeadline(occurrenceId), cancellationToken);
            }
        }

        if (command.Reminder is { } policy)
        {
            await reminder.HandleAsync(new SetTaskReminder(task.Id, policy), cancellationToken);
        }

        if (command.TagIds is { } tagIds)
        {
            await tags.HandleAsync(new SetTaskTags(task.Id, tagIds), cancellationToken);
        }
    }
}

/// <summary>
/// Criar uma tarefa já com etiquetas, prazo e plano (ADR-059): o
/// <see cref="CreateTaskHandler"/> da tela e, na mesma transação, o
/// <see cref="EditTaskHandler"/> para o resto.
/// </summary>
public sealed record CreateDetailedTask(CreateTask Task, EditTask? Details = null);

public sealed class CreateDetailedTaskHandler(
    IUnitOfWork unitOfWork,
    CreateTaskHandler create,
    EditTaskHandler edit)
{
    public async Task<CreateTaskResult> HandleAsync(
        CreateDetailedTask command,
        CancellationToken cancellationToken = default)
    {
        CreateTaskResult? created = null;

        await unitOfWork.ExecuteInTransactionAsync(
            async token =>
            {
                created = await create.HandleAsync(command.Task, token);

                if (command.Details is { IsEmpty: false } details)
                {
                    await edit.ApplyAsync(details with { TaskId = created.TaskId }, token);
                }
            },
            cancellationToken);

        return created!;
    }
}
