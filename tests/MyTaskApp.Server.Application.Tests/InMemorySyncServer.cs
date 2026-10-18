using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Server.Application.Tests;

/// <summary>
/// As portas do servidor em memória. Cada leitura e gravação copia a
/// entidade, como um banco faria — sem isso, um teste passaria por ter
/// alterado o objeto guardado por referência.
/// </summary>
internal sealed class InMemorySyncServer : ISyncStore, ISyncUnitOfWork, IWorkspaceAccess, IDeviceRegistry, IAuthorizationRevoker
{
    public static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<(Guid, string, Guid), StoredEntity> _entities = [];

    private readonly List<StoredReference> _references = [];

    private readonly Dictionary<Guid, long> _lastSeq = [];

    private readonly Dictionary<(Guid, Guid), ProcessedOperation> _processed = [];

    private readonly Dictionary<(Guid, Guid), WorkspaceRole> _members = [];

    private readonly Dictionary<(Guid, Guid), DeviceRecord> _devices = [];

    public InMemorySyncServer(PullLimits? pullLimits = null)
    {
        Push = new PushProcessor(this, this, this, Clock, NullLogger<PushProcessor>.Instance);
        Queries = new SyncQueries(this, this, this, pullLimits ?? new PullLimits());
        Devices = new DeviceUseCases(this, this, Clock, NullLogger<DeviceUseCases>.Instance);
    }

    public FakeTimeProvider Clock { get; } = new(Now);

    public PushProcessor Push { get; }

    public SyncQueries Queries { get; }

    public DeviceUseCases Devices { get; }

    public List<ChangeLogEntry> Log { get; } = [];

    public List<(Guid User, Guid Device)> Revoked { get; } = [];

    /// <summary>Quantas leituras de entidade ou referência o store atendeu: o N+1 apareceria aqui.</summary>
    public int Reads { get; set; }

    public Guid AddWorkspace(Guid owner, WorkspaceRole role = WorkspaceRole.Owner)
    {
        var workspace = Guid.CreateVersion7();
        _members[(owner, workspace)] = role;
        _lastSeq[workspace] = 0;
        return workspace;
    }

    public void AddMember(Guid workspace, Guid user, WorkspaceRole role) => _members[(user, workspace)] = role;

    public StoredEntity? Get(Guid workspace, string type, Guid id) =>
        _entities.TryGetValue((workspace, type, id), out var entity) ? Clone(entity) : null;

    public long LastSeqOf(Guid workspace) => _lastSeq.GetValueOrDefault(workspace);

    // ---- ISyncUnitOfWork ----

    public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> work, CancellationToken cancellationToken) => work(cancellationToken);

    // ---- ISyncStore ----

    public Task<long> LockWorkspaceAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        Task.FromResult(_lastSeq.GetValueOrDefault(workspaceId));

    public Task SetLastSeqAsync(Guid workspaceId, long seq, CancellationToken cancellationToken)
    {
        _lastSeq[workspaceId] = seq;
        return Task.CompletedTask;
    }

    public Task<long> GetLastSeqAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        Task.FromResult(_lastSeq.GetValueOrDefault(workspaceId));

    public Task<ProcessedOperation?> FindProcessedAsync(Guid workspaceId, Guid opId, CancellationToken cancellationToken) =>
        Task.FromResult(_processed.GetValueOrDefault((workspaceId, opId)));

    public Task SaveProcessedAsync(Guid workspaceId, SyncCaller caller, ProcessedOperation operation, DateTimeOffset at, CancellationToken cancellationToken)
    {
        _processed[(workspaceId, operation.OpId)] = operation;
        return Task.CompletedTask;
    }

    public Task<StoredEntity?> FindAsync(Guid workspaceId, string entityType, Guid entityId, CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult(Get(workspaceId, entityType, entityId));
    }

    public Task<IReadOnlyList<StoredEntity>> FindManyAsync(Guid workspaceId, IReadOnlyCollection<EntityRef> entities, CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult<IReadOnlyList<StoredEntity>>(
            [.. entities.Select(entity => Get(workspaceId, entity.Type, entity.Id)).OfType<StoredEntity>()]);
    }

    public Task<Guid?> FindByUniqueKeyAsync(Guid workspaceId, string entityType, string uniqueKey, Guid excluding, CancellationToken cancellationToken) =>
        Task.FromResult(_entities.Values
            .Where(entity => entity.WorkspaceId == workspaceId && entity.EntityType == entityType && !entity.IsDeleted
                && entity.UniqueKey == uniqueKey && entity.EntityId != excluding)
            .Select(entity => (Guid?)entity.EntityId)
            .FirstOrDefault());

    public Task SaveAsync(StoredEntity entity, CancellationToken cancellationToken)
    {
        _entities[(entity.WorkspaceId, entity.EntityType, entity.EntityId)] = Clone(entity);
        return Task.CompletedTask;
    }

    public Task ReplaceReferencesAsync(Guid workspaceId, string fromType, Guid fromId, IReadOnlyList<StoredReference> references, CancellationToken cancellationToken)
    {
        _references.RemoveAll(reference => reference.FromType == fromType && reference.FromId == fromId);
        _references.AddRange(references);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredReference>> FindReferencesToAsync(Guid workspaceId, string toType, Guid toId, CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult<IReadOnlyList<StoredReference>>([.. _references.Where(reference => reference.ToType == toType && reference.ToId == toId)]);
    }

    public Task<IReadOnlySet<EntityRef>> FindDeliveredAsync(Guid workspaceId, IReadOnlyCollection<EntityRef> targets, long afterSeq, CancellationToken cancellationToken)
    {
        Reads++;
        var wanted = targets.ToHashSet();

        return Task.FromResult<IReadOnlySet<EntityRef>>(_references
            .Where(reference => wanted.Contains(new EntityRef(reference.ToType, reference.ToId))
                && _entities.TryGetValue((workspaceId, reference.FromType, reference.FromId), out var from)
                && from.LastSeq <= afterSeq && !from.IsDeleted)
            .Select(reference => new EntityRef(reference.ToType, reference.ToId))
            .ToHashSet());
    }

    public Task<IReadOnlyList<StoredEntity>> FindFirstReferrersAsync(
        Guid workspaceId,
        string fromType,
        string toType,
        IReadOnlyCollection<Guid> toIds,
        long afterSeq,
        CancellationToken cancellationToken)
    {
        Reads++;
        var wanted = toIds.ToHashSet();

        return Task.FromResult<IReadOnlyList<StoredEntity>>(
        [
            .. _references
                .Where(reference => reference.FromType == fromType && reference.ToType == toType && wanted.Contains(reference.ToId))
                .Select(reference => (reference.ToId, From: _entities.GetValueOrDefault((workspaceId, reference.FromType, reference.FromId))))
                .Where(pair => pair.From is { IsDeleted: false } from && from.LastSeq > afterSeq)
                .GroupBy(pair => pair.ToId)
                .Select(group => Clone(group.MinBy(pair => pair.From!.LastSeq).From!)),
        ]);
    }

    public Task AppendLogAsync(ChangeLogEntry entry, CancellationToken cancellationToken)
    {
        Log.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StoredEntity>> ReadChangesAsync(Guid workspaceId, long afterSeq, int limit, bool includeDeleted, CancellationToken cancellationToken)
    {
        Reads++;
        return Task.FromResult<IReadOnlyList<StoredEntity>>(
        [
            .. _entities.Values
                .Where(entity => entity.WorkspaceId == workspaceId && entity.LastSeq > afterSeq && (includeDeleted || !entity.IsDeleted))
                .OrderBy(entity => entity.LastSeq)
                .Take(limit)
                .Select(Clone),
        ]);
    }

    public Task<long> CountAsync(Guid workspaceId, CancellationToken cancellationToken) =>
        Task.FromResult((long)_entities.Values.Count(entity => entity.WorkspaceId == workspaceId && !entity.IsDeleted));

    // ---- IWorkspaceAccess ----

    public Task<WorkspaceRole?> RoleOfAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken) =>
        Task.FromResult(_members.TryGetValue((userId, workspaceId), out var role) ? role : (WorkspaceRole?)null);

    public Task<IReadOnlyList<WorkspaceInfo>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkspaceInfo>>(
            [.. _members.Where(member => member.Key.Item1 == userId).Select(member => new WorkspaceInfo(member.Key.Item2, "Pessoal", member.Value, true))]);

    // ---- IDeviceRegistry ----

    public Task<DeviceRecord?> FindAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken) =>
        Task.FromResult(_devices.GetValueOrDefault((userId, deviceId)));

    Task<IReadOnlyList<DeviceRecord>> IDeviceRegistry.ListAsync(Guid userId, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<DeviceRecord>>([.. _devices.Values.Where(device => device.UserId == userId)]);

    public Task SaveAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        _devices[(device.UserId, device.DeviceId)] = device;
        return Task.CompletedTask;
    }

    // ---- IAuthorizationRevoker ----

    public Task RevokeDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        Revoked.Add((userId, deviceId));
        return Task.CompletedTask;
    }

    private static StoredEntity Clone(StoredEntity entity) => new()
    {
        WorkspaceId = entity.WorkspaceId,
        EntityType = entity.EntityType,
        EntityId = entity.EntityId,
        Version = entity.Version,
        LastSeq = entity.LastSeq,
        Data = entity.Data.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.Ordinal),
        FieldVersions = new Dictionary<string, FieldStamp>(entity.FieldVersions, StringComparer.Ordinal),
        UniqueKey = entity.UniqueKey,
        IsDeleted = entity.IsDeleted,
        CreatedByUserId = entity.CreatedByUserId,
        CreatedByDeviceId = entity.CreatedByDeviceId,
        OriginCreatedAt = entity.OriginCreatedAt,
        UpdatedByUserId = entity.UpdatedByUserId,
        UpdatedByDeviceId = entity.UpdatedByDeviceId,
        UpdatedAt = entity.UpdatedAt,
        DeletedAt = entity.DeletedAt,
    };
}

/// <summary>Monta operações do contrato sem repetir o boilerplate em cada teste.</summary>
internal static class Ops
{
    public static readonly DateTimeOffset Created = InMemorySyncServer.Now;

    public static SyncOperation Upsert(Guid device, string type, Guid id, long baseVersion, Dictionary<string, JsonElement> fields) =>
        new(Guid.CreateVersion7(), device, type, id, SyncOperationKind.Upsert, baseVersion, fields, Created);

    public static SyncOperation Delete(Guid device, string type, Guid id, long baseVersion) =>
        new(Guid.CreateVersion7(), device, type, id, SyncOperationKind.Delete, baseVersion, new Dictionary<string, JsonElement>());

    public static Dictionary<string, JsonElement> Task(string title = "Pagar boleto", int priority = 1) => new()
    {
        [SyncFields.Task.Title] = SyncValue.String(title),
        [SyncFields.Task.Priority] = SyncValue.Integer(priority),
        [SyncFields.Task.Reminder] = SyncValue.Object(
            (SyncFields.Reminder.IsEnabled, SyncValue.Boolean(false)),
            (SyncFields.Reminder.Anchor, SyncValue.Integer(0)),
            (SyncFields.Reminder.Offset, SyncValue.Duration(TimeSpan.Zero)),
            (SyncFields.Reminder.Repeat, SyncValue.Boolean(false)),
            (SyncFields.Reminder.RepeatEvery, SyncValue.Duration(TimeSpan.FromMinutes(15))),
            (SyncFields.Reminder.Channels, SyncValue.Integer(1))),
        [SyncFields.Task.ArchivedAt] = SyncValue.Null,
        [SyncFields.CreatedAt] = SyncValue.Instant(Created),
    };

    public static Dictionary<string, JsonElement> Occurrence(Guid taskId) => new()
    {
        [SyncFields.Occurrence.TaskId] = SyncValue.Guid(taskId),
        [SyncFields.Occurrence.Schedule] = SyncValue.Object(
            (SyncFields.Occurrence.Date, SyncValue.Null),
            (SyncFields.Occurrence.Time, SyncValue.Null)),
        [SyncFields.Occurrence.State] = SyncValue.Object(
            (SyncFields.Occurrence.Status, SyncValue.Integer(0)),
            (SyncFields.Occurrence.CompletedAt, SyncValue.Null)),
    };

    public static Dictionary<string, JsonElement> TimeEntry(Guid occurrenceId) => new()
    {
        [SyncFields.TimeEntry.OccurrenceId] = SyncValue.Guid(occurrenceId),
        [SyncFields.TimeEntry.StartedAt] = SyncValue.Instant(Created),
        [SyncFields.TimeEntry.EndedAt] = SyncValue.Instant(Created.AddMinutes(30)),
        [SyncFields.TimeEntry.Source] = SyncValue.Integer(0),
        [SyncFields.CreatedAt] = SyncValue.Instant(Created),
        [SyncFields.UpdatedAt] = SyncValue.Instant(Created),
    };

    public static Dictionary<string, JsonElement> Tag(string name, string color = "#00FF00") => new()
    {
        [SyncFields.Tag.Name] = SyncValue.String(name),
        [SyncFields.Tag.ColorHex] = SyncValue.String(color),
        [SyncFields.CreatedAt] = SyncValue.Instant(Created),
    };

    public static Dictionary<string, JsonElement> TaskTag(Guid taskId, Guid tagId) => new()
    {
        [SyncFields.TaskTag.TaskId] = SyncValue.Guid(taskId),
        [SyncFields.TaskTag.TagId] = SyncValue.Guid(tagId),
    };

    public static Dictionary<string, JsonElement> StickyNote(Guid? tagId) => new()
    {
        [SyncFields.StickyNote.Content] = SyncValue.String("Ligar"),
        [SyncFields.StickyNote.TagId] = SyncValue.Guid(tagId),
        [SyncFields.StickyNote.Appearance] = SyncValue.Object(
            (SyncFields.StickyNote.ColorMode, SyncValue.Integer(2)),
            (SyncFields.StickyNote.PaletteColor, SyncValue.Null),
            (SyncFields.StickyNote.Emphasis, SyncValue.Integer(0)),
            (SyncFields.StickyNote.Opacity, SyncValue.Integer(100))),
        [SyncFields.CreatedAt] = SyncValue.Instant(Created),
        [SyncFields.UpdatedAt] = SyncValue.Instant(Created),
    };

    public static Dictionary<string, JsonElement> With(this Dictionary<string, JsonElement> fields, string name, JsonElement value) =>
        new(fields) { [name] = value };
}
