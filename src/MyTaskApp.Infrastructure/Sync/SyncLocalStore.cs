using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Sync.Apply;
using MyTaskApp.Infrastructure.Sync.Capture;
using MyTaskApp.Infrastructure.Sync.Mapping;
using MyTaskApp.Infrastructure.Sync.Persistence;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Infrastructure.Sync;

/// <summary>
/// O estado local da sincronização no SQLite (ADR-061): a ligação com a
/// conta, o outbox e o que fazer com cada resposta do servidor.
/// </summary>
internal sealed class SyncLocalStore(
    MyTaskAppDbContext context,
    SyncCaptureSwitch captureSwitch,
    IDeviceIdentity device,
    ISyncTrigger trigger,
    TimeProvider timeProvider,
    DevicePlacementState placement,
    SyncDeviceDirectory directory,
    ILogger<SyncReconciler> reconcilerLogger,
    ILogger<SyncLocalStore> logger) : ISyncLocalStore
{
    public async Task<SyncLocalState> ReadAsync(CancellationToken cancellationToken)
    {
        var row = await context.SyncState.AsNoTracking().SingleOrDefaultAsync(cancellationToken);

        // A captura segue a ligação: quem nunca conectou não grava nada.
        captureSwitch.Set(row is not null && row.Status != SyncLinkStatus.Unlinked);
        SyncPlacementHydration.Apply(row, placement, directory);

        if (row is null)
        {
            return SyncLocalState.Unlinked;
        }

        return new SyncLocalState(
            (SyncLinkState)row.Status,
            row.IsEnabled,
            row.UserId,
            row.UserDisplayName,
            row.UserEmail,
            row.WorkspaceId,
            row.LinkedDeviceId,
            row.Cursor,
            row.ClientSchemaVersion,
            row.LastSuccessAt,
            row.LastPushAt,
            row.LastPullAt,
            row.LastErrorCode,
            row.LastError,
            row.LastErrorAt,
            await context.SyncEntityStates.AnyAsync(cancellationToken));
    }

    public async Task LinkAsync(SyncAccountLink link, CancellationToken cancellationToken)
    {
        // Tudo ou nada: esquecer o que era da conta anterior e gravar a ligação
        // nova. Uma queda entre os dois apagaria o outbox com a ligação antiga
        // de pé, e a volta à mesma conta nem refaria a varredura.
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var row = await RowAsync(cancellationToken);
        var otherAccount = row.UserId is { } previousUser && (previousUser != link.UserId || row.WorkspaceId != link.WorkspaceId);

        if (otherAccount)
        {
            // Outra conta: o que a sincronização sabia é da conta anterior. Os dados locais ficam e sobem como novos.
            await context.SyncOutbox.ExecuteDeleteAsync(cancellationToken);
            await context.SyncEntityStates.ExecuteDeleteAsync(cancellationToken);
            await context.SyncConflicts.ExecuteDeleteAsync(cancellationToken);
            row.Cursor = string.Empty;
            logger.LogInformation("SyncLinkedToAnotherAccount");
        }

        // Desconectado, nada foi capturado: a volta refaz a varredura e o pull completo.
        if (otherAccount || row.Status == SyncLinkStatus.Unlinked)
        {
            row.ClientSchemaVersion = 0;
        }

        // Revogado e reconectado, ou com o device.json perdido, este computador
        // volta com outro id: o que estava no antigo continua sendo daqui (ADR-062).
        if (row.LinkedDeviceId is { } previousDevice && previousDevice != link.DeviceId)
        {
            var former = SyncPlacementHydration.ReadIds(row.FormerDeviceIds).ToHashSet();
            former.Add(previousDevice);
            former.Remove(link.DeviceId);
            row.FormerDeviceIds = SyncPlacementHydration.WriteIds(former);
        }

        row.ServerBaseUrl = link.Server.AbsoluteUri;
        row.UserId = link.UserId;
        row.UserDisplayName = link.DisplayName;
        row.UserEmail = link.Email;
        row.WorkspaceId = link.WorkspaceId;
        row.LinkedDeviceId = link.DeviceId;
        row.Status = SyncLinkStatus.Linked;
        row.IsEnabled = true;
        row.LinkedAt = timeProvider.GetUtcNow();
        ClearError(row);

        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        captureSwitch.Set(true);
        SyncPlacementHydration.Apply(row, placement, directory);
    }

    public async Task SaveKnownDevicesAsync(IReadOnlyList<DeviceInfo> devices, CancellationToken cancellationToken)
    {
        var row = await RowAsync(cancellationToken);
        row.KnownDevices = SyncPlacementHydration.WriteDevices(devices);
        await SaveAsync(cancellationToken);
    }

    public async Task UnlinkAsync(CancellationToken cancellationToken)
    {
        var row = await RowAsync(cancellationToken);
        row.Status = SyncLinkStatus.Unlinked;
        row.IsEnabled = false;
        ClearError(row);

        await SaveAsync(cancellationToken);
        captureSwitch.Set(false);
    }

    public async Task SetStateAsync(SyncLinkState state, CancellationToken cancellationToken)
    {
        var row = await RowAsync(cancellationToken);
        row.Status = (SyncLinkStatus)state;
        await SaveAsync(cancellationToken);
        captureSwitch.Set(row.Status != SyncLinkStatus.Unlinked);
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        var row = await RowAsync(cancellationToken);
        row.IsEnabled = enabled;
        await SaveAsync(cancellationToken);
    }

    public async Task MarkSchemaCurrentAsync(int schemaVersion, CancellationToken cancellationToken)
    {
        var row = await RowAsync(cancellationToken);
        row.ClientSchemaVersion = schemaVersion;
        await SaveAsync(cancellationToken);
    }

    public async Task ResetCursorAsync(CancellationToken cancellationToken)
    {
        var row = await RowAsync(cancellationToken);
        row.Cursor = string.Empty;
        await SaveAsync(cancellationToken);
    }

    public async Task RecordSuccessAsync(bool pushed, bool pulled, CancellationToken cancellationToken)
    {
        var row = await RowAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        row.LastSuccessAt = now;
        row.LastPushAt = pushed ? now : row.LastPushAt;
        row.LastPullAt = pulled ? now : row.LastPullAt;
        ClearError(row);
        await SaveAsync(cancellationToken);
    }

    public async Task RecordErrorAsync(string code, string message, CancellationToken cancellationToken)
    {
        var row = await RowAsync(cancellationToken);
        row.LastErrorCode = Clip(code, 100);
        row.LastError = Clip(message, 2000);
        row.LastErrorAt = timeProvider.GetUtcNow();
        await SaveAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SyncOperation>> TakeOperationsAsync(Guid deviceId, int max, CancellationToken cancellationToken)
    {
        // A entidade com uma operação recusada de vez espera: mandar a seguinte por cima da recusa mudaria a ordem.
        var blocked = await context.SyncOutbox
            .Where(row => row.State == SyncOutboxState.Failed)
            .Select(row => row.EntityId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var rows = await context.SyncOutbox
            .Where(row => row.State == SyncOutboxState.Pending && !blocked.Contains(row.EntityId))
            .OrderBy(row => row.Seq)
            .Take(max)
            .ToListAsync(cancellationToken);

        // Marcado antes de sair do computador: daqui em diante a operação não muda mais.
        foreach (var row in rows)
        {
            row.Attempted = true;
            row.Attempts++;
        }

        await SaveAsync(cancellationToken);

        return
        [
            .. rows.Select(row => new SyncOperation(
                row.OpId,
                deviceId,
                row.EntityType,
                row.EntityId,
                row.Kind,
                row.BaseVersion,
                SyncSnapshot.Read(row.Fields),
                row.OriginCreatedAt)),
        ];
    }

    public async Task<PushOutcome> ApplyPushResultsAsync(IReadOnlyList<OperationResult> results, CancellationToken cancellationToken)
    {
        using var suppression = SyncSuppression.Enter();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var writer = new SqlRowWriter(context, timeProvider);
        var outcome = new Outcome();

        foreach (var result in results)
        {
            if (await context.SyncOutbox.SingleOrDefaultAsync(row => row.OpId == result.OpId, cancellationToken) is not { } operation
                || SyncMappers.ForEntityType(operation.EntityType) is not { } mapper)
            {
                continue;
            }

            var state = await context.SyncEntityStates.FindAsync([operation.EntityType, operation.EntityId], cancellationToken);

            if (result.Error is { } error)
            {
                await HandleErrorAsync(operation, mapper, state, result, error, writer, outcome, cancellationToken);
                continue;
            }

            if (state is not null && result.PreviousVersion == state.ServerVersion)
            {
                state.ServerVersion = result.Version;
            }

            await HandleRejectedFieldsAsync(operation, mapper, state, result.RejectedFields, writer, outcome, cancellationToken);

            context.SyncOutbox.Remove(operation);
            outcome.Done++;
        }

        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return new PushOutcome(outcome.Done, outcome.Conflicts, outcome.Failed, outcome.Waiting, [.. outcome.TaskIds], outcome.Areas);
    }

    /// <summary>
    /// O campo que ficou com o valor do servidor: o valor vale aqui também
    /// (salvo se houver outra edição local dele na fila), e o valor perdido
    /// vira conflito visível — menos quando é carimbo, resolvido em silêncio.
    /// </summary>
    private async Task HandleRejectedFieldsAsync(
        SyncOutboxRow operation,
        SyncEntityMapper mapper,
        SyncEntityStateRow? state,
        IReadOnlyList<RejectedField> rejected,
        SqlRowWriter writer,
        Outcome outcome,
        CancellationToken cancellationToken)
    {
        if (rejected.Count == 0)
        {
            return;
        }

        var sent = SyncSnapshot.Read(operation.Fields);
        var snapshot = SyncSnapshot.Read(state?.Snapshot);
        var later = await context.SyncOutbox
            .Where(row => row.EntityType == operation.EntityType && row.EntityId == operation.EntityId
                && row.State == SyncOutboxState.Pending && row.Seq > operation.Seq)
            .Select(row => row.Fields)
            .ToListAsync(cancellationToken);
        var stillEditing = later.SelectMany(fields => SyncSnapshot.Read(fields).Keys).ToHashSet(StringComparer.Ordinal);

        var serverValues = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var field in rejected)
        {
            if (!stillEditing.Contains(field.Field))
            {
                serverValues[field.Field] = field.ServerValue;
                snapshot[field.Field] = field.ServerValue;
            }

            if (field.Reason != SyncErrorCodes.Superseded)
            {
                AddConflict(operation, field.Field, sent.GetValueOrDefault(field.Field, SyncValue.Null), field.ServerValue, state?.ServerVersion ?? 0, field.Reason, snapshot);
                outcome.Conflicts++;
            }
        }

        if (serverValues.Count > 0 && state is { IsDeleted: false })
        {
            await writer.UpdateAsync(mapper, operation.EntityId, serverValues, snapshot, cancellationToken);
            state.Snapshot = SyncSnapshot.Write(snapshot);
            outcome.Track(operation.EntityType, operation.EntityId, snapshot);
        }
    }

    private async Task HandleErrorAsync(
        SyncOutboxRow operation,
        SyncEntityMapper mapper,
        SyncEntityStateRow? state,
        OperationResult result,
        SyncError error,
        SqlRowWriter writer,
        Outcome outcome,
        CancellationToken cancellationToken)
    {
        switch (error.Code)
        {
            case SyncErrorCodes.ParentMissing:
                operation.LastErrorCode = error.Code;
                operation.LastError = Clip(error.Message, 2000);
                outcome.Waiting++;
                return;

            case SyncErrorCodes.EntityDeleted or SyncErrorCodes.ParentDeleted:
                await DeleteLocallyAsync(operation, mapper, state, error.Code, writer, outcome, cancellationToken);
                outcome.Done++;
                return;

            // A outra precisa existir aqui, e não só ter estado: o comando que
            // espera um apelido livre (ADR-065) tem estado e nenhuma linha.
            case SyncErrorCodes.UniqueViolation
                when result.RejectedFields.FirstOrDefault()?.ConflictingEntityId is { } other
                && await context.SyncEntityStates.AnyAsync(candidate => candidate.EntityType == operation.EntityType && candidate.EntityId == other && !candidate.IsDeleted, cancellationToken)
                && await writer.ExistsAsync(mapper, other, new Dictionary<string, JsonElement>(), cancellationToken):
                await MergeIntoAsync(operation, mapper, other, writer, cancellationToken);
                outcome.Done++;
                outcome.Areas |= DataArea.Tags | DataArea.Tasks;
                return;

            case SyncErrorCodes.UniqueViolation:
                // A outra ainda não chegou aqui: o próximo pull a traz, e o reenvio funde.
                operation.LastErrorCode = error.Code;
                operation.LastError = Clip(error.Message, 2000);
                outcome.Waiting++;
                return;

            default:
                operation.State = SyncOutboxState.Failed;
                operation.LastErrorCode = error.Code;
                operation.LastError = Clip(error.Message, 2000);
                outcome.Failed++;
                logger.LogWarning("SyncOperationFailed {EntityType} {Code}", operation.EntityType, error.Code);
                return;
        }
    }

    /// <summary>
    /// Excluída em outro dispositivo (ou o pai foi): a linha some daqui também,
    /// e a edição local que não tem mais onde entrar fica registrada.
    /// </summary>
    private async Task DeleteLocallyAsync(
        SyncOutboxRow operation,
        SyncEntityMapper mapper,
        SyncEntityStateRow? state,
        string reason,
        SqlRowWriter writer,
        Outcome outcome,
        CancellationToken cancellationToken)
    {
        var snapshot = SyncSnapshot.Read(state?.Snapshot);

        foreach (var (field, value) in SyncSnapshot.Read(operation.Fields))
        {
            AddConflict(operation, field, value, null, state?.ServerVersion ?? 0, reason, snapshot);
            outcome.Conflicts++;
        }

        var keyFields = mapper == SyncMappers.TaskTag && !snapshot.ContainsKey(SyncFields.TaskTag.TaskId)
            ? await writer.FindTaskTagAsync(operation.EntityId, cancellationToken)
            : snapshot;

        if (keyFields is not null)
        {
            await writer.DeleteAsync(mapper, operation.EntityId, keyFields, cancellationToken);
        }

        await context.SyncOutbox
            .Where(row => row.EntityType == operation.EntityType && row.EntityId == operation.EntityId && row.Seq != operation.Seq)
            .ExecuteDeleteAsync(cancellationToken);

        context.SyncOutbox.Remove(operation);

        if (state is not null)
        {
            state.IsDeleted = true;
            state.Snapshot = null;
        }

        outcome.Areas |= AreaOf(operation.EntityType);
    }

    /// <summary>
    /// A criação colidiu com uma que o servidor já tinha (a mesma etiqueta
    /// criada nos dois computadores): as duas viram a do servidor. Os filhos
    /// passam a apontar para ela e são capturados de novo pela varredura.
    /// </summary>
    private async Task MergeIntoAsync(
        SyncOutboxRow operation,
        SyncEntityMapper mapper,
        Guid remoteId,
        SqlRowWriter writer,
        CancellationToken cancellationToken)
    {
        // O que os resultados anteriores do lote mudaram vai para a transação antes de o rastreamento ser limpo.
        await SaveAsync(cancellationToken);

        var children = new List<(string Type, Guid Id)>();

        foreach (var child in SyncMappers.All)
        {
            foreach (var reference in child.Definition.References.Where(reference => reference.TargetType == mapper.Definition.Type))
            {
                children.AddRange((await writer.FindReferencingAsync(child, reference.Field, operation.EntityId, cancellationToken))
                    .Select(id => (child.Definition.Type, id)));
            }
        }

        var remote = await context.SyncEntityStates.FindAsync([operation.EntityType, remoteId], cancellationToken);
        await new SyncLocalMerger(context, writer, timeProvider)
            .MergeAsync(mapper, operation.EntityId, remoteId, SyncSnapshot.Read(remote?.Snapshot), cancellationToken);

        // Os filhos nunca chegaram ao servidor (o pai dele não existia lá): esquecidos e capturados de novo, já com o pai certo.
        var childIds = children.Select(child => child.Id).ToList();
        await context.SyncOutbox.Where(row => row.EntityId == operation.EntityId || childIds.Contains(row.EntityId)).ExecuteDeleteAsync(cancellationToken);
        await context.SyncEntityStates.Where(row => row.EntityId == operation.EntityId || childIds.Contains(row.EntityId)).ExecuteDeleteAsync(cancellationToken);
        context.ChangeTracker.Clear();

        // O mesmo contexto do push, por isso não vem do contêiner; o log é o de sempre do reconciliador.
        await new SyncReconciler(context, device, timeProvider, trigger, reconcilerLogger)
            .ScanAsync(cancellationToken);

        logger.LogInformation("SyncMergedOnPush {EntityType}", operation.EntityType);
    }

    public async Task<SyncCounts> CountAsync(Guid deviceId, CancellationToken cancellationToken) =>
        new(
            await context.SyncOutbox.CountAsync(row => row.State == SyncOutboxState.Pending, cancellationToken),
            await context.SyncOutbox.CountAsync(row => row.State == SyncOutboxState.Failed, cancellationToken),
            await context.SyncConflicts.CountAsync(row => row.ResolvedAt == null, cancellationToken),
            // O botão que espera o comando (ADR-065) já conta no aviso do comando.
            await context.SyncEntityStates.CountAsync(
                row => row.ApplyErrorCode != null && row.ApplyErrorCode != SyncWaitingCodes.WaitingCommand,
                cancellationToken),
            (await ListCommandReviewsAsync(deviceId, cancellationToken)).Count);

    public async Task<IReadOnlyList<SyncConflictView>> ListConflictsAsync(CancellationToken cancellationToken) =>
        [
            .. (await context.SyncConflicts.AsNoTracking().Where(row => row.ResolvedAt == null).ToListAsync(cancellationToken))
                .OrderByDescending(row => row.DetectedAt)
                .Select(row => new SyncConflictView(row.Id, row.EntityType, row.EntityId, row.EntityLabel, row.Field, row.LocalValue, row.ServerValue, row.Reason, row.DetectedAt)),
        ];

    public async Task ReapplyConflictAsync(Guid conflictId, CancellationToken cancellationToken)
    {
        using var suppression = SyncSuppression.Enter();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var conflict = await OpenConflictAsync(conflictId, cancellationToken);
        var state = await context.SyncEntityStates.FindAsync([conflict.EntityType, conflict.EntityId], cancellationToken);

        if (state is not { IsDeleted: false } || SyncMappers.ForEntityType(conflict.EntityType) is not { } mapper || conflict.LocalValue is null)
        {
            throw new DomainException("Isto foi excluído em outro dispositivo: não há onde reaplicar o seu valor.");
        }

        var local = SyncValue.Parse(conflict.LocalValue);
        var snapshot = SyncSnapshot.Read(state.Snapshot);
        snapshot[conflict.Field] = local;

        await new SqlRowWriter(context, timeProvider).UpdateAsync(
            mapper,
            conflict.EntityId,
            new Dictionary<string, JsonElement> { [conflict.Field] = local },
            snapshot,
            cancellationToken);

        state.Snapshot = SyncSnapshot.Write(snapshot);
        state.UpdatedByDeviceId = device.DeviceId;
        state.UpdatedAt = timeProvider.GetUtcNow();

        // Uma alteração nova, sobre a versão atual: o servidor aceita, porque agora este dispositivo conhece a última.
        context.SyncOutbox.Add(new SyncOutboxRow
        {
            OpId = Guid.CreateVersion7(),
            EntityType = conflict.EntityType,
            EntityId = conflict.EntityId,
            Kind = SyncOperationKind.Upsert,
            BaseVersion = state.ServerVersion,
            Fields = SyncSnapshot.Write(new Dictionary<string, JsonElement> { [conflict.Field] = local }),
            OriginCreatedAt = state.OriginCreatedAt,
            CreatedAt = timeProvider.GetUtcNow(),
            State = SyncOutboxState.Pending,
        });

        conflict.ResolvedAt = timeProvider.GetUtcNow();
        conflict.Resolution = "reapplied";

        await SaveAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task DismissConflictAsync(Guid conflictId, CancellationToken cancellationToken)
    {
        var conflict = await OpenConflictAsync(conflictId, cancellationToken);
        conflict.ResolvedAt = timeProvider.GetUtcNow();
        conflict.Resolution = "dismissed";
        await SaveAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<SyncFailedOperationView>> ListFailedAsync(CancellationToken cancellationToken)
    {
        var failed = await context.SyncOutbox.AsNoTracking()
            .Where(row => row.State == SyncOutboxState.Failed)
            .OrderBy(row => row.Seq)
            .ToListAsync(cancellationToken);

        var ids = failed.Select(row => row.EntityId).ToList();
        var states = await context.SyncEntityStates.AsNoTracking().Where(state => ids.Contains(state.EntityId)).ToListAsync(cancellationToken);

        return
        [
            .. failed.Select(row => new SyncFailedOperationView(
                row.OpId,
                row.EntityType,
                row.EntityId,
                SyncLocalMerger.LabelOf(SyncSnapshot.Read(states.FirstOrDefault(state => state.EntityType == row.EntityType && state.EntityId == row.EntityId)?.Snapshot ?? row.Fields)),
                row.LastErrorCode,
                row.LastError,
                row.CreatedAt)),
        ];
    }

    public async Task RetryFailedAsync(Guid opId, CancellationToken cancellationToken)
    {
        var row = await context.SyncOutbox.SingleOrDefaultAsync(candidate => candidate.OpId == opId && candidate.State == SyncOutboxState.Failed, cancellationToken)
            ?? throw new DomainException("Esta falha já foi resolvida.");

        // Outro id: com o mesmo, o servidor devolveria a recusa guardada sem olhar de novo.
        row.OpId = Guid.CreateVersion7();
        row.State = SyncOutboxState.Pending;
        row.Attempted = false;
        row.Attempts = 0;
        row.LastErrorCode = null;
        row.LastError = null;

        await SaveAsync(cancellationToken);
    }

    public async Task<EntityRef?> DiscardFailedAsync(Guid opId, CancellationToken cancellationToken)
    {
        if (await context.SyncOutbox.SingleOrDefaultAsync(candidate => candidate.OpId == opId, cancellationToken) is not { } row)
        {
            return null;
        }

        context.SyncOutbox.Remove(row);
        await SaveAsync(cancellationToken);

        return new EntityRef(row.EntityType, row.EntityId);
    }

    public async Task<IReadOnlyList<SyncApplyFailureView>> ListApplyFailuresAsync(CancellationToken cancellationToken) =>
        [
            .. (await context.SyncEntityStates.AsNoTracking().Where(state => state.ApplyErrorCode != null).ToListAsync(cancellationToken))
                .Select(state => new SyncApplyFailureView(state.EntityType, state.EntityId, state.ApplyErrorCode, state.ApplyError)),
        ];

    public async Task<IReadOnlyList<SyncCommandReviewItem>> ListCommandReviewsAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        var states = await context.SyncEntityStates.AsNoTracking()
            .Where(state => CommandReviewRule.Types.Contains(state.EntityType) && !state.IsDeleted
                && state.UpdatedByDeviceId != null && state.UpdatedByDeviceId != deviceId
                && state.ServerVersion > state.ReviewedVersion)
            .ToListAsync(cancellationToken);

        return
        [
            .. states
                .Select(state => (state, text: CommandReviewRule.CommandTextOf(state.EntityType, SyncSnapshot.Read(state.Snapshot))))
                .Where(pair => pair.text is not null)
                .Select(pair => new SyncCommandReviewItem(
                    pair.state.EntityType,
                    pair.state.EntityId,
                    CommandReviewRule.LabelOf(pair.state.EntityType, SyncSnapshot.Read(pair.state.Snapshot)),
                    pair.text!,
                    pair.state.UpdatedByDeviceId,
                    pair.state.ServerVersion)),
        ];
    }

    public async Task MarkCommandReviewedAsync(string entityType, Guid entityId, CancellationToken cancellationToken)
    {
        var state = await context.SyncEntityStates.FindAsync([entityType, entityId], cancellationToken)
            ?? throw new DomainException("Este comando não está mais na lista.");

        state.ReviewedVersion = state.ServerVersion;
        await SaveAsync(cancellationToken);
    }

    public async Task<SyncOrigin?> OriginOfAsync(string entityType, Guid entityId, CancellationToken cancellationToken) =>
        await context.SyncEntityStates.AsNoTracking()
            .Where(state => state.EntityType == entityType && state.EntityId == entityId)
            .Select(state => new SyncOrigin(state.CreatedByDeviceId, state.OriginCreatedAt, state.UpdatedByDeviceId, state.UpdatedAt))
            .SingleOrDefaultAsync(cancellationToken);

    private async Task<SyncStateRow> RowAsync(CancellationToken cancellationToken)
    {
        if (await context.SyncState.FindAsync([SyncStateRow.SingletonId], cancellationToken) is { } row)
        {
            return row;
        }

        row = new SyncStateRow();
        context.SyncState.Add(row);
        return row;
    }

    private async Task<SyncConflictRow> OpenConflictAsync(Guid conflictId, CancellationToken cancellationToken) =>
        await context.SyncConflicts.SingleOrDefaultAsync(row => row.Id == conflictId && row.ResolvedAt == null, cancellationToken)
            ?? throw new DomainException("Este conflito já foi resolvido.");

    private void AddConflict(
        SyncOutboxRow operation,
        string field,
        JsonElement local,
        JsonElement? server,
        long serverVersion,
        string reason,
        IReadOnlyDictionary<string, JsonElement> snapshot) =>
        context.SyncConflicts.Add(new SyncConflictRow
        {
            Id = Guid.CreateVersion7(),
            EntityType = operation.EntityType,
            EntityId = operation.EntityId,
            Field = field,
            LocalValue = local.GetRawText(),
            ServerValue = server?.GetRawText(),
            ServerVersion = serverVersion,
            Reason = reason,
            EntityLabel = SyncLocalMerger.LabelOf(snapshot),
            DetectedAt = timeProvider.GetUtcNow(),
        });

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        using var suppression = SyncSuppression.Enter();
        await context.SaveChangesAsync(cancellationToken);
    }

    private static void ClearError(SyncStateRow row)
    {
        row.LastErrorCode = null;
        row.LastError = null;
        row.LastErrorAt = null;
    }

    private static string Clip(string value, int max) => value.Length > max ? value[..max] : value;

    private static DataArea AreaOf(string type) => type switch
    {
        SyncEntityTypes.Task or SyncEntityTypes.Occurrence or SyncEntityTypes.TaskTag or SyncEntityTypes.Development => DataArea.Tasks,
        SyncEntityTypes.TimeEntry => DataArea.Time,
        SyncEntityTypes.StickyNote => DataArea.StickyNotes,
        _ => DataArea.Tags,
    };

    private sealed class Outcome
    {
        public int Done { get; set; }

        public int Conflicts { get; set; }

        public int Failed { get; set; }

        public int Waiting { get; set; }

        public HashSet<Guid> TaskIds { get; } = [];

        public DataArea Areas { get; set; }

        public void Track(string type, Guid id, IReadOnlyDictionary<string, JsonElement> snapshot)
        {
            Areas |= AreaOf(type);

            if (type == SyncEntityTypes.Task)
            {
                TaskIds.Add(id);
            }
            else if (type == SyncEntityTypes.Occurrence && SyncValue.ReadGuid(snapshot.GetValueOrDefault(SyncFields.Occurrence.TaskId, SyncValue.Null)) is { } taskId)
            {
                TaskIds.Add(taskId);
            }
        }
    }
}

/// <summary>
/// Que entidade sincronizada carrega texto que vira comando — e qual. É a
/// regra única do portão e da lista "Comandos recebidos" (ADR-061).
/// </summary>
internal static class CommandReviewRule
{
    public static readonly string[] Types = [SyncEntityTypes.Command, SyncEntityTypes.DirectoryCommand, SyncEntityTypes.Development];

    /// <summary>O texto a revisar, ou <c>null</c> quando a mudança não leva comando (a ordem de um botão).</summary>
    public static string? CommandTextOf(string type, IReadOnlyDictionary<string, JsonElement> fields) => type switch
    {
        SyncEntityTypes.Command => SyncValue.ReadString(fields.GetValueOrDefault(SyncFields.Command.Text, SyncValue.Null)),
        SyncEntityTypes.DirectoryCommand => Join(
            SyncValue.ReadString(fields.GetValueOrDefault(SyncFields.DirectoryCommand.CommandOverride, SyncValue.Null)),
            SyncValue.ReadString(fields.GetValueOrDefault(SyncFields.DirectoryCommand.WorkingDirectoryOverride, SyncValue.Null)) is { } folder ? $"(na pasta {folder})" : null),
        SyncEntityTypes.Development => fields.GetValueOrDefault(SyncFields.Development.Commands, SyncValue.Null) is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0
            ? string.Join(Environment.NewLine, list.EnumerateArray().Select(item => item.GetString()))
            : null,
        _ => null,
    };

    public static string LabelOf(string type, IReadOnlyDictionary<string, JsonElement> fields) => type switch
    {
        SyncEntityTypes.Command =>
            SyncValue.ReadString(fields.GetValueOrDefault(SyncFields.Command.Name, SyncValue.Null))
            ?? SyncValue.ReadString(fields.GetValueOrDefault(SyncFields.Command.Alias, SyncValue.Null))
            ?? "Comando",
        SyncEntityTypes.DirectoryCommand => "Comando personalizado de um diretório",
        _ => "Comandos pós-worktree de uma tarefa",
    };

    private static string? Join(string? first, string? second) =>
        (first, second) switch
        {
            (null, null) => null,
            ({ } a, null) => a,
            (null, { } b) => b,
            ({ } a, { } b) => $"{a} {b}",
        };
}

/// <summary>O portão de revisão sobre o estado local (ADR-061).</summary>
internal sealed class SyncCommandReviewGate(MyTaskAppDbContext context, IDeviceIdentity device) : ICommandReviewGate
{
    public async Task EnsureReviewedAsync(IReadOnlyCollection<CommandSource> sources, CancellationToken cancellationToken)
    {
        if (sources.Count == 0)
        {
            return;
        }

        var ids = sources.Select(source => source.EntityId).ToList();
        var deviceId = device.DeviceId;

        var states = await context.SyncEntityStates.AsNoTracking()
            .Where(state => ids.Contains(state.EntityId) && !state.IsDeleted
                && state.UpdatedByDeviceId != null && state.UpdatedByDeviceId != deviceId
                && state.ServerVersion > state.ReviewedVersion)
            .ToListAsync(cancellationToken);

        foreach (var state in states.Where(state => sources.Contains(new CommandSource(state.EntityType, state.EntityId))))
        {
            var fields = SyncSnapshot.Read(state.Snapshot);

            if (CommandReviewRule.CommandTextOf(state.EntityType, fields) is not null)
            {
                throw new CommandReviewRequiredException(
                    $"\"{CommandReviewRule.LabelOf(state.EntityType, fields)}\" foi alterado em outro computador e precisa ser revisto antes de rodar aqui. "
                    + "Abra Sincronização… → Comandos recebidos.");
            }
        }
    }
}
