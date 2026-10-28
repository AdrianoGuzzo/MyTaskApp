using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Persistence;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// O banco pronto para dois processos no mesmo arquivo (fase 2, T-07): WAL,
/// escritores que esperam um ao outro e migration que roda uma vez só, mesmo
/// pedida duas vezes ao mesmo tempo. Cada contêiner faz o papel de um processo.
/// </summary>
public sealed class DatabaseInitializerTests : IAsyncDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"mytaskapp-init-{Guid.NewGuid():N}");
    private readonly List<ServiceProvider> _providers = [];

    private string DatabasePath => Path.Combine(_directory, "app.db");

    /// <summary>Uma conexão só sua, fora do pool: como a de outro processo.</summary>
    private SqliteConnection OwnConnection() =>
        new(new SqliteConnectionStringBuilder { DataSource = DatabasePath, Pooling = false }.ToString());

    private ServiceProvider Process()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Directory"] = _directory,
                ["Database:FileName"] = "app.db",
            })
            .Build();

        var provider = new ServiceCollection()
            .AddSingleton(typeof(ILogger<>), typeof(NullLogger<>))
            .AddApplication(configuration)
            .AddInfrastructure(configuration)
            .BuildServiceProvider(validateScopes: true);

        _providers.Add(provider);
        return provider;
    }

    /// <summary>Só o pool deste arquivo: o ClearAllPools derrubava testes em paralelo (ver TempSqliteDatabase).</summary>
    private void ClearThisFilesPool()
    {
        using var connection = new SqliteConnection($"Data Source={DatabasePath}");
        SqliteConnection.ClearPool(connection);
    }

    private static async Task InitializeAsync(ServiceProvider provider)
    {
        await using var scope = provider.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(Ct);
    }

    private static async Task<object?> ScalarAsync(SqliteConnection connection, string sql, SqliteTransaction? transaction = null)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        return await command.ExecuteScalarAsync(Ct);
    }

    [Fact]
    public async Task AfterInitialization_TheFileIsInWal()
    {
        await InitializeAsync(Process());

        await using var connection = OwnConnection();
        await connection.OpenAsync(Ct);

        (await ScalarAsync(connection, "PRAGMA journal_mode;")).Should().Be("wal");
    }

    /// <summary>
    /// Um processo mais velho que o esquema não sobe (ADR-085, item 3): o banco
    /// tem uma migration que ele não conhece, e ele recusa sem tocar em nada.
    /// </summary>
    [Fact]
    public async Task AnAppOlderThanTheSchema_RefusesToOpenIt()
    {
        await InitializeAsync(Process());

        await using (var connection = OwnConnection())
        {
            await connection.OpenAsync(Ct);
            await ScalarAsync(connection, "INSERT INTO \"__EFMigrationsHistory\" (\"MigrationId\", \"ProductVersion\") VALUES ('29991231000000_FromANewerVersion', '99.0.0');");
        }

        var older = async () => await InitializeAsync(Process());

        (await older.Should().ThrowAsync<DomainException>()).WithMessage(DatabaseInitializer.NewerSchemaMessage);
    }

    /// <summary>O desktop e o agente sobem juntos: o EF trava a migration, e ela roda uma vez.</summary>
    [Fact]
    public async Task TwoProcesses_InitializingAtOnce_MigrateOnce()
    {
        var first = Process();
        var second = Process();

        await Task.WhenAll(InitializeAsync(first), InitializeAsync(second));

        await using var scope = first.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MyTaskAppDbContext>();
        (await context.Database.GetAppliedMigrationsAsync(Ct)).Should().Equal(context.Database.GetMigrations());
    }

    /// <summary>
    /// O EF já cria o banco em WAL; o que a abertura garante é o arquivo que
    /// chegou de outro jeito — um backup restaurado, um banco passado para o
    /// modo antigo — voltar para WAL.
    /// </summary>
    [Fact]
    public async Task AFileInTheOldJournalMode_IsTurnedBackToWal()
    {
        var provider = Process();
        await InitializeAsync(provider);
        ClearThisFilesPool();

        // Sair do WAL exige o arquivo só para si: o pool do EF não pode segurá-lo.
        await using (var connection = OwnConnection())
        {
            await connection.OpenAsync(Ct);
            (await ScalarAsync(connection, "PRAGMA journal_mode=DELETE;")).Should().Be("delete");
        }

        await InitializeAsync(provider);

        await using var reopened = OwnConnection();
        await reopened.OpenAsync(Ct);
        (await ScalarAsync(reopened, "PRAGMA journal_mode;")).Should().Be("wal");
    }

    /// <summary>
    /// Com um escritor no meio de uma transação, o segundo espera e grava, em
    /// vez de falhar com "database is locked": o Microsoft.Data.Sqlite repete o
    /// comando ocupado até o timeout dele.
    /// </summary>
    [Fact]
    public async Task TwoWriters_WaitForEachOther_InsteadOfFailing()
    {
        await InitializeAsync(Process());
        await using var first = OwnConnection();
        await using var second = OwnConnection();
        await first.OpenAsync(Ct);
        await second.OpenAsync(Ct);
        await ScalarAsync(first, "CREATE TABLE Probe (Value INTEGER NOT NULL);");

        await using var holding = first.BeginTransaction(deferred: false);
        await ScalarAsync(first, "INSERT INTO Probe VALUES (1);", holding);

        // O "async" do Microsoft.Data.Sqlite roda síncrono: a espera vai para
        // outra thread, ou travaria a deste teste, que é quem vai fazer o commit.
        var waiting = Task.Run(() => ScalarAsync(second, "INSERT INTO Probe VALUES (2);"), Ct);
        await Task.Delay(300, Ct);
        waiting.IsCompleted.Should().BeFalse("o primeiro ainda segura a escrita");

        await holding.CommitAsync(Ct);
        await waiting;

        (await ScalarAsync(second, "SELECT COUNT(*) FROM Probe;")).Should().Be(2L);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in _providers)
        {
            await provider.DisposeAsync();
        }

        ClearThisFilesPool();

        for (var attempt = 1; Directory.Exists(_directory); attempt++)
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
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
