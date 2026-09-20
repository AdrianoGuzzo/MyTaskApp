using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using MyTaskApp.Infrastructure.Persistence;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// Banco SQLite em arquivo temporário, criado pelas <b>migrations reais</b>.
/// Arquivo em vez de in-memory de propósito: assim a migration também é testada,
/// e não só o modelo do EF.
/// </summary>
internal sealed class TempSqliteDatabase : IAsyncDisposable
{
    private readonly string _path =
        Path.Combine(Path.GetTempPath(), $"mytaskapp-{Guid.NewGuid():N}.db");

    public string ConnectionString => $"Data Source={_path}";

    public MyTaskAppDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<MyTaskAppDbContext>()
            .UseSqlite(ConnectionString)
            .Options);

    public async Task<TempSqliteDatabase> MigrateAsync(CancellationToken cancellationToken)
    {
        await using var context = CreateContext();
        await context.Database.MigrateAsync(cancellationToken);
        return this;
    }

    public async ValueTask DisposeAsync()
    {
        SqliteConnection.ClearAllPools();
        await Task.Yield();

        if (File.Exists(_path))
        {
            File.Delete(_path);
        }
    }
}
