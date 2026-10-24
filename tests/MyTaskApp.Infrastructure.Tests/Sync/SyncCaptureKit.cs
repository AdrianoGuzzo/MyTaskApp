using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Sync.Apply;
using MyTaskApp.Infrastructure.Sync.Capture;
using MyTaskApp.Infrastructure.Sync.Mapping;
using MyTaskApp.Infrastructure.Sync.Persistence;
using MyTaskApp.Infrastructure.Tests.Persistence;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Infrastructure.Tests.Sync;

internal sealed class FakeDeviceIdentity(Guid? deviceId = null) : IDeviceIdentity
{
    public Guid DeviceId { get; private set; } = deviceId ?? Guid.CreateVersion7();

    public string DefaultNickname { get; init; } = "PC-TESTE";

    public int Rotations { get; private set; }

    public void Rotate()
    {
        DeviceId = Guid.CreateVersion7();
        Rotations++;
    }
}

internal sealed class CountingTrigger : ISyncTrigger
{
    public int Signals { get; private set; }

    public event Action? Signaled
    {
        add { }
        remove { }
    }

    public void Signal() => Signals++;

    public Task WaitAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Um banco temporário com a captura ligada, como numa instalação conectada.</summary>
internal sealed class SyncCaptureKit : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private SyncCaptureKit(TempSqliteDatabase database)
    {
        Database = database;
        Interceptor = new SyncCaptureInterceptor(Switch, Device, Trigger, Clock);
        Switch.Set(true);
    }

    public TempSqliteDatabase Database { get; }

    public SyncCaptureSwitch Switch { get; } = new();

    public FakeDeviceIdentity Device { get; } = new();

    public CountingTrigger Trigger { get; } = new();

    public FakeTimeProvider Clock { get; } = new(Now);

    public SyncCaptureInterceptor Interceptor { get; }

    public static async Task<SyncCaptureKit> CreateAsync(CancellationToken cancellationToken) =>
        new(await new TempSqliteDatabase().MigrateAsync(cancellationToken));

    public MyTaskAppDbContext CreateContext() => Database.CreateContext(Interceptor);

    public async Task SaveAsync(Action<MyTaskAppDbContext> change, CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        change(context);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<List<SyncOutboxRow>> OutboxAsync(CancellationToken cancellationToken)
    {
        await using var context = Database.CreateContext();
        return await context.SyncOutbox.AsNoTracking().OrderBy(row => row.Seq).ToListAsync(cancellationToken);
    }

    public async Task<List<SyncEntityStateRow>> StatesAsync(CancellationToken cancellationToken)
    {
        await using var context = Database.CreateContext();
        return await context.SyncEntityStates.AsNoTracking().ToListAsync(cancellationToken);
    }

    /// <summary>Como se o servidor tivesse aceitado tudo: versão 1 e outbox vazio.</summary>
    public async Task AcknowledgeAllAsync(CancellationToken cancellationToken)
    {
        await using var context = Database.CreateContext();
        await context.SyncOutbox.ExecuteDeleteAsync(cancellationToken);
        await context.SyncEntityStates.ExecuteUpdateAsync(
            setters => setters.SetProperty(state => state.ServerVersion, state => state.ServerVersion + 1),
            cancellationToken);
    }

    /// <summary>Como se o motor tivesse mandado as operações e esperasse a resposta.</summary>
    public async Task MarkAllAttemptedAsync(CancellationToken cancellationToken)
    {
        await using var context = Database.CreateContext();
        await context.SyncOutbox.ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Attempted, true), cancellationToken);
    }

    public static Dictionary<string, JsonElement> Fields(SyncOutboxRow row) => SyncSnapshot.Read(row.Fields);

    /// <summary>O outbox como o servidor o devolveria num pull: cada criação, na versão dada.</summary>
    public async Task<List<SyncChange>> PageFromOutboxAsync(CancellationToken cancellationToken, long version = 1) =>
        [
            .. (await OutboxAsync(cancellationToken))
                .Where(row => row.Kind == SyncOperationKind.Upsert)
                .Select(row => new SyncChange(
                    row.EntityType,
                    row.EntityId,
                    version,
                    Deleted: false,
                    Fields(row),
                    CreatedByUserId: null,
                    Device.DeviceId,
                    row.OriginCreatedAt,
                    Device.DeviceId,
                    Now)),
        ];

    public RemoteChangeApplier Applier(MyTaskAppDbContext context) =>
        new(context, Clock, NullLogger<RemoteChangeApplier>.Instance);

    public async Task<RemoteApplyResult> ApplyAsync(
        IReadOnlyList<SyncChange> changes,
        CancellationToken cancellationToken,
        string? cursor = "c1",
        bool isolate = false)
    {
        await using var context = CreateContext();
        return await Applier(context).ApplyAsync(changes, cursor, isolate, cancellationToken);
    }

    public SyncReconciler Reconciler(MyTaskAppDbContext context) =>
        new(context, Device, Clock, Trigger, NullLogger<SyncReconciler>.Instance);

    /// <summary>Os campos compartilhados de tudo o que está no banco, como o mapper os vê.</summary>
    public async Task<Dictionary<(string Type, Guid Id), Dictionary<string, JsonElement>>> ProjectAllAsync(CancellationToken cancellationToken)
    {
        await using var context = Database.CreateContext();
        await context.Tasks.LoadAsync(cancellationToken);
        await context.Occurrences.LoadAsync(cancellationToken);
        await context.TaskItemTags.LoadAsync(cancellationToken);
        await context.Tags.LoadAsync(cancellationToken);
        await context.TagDirectories.LoadAsync(cancellationToken);
        await context.TagDirectoryCommands.LoadAsync(cancellationToken);
        await context.DevelopmentCommands.Include(command => command.Parameters).LoadAsync(cancellationToken);
        await context.TaskDevelopments.Include(development => development.Commands).LoadAsync(cancellationToken);
        await context.TimeEntries.LoadAsync(cancellationToken);
        await context.StickyNotes.LoadAsync(cancellationToken);

        var all = new Dictionary<(string, Guid), Dictionary<string, JsonElement>>();

        foreach (var mapper in SyncMappers.All)
        {
            foreach (var entry in context.ChangeTracker.Entries().Where(entry => entry.Metadata.ClrType == mapper.ClrType))
            {
                all[(mapper.Definition.Type, mapper.IdOf(entry))] = mapper.Project(entry);
            }
        }

        return all;
    }

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}
