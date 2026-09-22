using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
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

    /// <summary>
    /// Migra só até a versão informada, para poder popular o banco <b>como ele
    /// era</b> e então aplicar a migration seguinte. É a única forma de testar o
    /// que uma migration faz — ou deixa de fazer — com dados que já existiam.
    /// </summary>
    public async Task<TempSqliteDatabase> MigrateToAsync(
        string targetMigration,
        CancellationToken cancellationToken)
    {
        await using var context = CreateContext();

        await context.GetService<IMigrator>()
            .MigrateAsync(targetMigration, cancellationToken);

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
