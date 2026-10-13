using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Sync;
using MyTaskApp.Infrastructure.Sync.Capture;
using MyTaskApp.Infrastructure.Sync.Persistence;

namespace MyTaskApp.Infrastructure.Persistence;

internal sealed class DatabaseInitializer(
    MyTaskAppDbContext context,
    IOptions<DatabaseOptions> options,
    SyncCaptureSwitch captureSwitch,
    ILogger<DatabaseInitializer> logger,
    DevicePlacementState? placement = null,
    SyncDeviceDirectory? devices = null) : IDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var path = options.Value.ResolveFullPath();
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await context.Database.MigrateAsync(cancellationToken);

        // A captura da sincronização (ADR-061) liga antes da primeira gravação
        // da tela: com a conta ligada, nenhuma alteração pode escapar do outbox.
        var row = await context.SyncState.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        var linked = row is not null && row.Status != SyncLinkStatus.Unlinked;
        captureSwitch.Set(linked);

        // Quem é "este computador" (ADR-062), antes de a primeira lista ser montada.
        if (placement is not null && devices is not null)
        {
            SyncPlacementHydration.Apply(row, placement, devices);
        }

        logger.LogInformation("DatabaseReady {DatabasePath} {SyncLinked}", path, linked);
    }
}
