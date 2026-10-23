using System.Globalization;
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

        await UseWriteAheadLogAsync(cancellationToken);

        // Dois processos migrando ao mesmo tempo esperam um ao outro: o próprio
        // EF trava a migration no SQLite (tabela __EFMigrationsLock).
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

    /// <summary>
    /// O banco em WAL, para dois processos no mesmo arquivo: o desktop e, na
    /// fase 2, o agente sem tela (ADR-085). Quem lê não espera quem escreve, e
    /// dois escritores esperam um ao outro — o Microsoft.Data.Sqlite repete o
    /// comando ocupado até o timeout dele, 30 s — em vez de falhar.
    /// </summary>
    /// <remarks>
    /// <para>
    /// O EF já cria o banco em WAL, e o modo fica gravado no arquivo. Isto
    /// garante o mesmo para o arquivo que chegou de outro jeito: um backup
    /// restaurado, um banco que alguém passou para outro modo. Pedir de novo
    /// não custa nada.
    /// </para>
    /// <para>
    /// Um arquivo que não aceita WAL (um banco em memória, um disco de rede)
    /// continua no modo de antes, com um aviso no log: o app de um processo só
    /// funciona igual.
    /// </para>
    /// </remarks>
    private async Task UseWriteAheadLogAsync(CancellationToken cancellationToken)
    {
        await context.Database.OpenConnectionAsync(cancellationToken);

        try
        {
            await using var command = context.Database.GetDbConnection().CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL;";

            var mode = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);

            if (!string.Equals(mode, "wal", StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("DatabaseJournalModeNotWal {JournalMode}", mode);
            }
        }
        finally
        {
            await context.Database.CloseConnectionAsync();
        }
    }
}
