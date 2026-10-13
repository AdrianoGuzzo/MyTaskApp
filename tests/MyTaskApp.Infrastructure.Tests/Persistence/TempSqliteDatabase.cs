using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
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

    /// <summary>Com os interceptores do app — a captura da sincronização (ADR-061).</summary>
    public MyTaskAppDbContext CreateContext(params IInterceptor[] interceptors) =>
        new(new DbContextOptionsBuilder<MyTaskAppDbContext>()
            .UseSqlite(ConnectionString)
            .AddInterceptors(interceptors)
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

    /// <summary>
    /// Grava pelo modelo atual num banco migrado só até uma versão antiga.
    /// Colunas que o modelo tem e o banco ainda não entram só durante a gravação
    /// e saem logo depois. Assim a migration seguinte as cria como em produção,
    /// e um campo novo numa tabela antiga não obriga a reescrever em SQL todo
    /// teste de upgrade que semeia por ela.
    /// </summary>
    public async Task SeedOnOlderSchemaAsync(CancellationToken cancellationToken, params object[] entities)
    {
        await using var context = CreateContext();
        var added = new List<(string Table, string Column)>();

        foreach (var entityType in context.Model.GetEntityTypes())
        {
            if (entityType.GetTableName() is not { } table)
            {
                continue;
            }

            var existing = (await context.Database
                .SqlQuery<string>($"SELECT name AS Value FROM pragma_table_info({table})")
                .ToListAsync(cancellationToken))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (existing.Count == 0)
            {
                // A tabela ainda não existe nesta versão: nada dela vai ser semeado.
                continue;
            }

            var store = StoreObjectIdentifier.Table(table, entityType.GetSchema());

            foreach (var property in entityType.GetProperties())
            {
                if (property.GetColumnName(store) is { } column
                    && !existing.Contains(column)
                    && !added.Contains((table, column)))
                {
                    var addColumn = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\"";
                    await context.Database.ExecuteSqlRawAsync(addColumn, cancellationToken);
                    added.Add((table, column));
                }
            }
        }

        context.AddRange(entities);
        await context.SaveChangesAsync(cancellationToken);

        foreach (var (table, column) in added)
        {
            var dropColumn = $"ALTER TABLE \"{table}\" DROP COLUMN \"{column}\"";
            await context.Database.ExecuteSqlRawAsync(dropColumn, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        // Só o pool deste arquivo. ClearAllPools fechava também as conexões de
        // outro teste rodando em paralelo, que então falhava ao abrir com
        // ObjectDisposedException no handle do SQLite — intermitente.
        using (var connection = new SqliteConnection(ConnectionString))
        {
            SqliteConnection.ClearPool(connection);
        }

        await Task.Yield();

        // No runner do CI o antivírus/indexador às vezes abre o .db recém-criado
        // e o Delete falha com "being used by another process". Tenta por um
        // tempo e, se não der, deixa o arquivo na pasta temporária: é limpeza,
        // não pode derrubar um teste que já passou.
        for (var attempt = 1; File.Exists(_path); attempt++)
        {
            try
            {
                File.Delete(_path);
            }
            catch (IOException) when (attempt < 20)
            {
                await Task.Delay(100);
            }
            catch (IOException)
            {
                return;
            }
        }
    }
}
