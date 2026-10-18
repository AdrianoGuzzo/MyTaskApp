using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Server.Application;

/// <summary>
/// O push (ADR-061): um lote de operações numa transação, com o workspace
/// travado. A ordem do lote é a ordem de aplicação, e cada mudança recebe a
/// próxima posição do log — densa, sem buraco, porque só um push por vez
/// escreve no workspace.
/// </summary>
/// <remarks>
/// <para>
/// <b>Idempotência.</b> Toda operação processada fica guardada com o
/// resultado. Reenviada depois de um timeout — o servidor gravou, a resposta
/// se perdeu —, ela devolve o mesmo resultado, marcado como duplicado, sem
/// aplicar nada de novo. O mesmo id com outro conteúdo é recusado.
/// </para>
/// <para>
/// <b>O pai que ainda não chegou</b> é a única recusa temporária: a operação
/// não é guardada, e a próxima tentativa decide de novo. A cota cheia também
/// não é guardada — depende do que o workspace ocupa na hora —, mas não volta
/// sozinha: pede que alguém exclua.
/// </para>
/// <para>
/// <b>Só o contrato entra</b> (ADR-072). Campo fora do registro recusa a
/// operação inteira; a entidade mesclada tem teto de bytes, e o workspace,
/// cota de linhas e de bytes. Operação que não cresce passa mesmo acima da
/// cota, para sempre dar para excluir e encolher.
/// </para>
/// <para>
/// <b>Excluído não volta.</b> A exclusão deixa um tombstone; uma alteração
/// atrasada dele é recusada. Só o vínculo puro (tarefa↔etiqueta) revive:
/// marcar de novo a mesma etiqueta é um vínculo novo com o mesmo id.
/// </para>
/// </remarks>
public sealed class PushProcessor(
    ISyncStore store,
    ISyncUnitOfWork unitOfWork,
    IWorkspaceAccess access,
    PushLimits limits,
    TimeProvider timeProvider,
    ILogger<PushProcessor> logger)
{
    public async Task<PushResponse> PushAsync(
        SyncCaller caller,
        Guid workspaceId,
        PushRequest request,
        CancellationToken cancellationToken)
    {
        if (request.SchemaVersion < SyncSchema.MinimumClient)
        {
            throw SyncServerException.UpgradeRequired();
        }

        if (request.DeviceId != caller.DeviceId)
        {
            throw new SyncServerException(SyncErrorCodes.DeviceMismatch, 400, "O dispositivo do pedido não é o do token.");
        }

        if (request.Operations is null)
        {
            throw SyncServerException.BadRequest("O envio veio sem a lista de operações.");
        }

        if (request.Operations.Count > SyncSchema.MaxOperationsPerPush)
        {
            throw SyncServerException.BadRequest($"No máximo {SyncSchema.MaxOperationsPerPush} operações por envio.");
        }

        // O tipo entra na frase da recusa, que é gravada: com NUL, o banco não aceitaria.
        if (request.Operations.Any(operation => operation is null
            || string.IsNullOrEmpty(operation.EntityType)
            || ReceivedText.HasControl(operation.EntityType)
            || operation.Fields is null))
        {
            throw SyncServerException.BadRequest("Operação nula, sem tipo ou sem campos.");
        }

        var role = await access.RoleOfAsync(caller.UserId, workspaceId, cancellationToken) ?? throw SyncServerException.NotFound();

        if (role < WorkspaceRole.Editor)
        {
            throw SyncServerException.Forbidden("Este workspace é só de leitura para você.");
        }

        var results = await unitOfWork.ExecuteAsync(
            async token =>
            {
                var seq = await store.LockWorkspaceAsync(workspaceId, token);
                var batch = new Batch(caller, workspaceId, seq, timeProvider.GetUtcNow(), await store.UsageAsync(workspaceId, token));
                var list = new List<OperationResult>(request.Operations.Count);

                foreach (var operation in request.Operations)
                {
                    list.Add(await ProcessAsync(batch, operation, token));
                }

                await store.SetLastSeqAsync(workspaceId, batch.Seq, token);
                return list;
            },
            cancellationToken);

        logger.LogInformation(
            "SyncPushProcessed {WorkspaceId} {Operations} {Rejected}",
            workspaceId,
            results.Count,
            results.Count(result => result.Outcome == OperationOutcome.Rejected));

        return new PushResponse(results, timeProvider.GetUtcNow());
    }

    private async Task<OperationResult> ProcessAsync(Batch batch, SyncOperation operation, CancellationToken cancellationToken)
    {
        if (operation.DeviceId != batch.Caller.DeviceId)
        {
            return Rejected(operation, 0, SyncErrorCodes.DeviceMismatch, "O dispositivo da operação não é o do token.");
        }

        var hash = Hash(operation);

        if (await store.FindProcessedAsync(batch.WorkspaceId, operation.OpId, cancellationToken) is { } processed)
        {
            return CryptographicOperations.FixedTimeEquals(processed.RequestHash, hash)
                ? processed.Result with { Duplicate = true }
                : Rejected(operation, 0, SyncErrorCodes.IdempotencyKeyReuse, "Este id de operação já foi usado com outro conteúdo.");
        }

        var result = await ExecuteAsync(batch, operation, cancellationToken);

        if (result.Error?.Code is not (SyncErrorCodes.ParentMissing or SyncErrorCodes.QuotaExceeded))
        {
            await store.SaveProcessedAsync(
                batch.WorkspaceId,
                batch.Caller,
                new ProcessedOperation(operation.OpId, hash, result),
                batch.Now,
                cancellationToken);
        }

        return result;
    }

    private async Task<OperationResult> ExecuteAsync(Batch batch, SyncOperation operation, CancellationToken cancellationToken)
    {
        if (SyncRegistry.Find(operation.EntityType) is not { } definition)
        {
            return Rejected(operation, 0, SyncErrorCodes.UnknownEntityType, $"Tipo {operation.EntityType} desconhecido.");
        }

        var current = await store.FindAsync(batch.WorkspaceId, operation.EntityType, operation.EntityId, cancellationToken);

        if (operation.Kind == SyncOperationKind.Delete)
        {
            return await DeleteAsync(batch, definition, current, operation, cancellationToken);
        }

        // Quem já viu a exclusão (a versão base alcança a do tombstone) e manda a
        // entidade inteira está recriando de propósito: o comando que voltou a ser
        // compartilhado depois de excluído em outro computador (ADR-066). Quem não
        // viu continua recebendo "excluído em outro dispositivo".
        var revive = current is { IsDeleted: true }
            && (definition.IsAssociation || operation.BaseVersion >= current.Version);

        if (current is { IsDeleted: true } && !revive)
        {
            return Rejected(operation, current.Version, SyncErrorCodes.EntityDeleted, "Excluído em outro dispositivo.");
        }

        var isCreate = current is null || revive;

        if (SyncFieldValidator.FindUnknown(definition, operation.Fields) is { Count: > 0 } unknown)
        {
            return Rejected(
                operation,
                current?.Version ?? 0,
                SyncErrorCodes.UnknownField,
                $"Campos fora do contrato deste servidor: {string.Join(", ", unknown)}.");
        }

        var errors = SyncFieldValidator.Validate(definition, operation.Fields, isCreate);

        if (errors.Count > 0)
        {
            return Rejected(
                operation,
                current?.Version ?? 0,
                SyncErrorCodes.ValidationFailed,
                string.Join("; ", errors.Select(error => $"{error.Field}: {error.Message}")));
        }

        // O mesmo GUID ou instante escrito de outro jeito é o mesmo valor: a chave de unicidade e a mescla comparam o texto.
        operation = operation with { Fields = SyncFieldValidator.Normalize(definition, operation.Fields) };

        Dictionary<string, JsonElement> applied;
        var rejected = new List<RejectedField>();

        if (isCreate)
        {
            applied = new Dictionary<string, JsonElement>(operation.Fields, StringComparer.Ordinal);
        }
        else
        {
            var merge = FieldMergePolicy.Merge(definition, current!, operation);

            if (merge.Error is { } error)
            {
                return Rejected(operation, current!.Version, error.Code, error.Message);
            }

            applied = new Dictionary<string, JsonElement>(merge.Applied, StringComparer.Ordinal);
            rejected.AddRange(merge.Rejected);
        }

        // O que um servidor de antes do ADR-072 guardou fora do contrato sai na próxima escrita.
        var data = isCreate
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(
                current!.Data.Where(pair => definition.FindField(pair.Key) is not null),
                StringComparer.Ordinal);

        foreach (var (name, value) in applied)
        {
            data[name] = SyncValue.Detach(value);
        }

        // O pai precisa existir neste workspace — o que também impede apontar para dado de outro.
        foreach (var reference in definition.References)
        {
            if ((!isCreate && !applied.ContainsKey(reference.Field))
                || SyncValue.ReadGuid(data.GetValueOrDefault(reference.Field, SyncValue.Null)) is not { } targetId)
            {
                continue;
            }

            var target = await store.FindAsync(batch.WorkspaceId, reference.TargetType, targetId, cancellationToken);

            if (target is null)
            {
                return Rejected(operation, current?.Version ?? 0, SyncErrorCodes.ParentMissing, "O pai ainda não chegou ao servidor.");
            }

            if (target.IsDeleted)
            {
                if (reference.OnDelete != SyncDeleteBehavior.SetNull)
                {
                    return Rejected(operation, current?.Version ?? 0, SyncErrorCodes.ParentDeleted, "O pai foi excluído em outro dispositivo.");
                }

                data[reference.Field] = SyncValue.Null;
                applied[reference.Field] = SyncValue.Null;
                rejected.Add(new RejectedField(reference.Field, SyncErrorCodes.Superseded, SyncValue.Null));
            }
        }

        var uniqueKey = SyncUniqueKey.Build(definition, name => data.GetValueOrDefault(name, SyncValue.Null));

        if (uniqueKey is not null && (isCreate || definition.UniqueKeyFields.Any(applied.ContainsKey))
            && await store.FindByUniqueKeyAsync(batch.WorkspaceId, operation.EntityType, uniqueKey, operation.EntityId, cancellationToken) is { } other)
        {
            if (isCreate)
            {
                return new OperationResult(
                    operation.OpId,
                    OperationOutcome.Rejected,
                    Duplicate: false,
                    current?.Version ?? 0,
                    current?.Version ?? 0,
                    [.. definition.UniqueKeyFields.Select(field => new RejectedField(field, SyncErrorCodes.UniqueViolation, SyncValue.Null, other))],
                    new SyncError(SyncErrorCodes.UniqueViolation, "Já existe outro com o mesmo nome."));
            }

            foreach (var field in definition.UniqueKeyFields.Where(applied.ContainsKey).ToList())
            {
                var serverValue = current!.Data.GetValueOrDefault(field, SyncValue.Null);
                applied.Remove(field);
                data[field] = serverValue;
                rejected.Add(new RejectedField(field, SyncErrorCodes.UniqueViolation, serverValue, other));
            }

            uniqueKey = current!.UniqueKey;
        }

        if (applied.Count == 0)
        {
            var version = current?.Version ?? 0;

            return new OperationResult(
                operation.OpId,
                rejected.Any(FieldMergePolicy.IsConflict) ? OperationOutcome.Rejected : OperationOutcome.Noop,
                Duplicate: false,
                version,
                version,
                rejected);
        }

        var bytes = StoredEntity.SizeOf(data);

        if (limits.MaxEntityBytes > 0 && bytes > limits.MaxEntityBytes)
        {
            return Rejected(
                operation,
                current?.Version ?? 0,
                SyncErrorCodes.EntityTooLarge,
                $"Ficaria com {bytes} bytes; o teto do servidor é {limits.MaxEntityBytes}.");
        }

        var wasLive = current is { IsDeleted: false };

        if (QuotaProblem(batch, becomesLive: !wasLive, bytes - (current?.DataBytes ?? 0)) is { } quota)
        {
            return Rejected(operation, current?.Version ?? 0, SyncErrorCodes.QuotaExceeded, quota);
        }

        var previous = current?.Version ?? 0;
        var entity = current ?? new StoredEntity
        {
            WorkspaceId = batch.WorkspaceId,
            EntityType = operation.EntityType,
            EntityId = operation.EntityId,
        };

        entity.Version = previous + 1;
        entity.LastSeq = ++batch.Seq;
        entity.Data = data;
        entity.UniqueKey = uniqueKey;
        entity.IsDeleted = false;
        entity.DeletedAt = null;

        if (isCreate)
        {
            entity.FieldVersions = data.Keys.ToDictionary(
                name => name,
                _ => new FieldStamp(entity.Version, batch.Caller.DeviceId),
                StringComparer.Ordinal);
            entity.CreatedByUserId = batch.Caller.UserId;
            entity.CreatedByDeviceId = batch.Caller.DeviceId;
            entity.OriginCreatedAt = operation.OriginCreatedAt ?? batch.Now;
        }
        else
        {
            foreach (var name in applied.Keys)
            {
                entity.FieldVersions[name] = new FieldStamp(entity.Version, batch.Caller.DeviceId);
            }
        }

        entity.UpdatedByUserId = batch.Caller.UserId;
        entity.UpdatedByDeviceId = batch.Caller.DeviceId;
        entity.UpdatedAt = batch.Now;

        await SaveAsync(batch, entity, wasLive, bytes, cancellationToken);
        await store.ReplaceReferencesAsync(batch.WorkspaceId, entity.EntityType, entity.EntityId, ReferencesOf(definition, entity), cancellationToken);
        await LogAsync(batch, entity, operation.OpId, isCreate ? "create" : "update", [.. applied.Keys], cancellationToken);

        return new OperationResult(
            operation.OpId,
            rejected.Any(FieldMergePolicy.IsConflict) ? OperationOutcome.PartiallyApplied : OperationOutcome.Applied,
            Duplicate: false,
            entity.Version,
            previous,
            rejected);
    }

    private async Task<OperationResult> DeleteAsync(
        Batch batch,
        SyncEntityDefinition definition,
        StoredEntity? current,
        SyncOperation operation,
        CancellationToken cancellationToken)
    {
        if (current is { IsDeleted: true })
        {
            return new OperationResult(operation.OpId, OperationOutcome.Noop, false, current.Version, current.Version, []);
        }

        // Excluir o que nunca chegou: o tombstone fica, para uma criação atrasada não ressuscitar.
        var entity = current ?? new StoredEntity
        {
            WorkspaceId = batch.WorkspaceId,
            EntityType = operation.EntityType,
            EntityId = operation.EntityId,
            CreatedByUserId = batch.Caller.UserId,
            CreatedByDeviceId = batch.Caller.DeviceId,
            OriginCreatedAt = batch.Now,
        };

        var previous = entity.Version;
        await TombstoneAsync(batch, entity, operation.OpId, "delete", cancellationToken);
        await CascadeAsync(batch, definition.Type, entity.EntityId, [], cancellationToken);

        return new OperationResult(operation.OpId, OperationOutcome.Applied, false, entity.Version, previous, []);
    }

    private async Task TombstoneAsync(Batch batch, StoredEntity entity, Guid? opId, string kind, CancellationToken cancellationToken)
    {
        var wasLive = entity.Version > 0 && !entity.IsDeleted;

        entity.Version++;
        entity.LastSeq = ++batch.Seq;
        entity.IsDeleted = true;
        entity.Data = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        entity.FieldVersions = new Dictionary<string, FieldStamp>(StringComparer.Ordinal);
        entity.UniqueKey = null;
        entity.DeletedAt = batch.Now;
        entity.UpdatedByUserId = batch.Caller.UserId;
        entity.UpdatedByDeviceId = batch.Caller.DeviceId;
        entity.UpdatedAt = batch.Now;

        await SaveAsync(batch, entity, wasLive, StoredEntity.SizeOf(entity.Data), cancellationToken);
        await store.ReplaceReferencesAsync(batch.WorkspaceId, entity.EntityType, entity.EntityId, [], cancellationToken);
        await LogAsync(batch, entity, opId, kind, [], cancellationToken);
    }

    /// <summary>
    /// O que apontava para a entidade excluída: o filho vai junto, e a
    /// referência fraca (a etiqueta do post-it) fica vazia. É a mesma cascata
    /// do banco local, então os dois lados terminam iguais.
    /// </summary>
    private async Task CascadeAsync(
        Batch batch,
        string type,
        Guid id,
        HashSet<(string, Guid)> visited,
        CancellationToken cancellationToken)
    {
        if (!visited.Add((type, id)))
        {
            return;
        }

        foreach (var reference in await store.FindReferencesToAsync(batch.WorkspaceId, type, id, cancellationToken))
        {
            var child = await store.FindAsync(batch.WorkspaceId, reference.FromType, reference.FromId, cancellationToken);

            if (child is null || child.IsDeleted || SyncRegistry.Find(child.EntityType) is not { } childDefinition)
            {
                continue;
            }

            if (reference.OnDelete == SyncDeleteBehavior.Cascade)
            {
                await TombstoneAsync(batch, child, null, "cascade_delete", cancellationToken);
                await CascadeAsync(batch, child.EntityType, child.EntityId, visited, cancellationToken);
                continue;
            }

            child.Version++;
            child.LastSeq = ++batch.Seq;
            child.Data[reference.Field] = SyncValue.Null;
            child.FieldVersions[reference.Field] = new FieldStamp(child.Version, batch.Caller.DeviceId);
            child.UpdatedByUserId = batch.Caller.UserId;
            child.UpdatedByDeviceId = batch.Caller.DeviceId;
            child.UpdatedAt = batch.Now;

            await SaveAsync(batch, child, wasLive: true, StoredEntity.SizeOf(child.Data), cancellationToken);
            await store.ReplaceReferencesAsync(batch.WorkspaceId, child.EntityType, child.EntityId, ReferencesOf(childDefinition, child), cancellationToken);
            await LogAsync(batch, child, null, "cascade_set_null", [reference.Field], cancellationToken);
        }
    }

    public static IReadOnlyList<StoredReference> ReferencesOf(SyncEntityDefinition definition, StoredEntity entity) =>
        [
            .. definition.References
                .Select(reference => (reference, target: SyncValue.ReadGuid(entity.Data.GetValueOrDefault(reference.Field, SyncValue.Null))))
                .Where(pair => pair.target is not null)
                .Select(pair => new StoredReference(
                    entity.EntityType,
                    entity.EntityId,
                    pair.reference.Field,
                    pair.reference.TargetType,
                    pair.target!.Value,
                    pair.reference.OnDelete)),
        ];

    private Task LogAsync(Batch batch, StoredEntity entity, Guid? opId, string kind, IReadOnlyList<string> fields, CancellationToken cancellationToken) =>
        store.AppendLogAsync(
            new ChangeLogEntry(
                batch.WorkspaceId,
                entity.LastSeq,
                entity.EntityType,
                entity.EntityId,
                entity.Version,
                opId,
                batch.Caller.UserId,
                batch.Caller.DeviceId,
                kind,
                fields,
                batch.Now),
            cancellationToken);

    /// <summary>Grava e mantém a conta do que o workspace ocupa durante o lote.</summary>
    private async Task SaveAsync(Batch batch, StoredEntity entity, bool wasLive, int bytes, CancellationToken cancellationToken)
    {
        var before = entity.DataBytes;
        entity.DataBytes = bytes;

        await store.SaveAsync(entity, cancellationToken);

        batch.Entities += (entity.IsDeleted ? 0 : 1) - (wasLive ? 1 : 0);
        batch.Bytes += bytes - before;
    }

    /// <summary>Por que a escrita estoura a cota do workspace, ou <c>null</c>. Quem não cresce sempre passa.</summary>
    private string? QuotaProblem(Batch batch, bool becomesLive, long growth)
    {
        if (becomesLive && limits.MaxWorkspaceEntities > 0 && batch.Entities >= limits.MaxWorkspaceEntities)
        {
            return $"O workspace chegou ao teto de {limits.MaxWorkspaceEntities} entidades; exclua algo para liberar.";
        }

        return growth > 0 && limits.MaxWorkspaceBytes > 0 && batch.Bytes + growth > limits.MaxWorkspaceBytes
            ? $"O workspace chegou ao teto de {limits.MaxWorkspaceBytes} bytes; exclua ou encolha algo para liberar."
            : null;
    }

    private static OperationResult Rejected(SyncOperation operation, long version, string code, string message) =>
        new(operation.OpId, OperationOutcome.Rejected, false, version, version, [], new SyncError(code, message));

    /// <summary>O conteúdo da operação, para reconhecer o mesmo id reenviado com outra coisa.</summary>
    public static byte[] Hash(SyncOperation operation) =>
        SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(operation, SyncJson.Options));

    private sealed class Batch(SyncCaller caller, Guid workspaceId, long seq, DateTimeOffset now, WorkspaceUsage usage)
    {
        public SyncCaller Caller { get; } = caller;

        public Guid WorkspaceId { get; } = workspaceId;

        public long Seq { get; set; } = seq;

        public DateTimeOffset Now { get; } = now;

        public long Entities { get; set; } = usage.Entities;

        public long Bytes { get; set; } = usage.Bytes;
    }
}
