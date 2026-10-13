using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Sync.Capture;
using MyTaskApp.Infrastructure.Sync.Persistence;
using MyTaskApp.Infrastructure.Tests.Persistence;

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

    public ValueTask DisposeAsync() => Database.DisposeAsync();
}
