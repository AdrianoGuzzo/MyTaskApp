using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Application.TimeTracking;

/// <summary>
/// "+ Adicionar tempo" (ADR-052): o período em hora de parede, como o usuário o
/// escreveu. A data do fim é separada para um período que atravessa a meia-noite.
/// </summary>
public sealed record AddTimeEntry(
    Guid OccurrenceId,
    DateOnly StartDate,
    TimeOnly StartTime,
    DateOnly EndDate,
    TimeOnly EndTime,
    string? Note);

public sealed class AddTimeEntryHandler(
    ITaskItemRepository tasks,
    ITimeEntryRepository timeEntries,
    IUnitOfWork unitOfWork,
    ITaskAuditLog audit,
    ICurrentUser currentUser,
    IUserClock clock,
    TimeProvider timeProvider,
    ILogger<AddTimeEntryHandler> logger)
{
    public async Task<Guid> HandleAsync(AddTimeEntry command, CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByOccurrenceIdAsync(command.OccurrenceId, cancellationToken);

        task.EnsureTimeCanBeLogged(command.OccurrenceId);

        var now = timeProvider.GetUtcNow();
        var entry = TimeEntry.Manual(
            command.OccurrenceId,
            clock.ToInstant(command.StartDate, command.StartTime),
            clock.ToInstant(command.EndDate, command.EndTime),
            command.Note,
            now);

        var siblings = await timeEntries.ListForOccurrenceAsync(command.OccurrenceId, cancellationToken);

        TimeLog.EnsureNoOverlap(
            entry.StartedAt,
            entry.EndedAt,
            siblings,
            now,
            sibling => TimeEntryText.DatedRange(sibling, clock));

        await timeEntries.AddAsync(entry, cancellationToken);
        await audit.RecordAsync(
            TaskAuditEntry.ByUser(
                task.Id,
                task.Title,
                TaskAuditOperation.TimeEntryAdded,
                now,
                currentUser.Name,
                TimeEntryText.Audit(entry, clock, now)),
            cancellationToken);

        // O período e o seu registro entram juntos, ou não entra nenhum dos dois.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TimeEntryAdded {EntryId} {TaskId} {OccurrenceId}", entry.Id, task.Id, command.OccurrenceId);

        return entry.Id;
    }
}

/// <summary>Corrige um período encerrado; a origem (Timer ou Manual) fica como era.</summary>
public sealed record UpdateTimeEntry(
    Guid EntryId,
    DateOnly StartDate,
    TimeOnly StartTime,
    DateOnly EndDate,
    TimeOnly EndTime,
    string? Note);

public sealed class UpdateTimeEntryHandler(
    ITaskItemRepository tasks,
    ITimeEntryRepository timeEntries,
    IUnitOfWork unitOfWork,
    ITaskAuditLog audit,
    ICurrentUser currentUser,
    IUserClock clock,
    TimeProvider timeProvider,
    ILogger<UpdateTimeEntryHandler> logger)
{
    public async Task HandleAsync(UpdateTimeEntry command, CancellationToken cancellationToken = default)
    {
        var entry = await timeEntries.GetAsync(command.EntryId, cancellationToken);
        var task = await tasks.GetByOccurrenceIdAsync(entry.TaskOccurrenceId, cancellationToken);

        task.EnsureTimeCanBeLogged(entry.TaskOccurrenceId);

        if (entry.IsActive)
        {
            throw new DomainException("Pare o cronômetro antes de editar este período.");
        }

        var now = timeProvider.GetUtcNow();
        var start = clock.ToInstant(command.StartDate, command.StartTime);
        var end = clock.ToInstant(command.EndDate, command.EndTime);

        // Tudo conferido antes de tocar no período: uma recusa não pode deixá-lo
        // meio editado em memória (Edição atômica).
        TimeEntry.EnsurePeriod(start, end, now);

        var siblings = await timeEntries.ListForOccurrenceAsync(entry.TaskOccurrenceId, cancellationToken);

        TimeLog.EnsureNoOverlap(
            start,
            end,
            siblings,
            now,
            sibling => TimeEntryText.DatedRange(sibling, clock),
            ignoring: entry.Id);

        var before = TimeEntryText.Audit(entry, clock, now);

        entry.Change(start, end, command.Note, now);

        await audit.RecordAsync(
            TaskAuditEntry.ByUser(
                task.Id,
                task.Title,
                TaskAuditOperation.TimeEntryChanged,
                now,
                currentUser.Name,
                $"{before} → {TimeEntryText.Audit(entry, clock, now)}"),
            cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TimeEntryChanged {EntryId} {TaskId}", entry.Id, task.Id);
    }
}

/// <summary>Exclui um período. Se for o que está correndo, o cronômetro é descartado.</summary>
public sealed record DeleteTimeEntry(Guid EntryId);

public sealed class DeleteTimeEntryHandler(
    ITaskItemRepository tasks,
    ITimeEntryRepository timeEntries,
    IUnitOfWork unitOfWork,
    ITaskAuditLog audit,
    ICurrentUser currentUser,
    IUserClock clock,
    TimeProvider timeProvider,
    ILogger<DeleteTimeEntryHandler> logger)
{
    public async Task HandleAsync(DeleteTimeEntry command, CancellationToken cancellationToken = default)
    {
        var entry = await timeEntries.GetAsync(command.EntryId, cancellationToken);
        var task = await tasks.GetByOccurrenceIdAsync(entry.TaskOccurrenceId, cancellationToken);

        task.EnsureTimeCanBeLogged(entry.TaskOccurrenceId);

        var now = timeProvider.GetUtcNow();

        timeEntries.Remove(entry);

        await audit.RecordAsync(
            TaskAuditEntry.ByUser(
                task.Id,
                task.Title,
                TaskAuditOperation.TimeEntryDeleted,
                now,
                currentUser.Name,
                TimeEntryText.Audit(entry, clock, now)),
            cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TimeEntryDeleted {EntryId} {TaskId} {WasActive}", entry.Id, task.Id, entry.IsActive);
    }
}

internal static class TimeEntryRepositoryExtensions
{
    /// <summary>Carrega o período, ou falha com mensagem que a tela pode exibir (ADR-008).</summary>
    public static async Task<TimeEntry> GetAsync(
        this ITimeEntryRepository timeEntries,
        Guid entryId,
        CancellationToken cancellationToken) =>
        await timeEntries.FindByIdAsync(entryId, cancellationToken)
        ?? throw new DomainException("Período não encontrado.");
}
