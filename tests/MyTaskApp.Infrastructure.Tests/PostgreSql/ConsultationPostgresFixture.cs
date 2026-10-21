using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Infrastructure.PostgreSql;
using Npgsql;
using Testcontainers.PostgreSql;

namespace MyTaskApp.Infrastructure.Tests.PostgreSql;

/// <summary>
/// Um PostgreSQL de verdade para a Consulta Inteligente (ADR-081): o de
/// <see cref="PostgresIntegrationTests.Variable"/>, se houver, ou um contêiner
/// <c>postgres:14-alpine</c> — a versão mais antiga em uso. Sem nenhum dos dois
/// (o CI do Windows não roda contêiner Linux), cada teste pula com o motivo.
/// </summary>
/// <remarks>
/// Monta um banco com tabelas, FK, comentário, uma função
/// <c>SECURITY DEFINER</c> que escreve e um procedimento — o que a IA poderia
/// tentar usar para escapar da leitura.
/// </remarks>
public sealed class ConsultationPostgresFixture : IAsyncLifetime
{
    public const string AdminSecret = "postgres-admin";

    public const string ReaderSecret = "postgres-leitura";

    private PostgreSqlContainer? _container;

    public string? SkipReason { get; private set; }

    public NpgsqlConnectionStringBuilder Admin { get; private set; } = new();

    public string Database { get; } = $"consulta_{Guid.NewGuid():N}"[..24];

    public string Other { get; } = $"outro_{Guid.NewGuid():N}"[..24];

    /// <summary>As senhas por referência, como o cofre do app as guardaria.</summary>
    public MapPasswordReader Passwords { get; } = new();

    public async ValueTask InitializeAsync()
    {
        try
        {
            if (Environment.GetEnvironmentVariable(PostgresIntegrationTests.Variable) is { Length: > 0 } configured)
            {
                Admin = new NpgsqlConnectionStringBuilder(configured) { Pooling = false };
            }
            else
            {
                _container = new PostgreSqlBuilder("postgres:14-alpine").Build();
                await _container.StartAsync();
                Admin = new NpgsqlConnectionStringBuilder(_container.GetConnectionString()) { Pooling = false };
            }
        }
        catch (Exception exception)
        {
            SkipReason = $"Sem Docker nem {PostgresIntegrationTests.Variable}: a Consulta Inteligente não é testada contra um PostgreSQL ({exception.GetType().Name}).";
            return;
        }

        Passwords.Set(AdminSecret, Admin.Password);

        var maintenance = Admin.Database ?? "postgres";
        await ExecuteAsync(maintenance, $"CREATE DATABASE {Database}");
        await ExecuteAsync(maintenance, $"CREATE DATABASE {Other}");

        await ExecuteAsync(Database,
            "CREATE TABLE clientes (id serial PRIMARY KEY, nome text NOT NULL, situacao text NOT NULL DEFAULT 'pendente');" +
            "COMMENT ON TABLE clientes IS 'Cadastro de clientes';" +
            "COMMENT ON COLUMN clientes.situacao IS 'pendente, aprovado ou recusado';" +
            "INSERT INTO clientes (nome, situacao) SELECT 'Cliente ' || g, CASE WHEN g % 3 = 0 THEN 'aprovado' ELSE 'pendente' END " +
            "FROM generate_series(1, 300) g;" +
            "CREATE TABLE pedidos (id serial PRIMARY KEY, cliente_id int NOT NULL REFERENCES clientes (id), valor numeric(10,2), anexo bytea);" +
            "INSERT INTO pedidos (cliente_id, valor, anexo) SELECT 1 + g % 300, g * 1.5, '\\x0102'::bytea FROM generate_series(1, 50) g;" +
            "CREATE VIEW clientes_pendentes AS SELECT * FROM clientes WHERE situacao = 'pendente';" +
            "CREATE FUNCTION aprovar_todos() RETURNS int LANGUAGE sql SECURITY DEFINER AS " +
            "$$ UPDATE clientes SET situacao = 'aprovado'; SELECT 1 $$;" +
            "CREATE PROCEDURE limpar() LANGUAGE sql AS $$ DELETE FROM pedidos $$;");

        await ExecuteAsync(Other, "CREATE TABLE segredo (id int PRIMARY KEY, valor text); INSERT INTO segredo VALUES (1, 'nao leia');");
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
        else if (SkipReason is null && Admin.Host is not null)
        {
            var maintenance = Admin.Database ?? "postgres";
            await ExecuteAsync(maintenance, $"DROP DATABASE IF EXISTS {Database} WITH (FORCE)");
            await ExecuteAsync(maintenance, $"DROP DATABASE IF EXISTS {Other} WITH (FORCE)");
        }
    }

    public void SkipIfUnavailable() => Assert.SkipWhen(SkipReason is not null, SkipReason ?? string.Empty);

    /// <summary>A conexão administrativa (o <c>postgres</c> do contêiner), apontada para <paramref name="database"/>.</summary>
    public DatabaseConnectionSnapshot AdminConnection(string? database = null) =>
        Connection("Servidor local", Admin.Username!, database ?? Database, AdminSecret);

    /// <summary>A conexão de leitura, como "Preparar acesso de leitura" a cadastra.</summary>
    public DatabaseConnectionSnapshot ReaderConnection(string? database = null) =>
        Connection(
            "Servidor local (leitura)",
            Application.Consultations.IConsultationReadAccessProvisioner.RoleName,
            database ?? Database,
            ReaderSecret);

    public async Task<long> CountAsync(string sql)
    {
        var builder = new NpgsqlConnectionStringBuilder(Admin.ConnectionString) { Database = Database };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async Task ExecuteAsync(string database, string sql)
    {
        var builder = new NpgsqlConnectionStringBuilder(Admin.ConnectionString) { Database = database };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private DatabaseConnectionSnapshot Connection(string name, string username, string database, string secret) =>
        DatabaseConnection.Create(
                name, Admin.Host!, Admin.Port, database, username, DatabaseEnvironment.Development,
                DatabaseSslMode.Disable, null, ConnectionPermissions.FromFlags(EnvironmentPolicy.For(DatabaseEnvironment.Development).Defaults),
                PostgresTestSupport.Now)
            .Snapshot() with { SecretReference = secret };
}

/// <summary>O cofre de senhas do teste: uma senha por referência.</summary>
public sealed class MapPasswordReader : IPostgresPasswordReader
{
    private readonly Dictionary<string, string?> _passwords = new(StringComparer.Ordinal);

    public void Set(string reference, string? password) => _passwords[reference] = password;

    public Task<string?> ReadAsync(string? reference, CancellationToken cancellationToken = default) =>
        Task.FromResult(reference is not null && _passwords.TryGetValue(reference, out var password) ? password : null);
}
