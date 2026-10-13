using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyTaskApp.Infrastructure.Sync.Capture;
using MyTaskApp.Infrastructure.Sync.Persistence;

namespace MyTaskApp.Infrastructure.Persistence;

internal sealed class DatabaseInitializer(
    MyTaskAppDbContext context,
    IOptions<DatabaseOptions> options,
    SyncCaptureSwitch captureSwitch,
    ILogger<DatabaseInitializer> logger) : IDatabaseInitializer
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
        var linked = await context.SyncState.AnyAsync(state => state.Status != SyncLinkStatus.Unlinked, cancellationToken);
        captureSwitch.Set(linked);

        logger.LogInformation("DatabaseReady {DatabasePath} {SyncLinked}", path, linked);
    }
}
