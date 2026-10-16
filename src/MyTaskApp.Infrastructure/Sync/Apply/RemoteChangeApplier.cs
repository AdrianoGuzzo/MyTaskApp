using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Sync.Capture;
using MyTaskApp.Infrastructure.Sync.Mapping;
using MyTaskApp.Infrastructure.Sync.Persistence;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Infrastructure.Sync.Apply;

/// <summary>
/// Aplica uma página do pull no SQLite (ADR-061): uma transação, dados e
/// cursor juntos. Se a página falha, nada entra e o cursor fica — a próxima
/// tentativa recomeça da mesma página.
/// </summary>
/// <remarks>
/// <para>
/// <b>O campo com alteração local pendente fica com o valor local.</b> A
/// operação pendente ainda vai ao servidor, que decide; aplicar o valor de lá
/// por cima apagaria a edição local antes de ela ser julgada.
/// </para>
/// <para>
/// <b>Exclusão vem como tombstone</b> e apaga a linha pela cascata do banco,
/// como a exclusão local. A versão fica no estado, então uma mudança antiga
/// do mesmo id não ressuscita nada.
/// </para>
/// </remarks>
internal sealed class RemoteChangeApplier(
    MyTaskAppDbContext context,
    TimeProvider timeProvider,
    ILogger<RemoteChangeApplier> logger) : IRemoteChangeApplier
{
    public async Task<RemoteApplyResult> ApplyAsync(
        IReadOnlyList<SyncChange> changes,
        string? nextCursor,
        bool isolateFailures,
        CancellationToken cancellationToken = default)
    {
        using var suppression = SyncSuppression.Enter();

        var writer = new SqlRowWriter(context, timeProvider);
        var merger = new SyncLocalMerger(context, writer, timeProvider);
        var page = new PageResult();

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        foreach (var change in Ordered(changes))
        {
            if (!isolateFailures)
            {
                await ApplyOneAsync(change, writer, merger, page, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
                continue;
            }

            var savepoint = $"change_{page.Applied + page.Skipped + page.Failures.Count}";
            await transaction.CreateSavepointAsync(savepoint, cancellationToken);

            try
            {
                await ApplyOneAsync(change, writer, merger, page, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // O savepoint desfaz o banco; o que o EF rastreava sai junto. As
                // mudanças anteriores já estão gravadas na transação.
                await transaction.RollbackToSavepointAsync(savepoint, cancellationToken);
                context.ChangeTracker.Clear();
                await RecordFailureAsync(change, exception, page, cancellationToken);
                await context.SaveChangesAsync(cancellationToken);
            }
        }

        if (nextCursor is not null)
        {
            var state = await context.SyncState.FindAsync([SyncStateRow.SingletonId], cancellationToken);

            if (state is null)
            {
                state = new SyncStateRow();
                context.SyncState.Add(state);
            }

            state.Cursor = nextCursor;
            state.LastPullAt = timeProvider.GetUtcNow();
        }

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        logger.LogInformation(
            "SyncPageApplied {Applied} {Skipped} {Failed}",
            page.Applied,
            page.Skipped,
            page.Failures.Count);

        return new RemoteApplyResult(
            page.Applied,
            page.Skipped,
            [.. page.TaskIds],
            [.. page.RearmTaskIds],
            page.Areas,
            page.Failures,
            page.Moves,
            [.. page.MergedTagIds]);
    }

    /// <summary>Pai antes do filho nas criações; as exclusões por último, filho antes do pai.</summary>
    private static IEnumerable<SyncChange> Ordered(IReadOnlyList<SyncChange> changes) =>
        changes
            .Select((change, index) => (change, index, rank: SyncRegistry.Find(change.EntityType)?.Rank ?? int.MaxValue))
            .OrderBy(item => item.change.Deleted)
            .ThenBy(item => item.change.Deleted ? -item.rank : item.rank)
            .ThenBy(item => item.index)
            .Select(item => item.change);

    private async Task ApplyOneAsync(
        SyncChange change,
        SqlRowWriter writer,
        SyncLocalMerger merger,
        PageResult page,
        CancellationToken cancellationToken)
    {
        // Um tipo que esta versão não conhece: veio de um cliente mais novo. O
        // pull completo depois de atualizar o app traz de novo.
        if (SyncMappers.ForEntityType(change.EntityType) is not { } mapper)
        {
            page.Skipped++;
            return;
        }

        var state = await context.SyncEntityStates.FindAsync([change.EntityType, change.EntityId], cancellationToken);

        if (state is not null && change.Version <= state.ServerVersion)
        {
            page.Skipped++;
            return;
        }

        // Este computador deixou de acompanhar a cópia de lá (ADR-066): nem
        // edição nem exclusão de outro computador entram. A versão segue a do
        // servidor, para que compartilhar de novo faça valer a daqui.
        if (await IsDetachedHereAsync(change, mapper, state, cancellationToken))
        {
            KeepPace(change, state);
            page.Skipped++;
            return;
        }

        if (await WaitingCommandOfAsync(change, mapper, cancellationToken) is { } waitingCommand)
        {
            Wait(change, state, SyncWaitingCodes.WaitingCommand, $"Este botão espera o comando {waitingCommand} entrar neste computador.");
            page.Skipped++;
            return;
        }

        var snapshot = SyncSnapshot.Read(state?.Snapshot);
        var outbox = await context.SyncOutbox
            .Where(row => row.EntityType == change.EntityType && row.EntityId == change.EntityId && row.State == SyncOutboxState.Pending)
            .ToListAsync(cancellationToken);

        if (change.Deleted)
        {
            await ApplyTombstoneAsync(change, mapper, state, snapshot, outbox, writer, page, cancellationToken);
            return;
        }

        var errors = SyncFieldValidator.Validate(mapper.Definition, change.Fields, isCreate: true);

        if (errors.Count > 0)
        {
            throw new RemoteValueException(string.Join("; ", errors.Select(error => $"{error.Field}: {error.Message}")));
        }

        // Excluída aqui e ainda não enviada: a exclusão local vai ao servidor; não volta por baixo.
        if (state is { IsDeleted: true } && outbox.Any(row => row.Kind == SyncOperationKind.Delete))
        {
            page.Skipped++;
            return;
        }

        var pendingFields = outbox
            .Where(row => row.Kind == SyncOperationKind.Upsert)
            .SelectMany(row => SyncSnapshot.Read(row.Fields).Keys)
            .ToHashSet(StringComparer.Ordinal);

        var exists = await writer.ExistsAsync(mapper, change.EntityId, change.Fields, cancellationToken);

        if (await writer.FindCollisionAsync(mapper, change.EntityId, change.Fields, cancellationToken) is { } localId)
        {
            // O apelido é de um comando que este computador não compartilha: ele
            // fica como está, e o que chegou espera alguém renomear um dos dois.
            if (mapper == SyncMappers.Command && await IsLocalOnlyCommandAsync(localId, cancellationToken))
            {
                var alias = SyncValue.ReadString(change.Fields[SyncFields.Command.Alias]);
                Wait(change, state, SyncWaitingCodes.LocalAliasTaken,
                    $"O comando {alias} de outro computador não entrou: {alias} já existe aqui como comando só deste computador. "
                    + "Renomeie um dos dois ou marque \"Compartilhar entre computadores\" no daqui.");
                page.Skipped++;
                return;
            }

            // Etiqueta juntada pelo nome: usada nos dois computadores, então de
            // todos (ADR-062). Quem compartilha é o reconcile, pelo domínio,
            // para a decisão subir como qualquer edição.
            if (await ResolveCollisionAsync(change, mapper, localId, exists, writer, merger, cancellationToken)
                && mapper == SyncMappers.Tag)
            {
                page.MergedTagIds.Add(change.EntityId);
            }

            exists = await writer.ExistsAsync(mapper, change.EntityId, change.Fields, cancellationToken);
        }

        if (!exists)
        {
            await writer.InsertAsync(mapper, change.EntityId, change.Fields, cancellationToken);
        }
        else
        {
            var incoming = change.Fields
                .Where(field => !pendingFields.Contains(field.Key))
                .ToDictionary(field => field.Key, field => field.Value, StringComparer.Ordinal);

            await writer.UpdateAsync(mapper, change.EntityId, incoming, change.Fields, cancellationToken);
        }

        // O último estado conhecido: o do servidor, menos o que a edição local pendente ainda segura.
        var known = new Dictionary<string, JsonElement>(change.Fields, StringComparer.Ordinal);

        foreach (var field in pendingFields.Where(snapshot.ContainsKey))
        {
            known[field] = snapshot[field];
        }

        if (state is null)
        {
            state = new SyncEntityStateRow { EntityType = change.EntityType, EntityId = change.EntityId };
            context.SyncEntityStates.Add(state);
        }

        state.ServerVersion = change.Version;
        state.IsDeleted = false;
        state.Snapshot = SyncSnapshot.Write(known);
        state.CreatedByUserId = change.CreatedByUserId ?? state.CreatedByUserId;
        state.CreatedByDeviceId = change.CreatedByDeviceId ?? state.CreatedByDeviceId;
        state.OriginCreatedAt = change.OriginCreatedAt ?? state.OriginCreatedAt;
        state.UpdatedByDeviceId = change.UpdatedByDeviceId;
        state.UpdatedAt = change.UpdatedAt;
        state.ApplyErrorCode = null;
        state.ApplyError = null;

        Track(change, snapshot, isNew: !exists, page);
        page.Applied++;
    }

    private async Task ApplyTombstoneAsync(
        SyncChange change,
        SyncEntityMapper mapper,
        SyncEntityStateRow? state,
        Dictionary<string, JsonElement> snapshot,
        List<SyncOutboxRow> outbox,
        SqlRowWriter writer,
        PageResult page,
        CancellationToken cancellationToken)
    {
        var keyFields = mapper == SyncMappers.TaskTag && !snapshot.ContainsKey(SyncFields.TaskTag.TaskId)
            ? await writer.FindTaskTagAsync(change.EntityId, cancellationToken)
            : snapshot;

        if (keyFields is not null)
        {
            await writer.DeleteAsync(mapper, change.EntityId, keyFields, cancellationToken);
        }

        // A edição local pendente não tem mais onde entrar: vira conflito, e não some calada.
        var now = timeProvider.GetUtcNow();

        foreach (var row in outbox.Where(row => row.Kind == SyncOperationKind.Upsert))
        {
            foreach (var (field, value) in SyncSnapshot.Read(row.Fields))
            {
                context.SyncConflicts.Add(new SyncConflictRow
                {
                    Id = Guid.CreateVersion7(now),
                    EntityType = change.EntityType,
                    EntityId = change.EntityId,
                    Field = field,
                    LocalValue = value.GetRawText(),
                    ServerValue = null,
                    ServerVersion = change.Version,
                    Reason = SyncConflictReasons.EntityDeleted,
                    EntityLabel = SyncLocalMerger.LabelOf(snapshot),
                    DetectedAt = now,
                });
            }
        }

        context.SyncOutbox.RemoveRange(outbox);

        if (state is null)
        {
            state = new SyncEntityStateRow { EntityType = change.EntityType, EntityId = change.EntityId };
            context.SyncEntityStates.Add(state);
        }

        state.ServerVersion = change.Version;
        state.IsDeleted = true;
        state.Snapshot = null;
        state.UpdatedByDeviceId = change.UpdatedByDeviceId;
        state.UpdatedAt = change.UpdatedAt;

        // O que esperava e foi excluído lá não espera mais nada.
        state.ApplyErrorCode = null;
        state.ApplyError = null;

        page.Areas |= AreaOf(change.EntityType);
        page.Applied++;
    }

    /// <summary>
    /// Outra linha local tem a mesma chave única. Se ela ainda não é conhecida
    /// do servidor (a primeira conexão), as duas viram uma; se é, ela sai do
    /// caminho e a operação pendente dela recebe a resposta do servidor.
    /// </summary>
    /// <returns>As duas viraram uma.</returns>
    private async Task<bool> ResolveCollisionAsync(
        SyncChange change,
        SyncEntityMapper mapper,
        Guid localId,
        bool remoteExists,
        SqlRowWriter writer,
        SyncLocalMerger merger,
        CancellationToken cancellationToken)
    {
        var localState = await context.SyncEntityStates.FindAsync([change.EntityType, localId], cancellationToken);

        await writer.RenameAsideAsync(mapper, localId, cancellationToken);

        if (localState is not null || mapper == SyncMappers.DirectoryCommand)
        {
            return false;
        }

        if (!remoteExists)
        {
            await writer.InsertAsync(mapper, change.EntityId, change.Fields, cancellationToken);
        }

        await merger.MergeAsync(mapper, localId, change.EntityId, change.Fields, cancellationToken);
        return true;
    }

    /// <summary>
    /// O comando que este computador desligou, ou um botão dele (ADR-066). O
    /// botão é achado pelo comando do que chegou, ou do último estado
    /// conhecido, numa exclusão.
    /// </summary>
    private async Task<bool> IsDetachedHereAsync(
        SyncChange change,
        SyncEntityMapper mapper,
        SyncEntityStateRow? state,
        CancellationToken cancellationToken)
    {
        if (state is { IsDetached: true })
        {
            return true;
        }

        if (mapper != SyncMappers.DirectoryCommand || CommandIdOf(change, state) is not { } commandId)
        {
            return false;
        }

        var command = await context.SyncEntityStates.FindAsync([SyncEntityTypes.Command, commandId], cancellationToken);

        return command is { IsDetached: true };
    }

    /// <summary>
    /// O apelido do comando que o botão que chegou espera: o comando ficou de
    /// fora (<see cref="SyncWaitingCodes"/>) e a linha ainda não existe aqui.
    /// </summary>
    private async Task<string?> WaitingCommandOfAsync(SyncChange change, SyncEntityMapper mapper, CancellationToken cancellationToken)
    {
        if (change.Deleted || mapper != SyncMappers.DirectoryCommand || CommandIdOf(change, null) is not { } commandId)
        {
            return null;
        }

        var command = await context.SyncEntityStates.FindAsync([SyncEntityTypes.Command, commandId], cancellationToken);

        if (command is not { ApplyErrorCode: { } code } || !SyncWaitingCodes.IsWaiting(code)
            || await context.DevelopmentCommands.AnyAsync(row => row.Id == commandId, cancellationToken))
        {
            return null;
        }

        return SyncSnapshot.Read(command.Snapshot).TryGetValue(SyncFields.Command.Alias, out var alias)
            ? SyncValue.ReadString(alias)
            : "de outro computador";
    }

    private static Guid? CommandIdOf(SyncChange change, SyncEntityStateRow? state)
    {
        var fields = change.Deleted ? SyncSnapshot.Read(state?.Snapshot) : change.Fields;

        return fields.TryGetValue(SyncFields.DirectoryCommand.CommandId, out var value) ? SyncValue.ReadGuid(value) : null;
    }

    private Task<bool> IsLocalOnlyCommandAsync(Guid commandId, CancellationToken cancellationToken) =>
        context.DevelopmentCommands.AnyAsync(
            command => command.Id == commandId && command.TagDirectoryId == null && !command.IsShared,
            cancellationToken);

    /// <summary>
    /// Desligado: a mudança não entra, mas a versão anda. A revisão que estava
    /// em dia continua em dia — o texto daqui não mudou —, e a pendente
    /// continua pendente.
    /// </summary>
    private void KeepPace(SyncChange change, SyncEntityStateRow? state)
    {
        if (state is null)
        {
            // O botão criado lá para um comando que daqui não acompanha.
            context.SyncEntityStates.Add(new SyncEntityStateRow
            {
                EntityType = change.EntityType,
                EntityId = change.EntityId,
                ServerVersion = change.Version,
                IsDeleted = change.Deleted,
                IsDetached = true,
            });

            return;
        }

        if (state.ReviewedVersion >= state.ServerVersion)
        {
            state.ReviewedVersion = change.Version;
        }

        state.ServerVersion = change.Version;
    }

    /// <summary>
    /// A mudança fica de fora, visível, sem andar a versão: buscada de novo,
    /// ela é aplicada inteira (ver <see cref="RetryWaitingReceivedChangesHandler"/>).
    /// </summary>
    private void Wait(SyncChange change, SyncEntityStateRow? state, string code, string message)
    {
        if (state is null)
        {
            state = new SyncEntityStateRow { EntityType = change.EntityType, EntityId = change.EntityId };
            context.SyncEntityStates.Add(state);
        }

        // O retrato do que chegou dá o apelido aos botões que esperam por ele.
        if (state.ServerVersion == 0)
        {
            state.Snapshot = SyncSnapshot.Write(change.Fields);
        }

        state.ApplyErrorCode = code;
        state.ApplyError = message.Length > 2000 ? message[..2000] : message;

        logger.LogInformation("SyncChangeWaiting {EntityType} {EntityId} {Code}", change.EntityType, change.EntityId, code);
    }

    private static void Track(SyncChange change, Dictionary<string, JsonElement> before, bool isNew, PageResult page)
    {
        page.Areas |= AreaOf(change.EntityType);

        var taskId = change.EntityType switch
        {
            SyncEntityTypes.Task => change.EntityId,
            SyncEntityTypes.Occurrence when change.Fields.TryGetValue(SyncFields.Occurrence.TaskId, out var value) => SyncValue.ReadGuid(value),
            _ => null,
        };

        if (taskId is not { } id)
        {
            return;
        }

        page.TaskIds.Add(id);

        // Mudar de lugar também rearma (ADR-062): a tarefa que passa a aparecer
        // aqui não pode tocar um lembrete velho de uma vez.
        var moved = change.EntityType == SyncEntityTypes.Task && Changed(SyncFields.Task.Placement);

        var changedPlan = change.EntityType == SyncEntityTypes.Task
            ? Changed(SyncFields.Task.Reminder) || moved
            : Changed(SyncFields.Occurrence.Schedule) || Changed(SyncFields.Occurrence.State);

        if (!isNew && changedPlan)
        {
            page.RearmTaskIds.Add(id);
        }

        if (moved)
        {
            page.Moves.Add(new PlacementMove(
                id,
                isNew ? null : PlacementOf(before),
                PlacementOf(change.Fields),
                change.UpdatedByDeviceId,
                isNew));
        }

        bool Changed(string field) =>
            change.Fields.TryGetValue(field, out var now)
            && (!before.TryGetValue(field, out var then) || !SyncValue.AreEqual(now, then));
    }

    private static DevicePlacement? PlacementOf(IReadOnlyDictionary<string, JsonElement> fields)
    {
        if (!fields.TryGetValue(SyncFields.Task.Placement, out var value) || SyncValue.IsNull(value))
        {
            return null;
        }

        var kind = SyncValue.ReadInteger(SyncValue.Member(value, SyncFields.Placement.Kind));
        var device = SyncValue.ReadGuid(SyncValue.Member(value, SyncFields.Placement.DeviceId));

        return DevicePlacement.From(kind is { } number ? (PlacementKind)number : null, device);
    }

    private static DataArea AreaOf(string type) => type switch
    {
        SyncEntityTypes.Task or SyncEntityTypes.Occurrence or SyncEntityTypes.TaskTag or SyncEntityTypes.Development => DataArea.Tasks,
        SyncEntityTypes.Tag or SyncEntityTypes.TagDirectory or SyncEntityTypes.Command or SyncEntityTypes.DirectoryCommand => DataArea.Tags,
        SyncEntityTypes.TimeEntry => DataArea.Time,
        SyncEntityTypes.StickyNote => DataArea.StickyNotes,
        _ => DataArea.None,
    };

    private async Task RecordFailureAsync(SyncChange change, Exception exception, PageResult page, CancellationToken cancellationToken)
    {
        var code = exception is RemoteValueException ? SyncErrorCodes.ValidationFailed : "apply_failed";
        var message = exception is RemoteValueException ? exception.Message : "Não foi possível gravar esta mudança neste computador.";

        logger.LogWarning(exception, "SyncChangeApplyFailed {EntityType} {EntityId}", change.EntityType, change.EntityId);

        var state = await context.SyncEntityStates.FindAsync([change.EntityType, change.EntityId], cancellationToken);

        if (state is null)
        {
            state = new SyncEntityStateRow { EntityType = change.EntityType, EntityId = change.EntityId };
            context.SyncEntityStates.Add(state);
        }

        state.ApplyErrorCode = code;
        state.ApplyError = message.Length > 2000 ? message[..2000] : message;
        page.Failures.Add(new RemoteApplyFailure(change.EntityType, change.EntityId, code, message));
    }

    private sealed class PageResult
    {
        public int Applied { get; set; }

        public int Skipped { get; set; }

        public HashSet<Guid> TaskIds { get; } = [];

        public HashSet<Guid> RearmTaskIds { get; } = [];

        public List<PlacementMove> Moves { get; } = [];

        public HashSet<Guid> MergedTagIds { get; } = [];

        public DataArea Areas { get; set; }

        public List<RemoteApplyFailure> Failures { get; } = [];
    }
}
