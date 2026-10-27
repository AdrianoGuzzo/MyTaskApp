using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MyTaskApp.Server.Application;
using MyTaskApp.Server.Infrastructure;
using MyTaskApp.Server.Infrastructure.Persistence;
using Npgsql;
using Testcontainers.PostgreSql;

[assembly: AssemblyFixture(typeof(MyTaskApp.Server.IntegrationTests.PostgresFixture))]

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// Um PostgreSQL para a montagem inteira: o de <see cref="Variable"/>, se
/// houver, ou um contêiner do Testcontainers. Sem nenhum dos dois, cada teste
/// pula com o motivo — o CI do Windows não roda contêiner Linux.
/// </summary>
public sealed class PostgresFixture : IAsyncLifetime
{
    /// <summary>Uma connection string de um usuário que pode criar bancos.</summary>
    public const string Variable = "MYTASKAPP_TEST_SERVER_POSTGRES";

    private PostgreSqlContainer? _container;

    private string? _admin;

    public string? SkipReason { get; private set; }

    public async ValueTask InitializeAsync()
    {
        if (Environment.GetEnvironmentVariable(Variable) is { Length: > 0 } configured)
        {
            _admin = configured;
            return;
        }

        try
        {
            // Cada teste cria o seu banco, e cada banco guarda o seu pool de
            // conexões até o fim da montagem: com as classes em paralelo (dois
            // desktops, celular e desktop, a API), as 100 do padrão acabam.
            _container = new PostgreSqlBuilder("postgres:17-alpine")
                .WithCommand("-c", "max_connections=400")
                .Build();

            await _container.StartAsync();
            _admin = _container.GetConnectionString();
        }
        catch (Exception exception)
        {
            SkipReason = $"Sem Docker nem {Variable}: o servidor de sincronização não é testado aqui ({exception.GetType().Name}).";
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    /// <summary>Um banco novo, com as migrations do servidor aplicadas.</summary>
    public async Task<string> CreateDatabaseAsync(CancellationToken cancellationToken)
    {
        Assert.SkipWhen(SkipReason is not null, SkipReason ?? string.Empty);

        var name = $"mytaskapp_test_{Guid.NewGuid():N}";

        await using (var admin = new NpgsqlConnection(_admin))
        {
            await admin.OpenAsync(cancellationToken);
            await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // A conexão parada fecha em segundos: o pool de um teste que já acabou não
        // segura as do servidor até o fim da montagem (os testes da orquestração
        // somaram classes, e as 400 acabavam).
        var connectionString = new NpgsqlConnectionStringBuilder(_admin)
        {
            Database = name,
            ConnectionIdleLifetime = 5,
            ConnectionPruningInterval = 1,
        }.ConnectionString;

        await using var context = new ServerDbContext(new DbContextOptionsBuilder<ServerDbContext>().UseNpgsql(connectionString).Options);
        await context.Database.MigrateAsync(cancellationToken);

        return connectionString;
    }
}

/// <summary>O servidor sem HTTP: os casos de uso de verdade sobre um banco de verdade.</summary>
internal sealed class ServerHarness : IAsyncDisposable
{
    private ServerHarness(ServiceProvider services, string connectionString)
    {
        Services = services;
        ConnectionString = connectionString;
    }

    public ServiceProvider Services { get; }

    public string ConnectionString { get; }

    public static async Task<ServerHarness> CreateAsync(PostgresFixture fixture, CancellationToken cancellationToken)
    {
        var connectionString = await fixture.CreateDatabaseAsync(cancellationToken);

        var services = new ServiceCollection()
            .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .AddServerApplication()
            .AddServerInfrastructure(connectionString)
            .AddSingleton<IAuthorizationRevoker, NoRevoker>()
            .BuildServiceProvider(validateScopes: true);

        return new ServerHarness(services, connectionString);
    }

    /// <summary>Um usuário com o workspace pessoal, como o primeiro login deixaria.</summary>
    public async Task<(Guid User, Guid Workspace)> CreateUserAsync(CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        var user = new ApplicationUser { Id = Guid.CreateVersion7(), UserName = $"u{Guid.NewGuid():N}", Email = "teste@exemplo.com" };
        context.Users.Add(user);
        await context.SaveChangesAsync(cancellationToken);

        var workspace = await scope.ServiceProvider.GetRequiredService<WorkspaceProvisioner>().EnsurePersonalAsync(user.Id, cancellationToken);
        return (user.Id, workspace);
    }

    public async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    public ValueTask DisposeAsync() => Services.DisposeAsync();

    private sealed class NoRevoker : IAuthorizationRevoker
    {
        public Task RevokeDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
