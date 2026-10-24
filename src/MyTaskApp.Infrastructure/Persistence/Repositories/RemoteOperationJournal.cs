using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Remote;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

/// <summary>
/// O diário do agente no SQLite do desktop (ADR-086). Cada método grava na
/// hora, sem unidade de trabalho: o diário precisa estar no disco antes da
/// rede, e o despachante não roda dentro de um caso de uso. As atualizações
/// mexem só nos próprios campos (<c>ExecuteUpdate</c>), para o estado, o que
/// o servidor sabe e a numeração dos eventos não se sobrescreverem.
/// </summary>
internal sealed class RemoteOperationJournal(MyTaskAppDbContext context, TimeProvider timeProvider) : IRemoteOperationJournal
{
    public async Task<RemoteJournalEntry?> FindAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        await context.RemoteOperationJournal.AsNoTracking()
            .Where(row => row.OperationId == operationId)
            .Select(row => ToEntry(row))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<bool> TryAddAsync(RemoteJournalEntry entry, CancellationToken cancellationToken = default)
    {
        if (await context.RemoteOperationJournal.AnyAsync(row => row.OperationId == entry.OperationId, cancellationToken))
        {
            return false;
        }

        var row = ToRow(entry);
        context.RemoteOperationJournal.Add(row);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException)
        {
            // Outro escopo gravou o mesmo id no meio-tempo: o primeiro vale.
            context.Entry(row).State = EntityState.Detached;
            return false;
        }
    }

    public Task SetStatusAsync(
        Guid operationId,
        RemoteOperationStatus status,
        string? code = null,
        string? summary = null,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow().UtcTicks;

        return context.RemoteOperationJournal
            .Where(row => row.OperationId == operationId)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(row => row.Status, (int)status)
                    .SetProperty(row => row.ResultCode, code)
                    .SetProperty(row => row.ResultSummary, summary)
                    .SetProperty(row => row.UpdatedAt, now),
                cancellationToken);
    }

    public Task MarkReportedAsync(Guid operationId, RemoteOperationStatus status, CancellationToken cancellationToken = default) =>
        context.RemoteOperationJournal
            .Where(row => row.OperationId == operationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ReportedStatus, (int)status), cancellationToken);

    public async Task<RemoteOperationEvent> AppendEventAsync(RemoteOperationEvent draft, CancellationToken cancellationToken = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        // Avança primeiro, lê depois: dentro da transação, o número é só deste evento.
        var advanced = await context.RemoteOperationJournal
            .Where(row => row.OperationId == draft.OperationId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.NextSeq, row => row.NextSeq + 1), cancellationToken);

        if (advanced == 0)
        {
            throw new InvalidOperationException($"A operação {draft.OperationId} não está no diário.");
        }

        var seq = await context.RemoteOperationJournal
            .Where(row => row.OperationId == draft.OperationId)
            .Select(row => row.NextSeq - 1)
            .SingleAsync(cancellationToken);

        var stored = draft with { Seq = seq };
        context.RemoteOperationJournalEvents.Add(new RemoteOperationJournalEventRow
        {
            OperationId = stored.OperationId,
            Seq = seq,
            At = stored.At.UtcTicks,
            Kind = (int)stored.Kind,
            Step = stored.Step,
            Status = (int?)stored.Status,
            Text = stored.Text,
            Data = stored.Data is { } data ? data.GetRawText() : null,
        });
        await context.SaveChangesAsync(cancellationToken);

        if (stored.Kind == RemoteEventKind.StepStarted && stored.Step is { } step)
        {
            await context.RemoteOperationJournal
                .Where(row => row.OperationId == draft.OperationId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Step, step), cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        context.ChangeTracker.Clear();
        return stored;
    }

    public async Task<IReadOnlyList<RemoteOperationEvent>> PendingEventsAsync(Guid operationId, int limit, CancellationToken cancellationToken = default)
    {
        var rows = await context.RemoteOperationJournalEvents.AsNoTracking()
            .Where(row => row.OperationId == operationId)
            .OrderBy(row => row.Seq)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return [.. rows.Select(ToEvent)];
    }

    public async Task AcknowledgeAsync(Guid operationId, long lastContiguousSeq, CancellationToken cancellationToken = default)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        await context.RemoteOperationJournalEvents
            .Where(row => row.OperationId == operationId && row.Seq <= lastContiguousSeq)
            .ExecuteDeleteAsync(cancellationToken);

        // Nunca para trás, e nunca além do último gerado (o "long.MaxValue" de quem desiste de enviar).
        await context.RemoteOperationJournal
            .Where(row => row.OperationId == operationId && row.AckedSeq < lastContiguousSeq)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(
                    row => row.AckedSeq,
                    row => lastContiguousSeq < row.NextSeq - 1 ? lastContiguousSeq : row.NextSeq - 1),
                cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<RemoteJournalEntry>> ListUnfinishedAsync(CancellationToken cancellationToken = default) =>
        await context.RemoteOperationJournal.AsNoTracking()
            .Where(row => !FinalStatuses.Contains(row.Status))
            .OrderBy(row => row.ReceivedAt)
            .Select(row => ToEntry(row))
            .ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<RemoteJournalEntry>> ListUnreportedAsync(CancellationToken cancellationToken = default) =>
        await context.RemoteOperationJournal.AsNoTracking()
            .Where(row => row.ReportedStatus == null || row.ReportedStatus != row.Status || row.AckedSeq < row.NextSeq - 1)
            .OrderBy(row => row.ReceivedAt)
            .Select(row => ToEntry(row))
            .ToListAsync(cancellationToken);

    public Task<int> CountFromControllerSinceAsync(Guid controllerDeviceId, DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        var ticks = since.UtcTicks;
        return context.RemoteOperationJournal
            .CountAsync(row => row.ControllerDeviceId == controllerDeviceId && row.ReceivedAt >= ticks, cancellationToken);
    }

    public Task<int> PruneAsync(DateTimeOffset before, CancellationToken cancellationToken = default)
    {
        var ticks = before.UtcTicks;

        // Os eventos vão junto, pela cascata; as não encerradas e as ainda por contar ficam.
        return context.RemoteOperationJournal
            .Where(row => row.UpdatedAt < ticks && FinalStatuses.Contains(row.Status) && row.ReportedStatus == row.Status)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private static readonly int[] FinalStatuses =
    [
        .. Enum.GetValues<RemoteOperationStatus>().Where(RemoteOperationLifecycle.IsFinal).Select(status => (int)status),
    ];

    private static RemoteJournalEntry ToEntry(RemoteOperationJournalRow row) =>
        new(
            row.OperationId,
            row.ControllerDeviceId,
            row.ControllerNickname,
            row.Kind,
            row.TaskId,
            row.Envelope,
            (RemoteOperationStatus)row.Status,
            (RemoteOperationStatus?)row.ReportedStatus,
            row.ResultCode,
            row.ResultSummary,
            row.Step,
            row.NextSeq,
            row.AckedSeq,
            new DateTimeOffset(row.ReceivedAt, TimeSpan.Zero),
            new DateTimeOffset(row.UpdatedAt, TimeSpan.Zero));

    private static RemoteOperationJournalRow ToRow(RemoteJournalEntry entry) =>
        new()
        {
            OperationId = entry.OperationId,
            ControllerDeviceId = entry.ControllerDeviceId,
            ControllerNickname = entry.ControllerNickname,
            Kind = entry.Kind,
            TaskId = entry.TaskId,
            Envelope = entry.Envelope,
            Status = (int)entry.Status,
            ReportedStatus = (int?)entry.ReportedStatus,
            ResultCode = entry.ResultCode,
            ResultSummary = entry.ResultSummary,
            Step = entry.Step,
            NextSeq = entry.NextSeq,
            AckedSeq = entry.AckedSeq,
            ReceivedAt = entry.ReceivedAt.UtcTicks,
            UpdatedAt = entry.UpdatedAt.UtcTicks,
        };

    private static RemoteOperationEvent ToEvent(RemoteOperationJournalEventRow row)
    {
        JsonElement? data = null;

        if (row.Data is { } json)
        {
            using var document = JsonDocument.Parse(json);
            data = document.RootElement.Clone();
        }

        return new RemoteOperationEvent(
            row.OperationId,
            row.Seq,
            new DateTimeOffset(row.At, TimeSpan.Zero),
            (RemoteEventKind)row.Kind,
            row.Step,
            (RemoteOperationStatus?)row.Status,
            row.Text,
            data);
    }
}

/// <summary>Quais ids do pedido ainda não existem aqui: tarefa, comando, diretório ou etiqueta.</summary>
internal sealed class RemoteEntityProbe(MyTaskAppDbContext context) : IRemoteEntityProbe
{
    public async Task<IReadOnlyList<Guid>> MissingAsync(IReadOnlyCollection<Guid> entityIds, CancellationToken cancellationToken = default)
    {
        var ids = entityIds.ToList();
        var found = new HashSet<Guid>();

        found.UnionWith(await context.Tasks.AsNoTracking().Where(task => ids.Contains(task.Id)).Select(task => task.Id).ToListAsync(cancellationToken));
        found.UnionWith(await context.DevelopmentCommands.AsNoTracking().Where(command => ids.Contains(command.Id)).Select(command => command.Id).ToListAsync(cancellationToken));
        found.UnionWith(await context.TagDirectories.AsNoTracking().Where(directory => ids.Contains(directory.Id)).Select(directory => directory.Id).ToListAsync(cancellationToken));
        found.UnionWith(await context.Tags.AsNoTracking().Where(tag => ids.Contains(tag.Id)).Select(tag => tag.Id).ToListAsync(cancellationToken));

        return [.. ids.Where(id => !found.Contains(id))];
    }
}
