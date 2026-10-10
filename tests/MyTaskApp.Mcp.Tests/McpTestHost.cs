using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using MyTaskApp.Application;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Mcp;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Infrastructure;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Mcp;
using MyTaskApp.Mcp.Hosting;

namespace MyTaskApp.Mcp.Tests;

/// <summary>
/// O app sem a tela: o mesmo contêiner que o Desktop monta, um SQLite
/// temporário com as migrations reais e o gerente do servidor MCP. O cofre e o
/// token ficam em memória — nada chega à pasta de dados do usuário.
/// </summary>
internal sealed class McpTestHost : IAsyncDisposable
{
    private readonly string _directory;

    private McpTestHost(ServiceProvider services, CapturingLoggerProvider logs, string directory)
    {
        Services = services;
        Logs = logs;
        _directory = directory;
    }

    public ServiceProvider Services { get; }

    public CapturingLoggerProvider Logs { get; }

    public InMemoryTokenStore Tokens => (InMemoryTokenStore)Services.GetRequiredService<IMcpAccessTokenStore>();

    public InMemoryCredentialStore Credentials => (InMemoryCredentialStore)Services.GetRequiredService<IDatabaseCredentialStore>();

    public IMcpServerManager Manager => Services.GetRequiredService<IMcpServerManager>();

    public IUseCaseRunner Runner => Services.GetRequiredService<IUseCaseRunner>();

    public int Port { get; private set; }

    public Uri Endpoint => McpServerSettings.EndpointFor(Port);

    public static async Task<McpTestHost> CreateAsync(bool start = true, bool readOnly = false, CancellationToken cancellationToken = default)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"mytaskapp-mcp-{Guid.NewGuid():N}");
        var logs = new CapturingLoggerProvider();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Directory"] = directory,
                ["Database:FileName"] = "app.db",
                ["Application:TimeZoneId"] = "America/Sao_Paulo",
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug).AddProvider(logs))
            .AddSingleton<IUseCaseRunner, ScopedRunner>()
            .AddApplication(configuration)
            .AddInfrastructure(configuration)
            .AddMcpServerHost()

            // Depois da Infrastructure: o último registro é o que o contêiner entrega.
            .AddSingleton<IMcpAccessTokenStore, InMemoryTokenStore>()
            .AddSingleton<IDatabaseCredentialStore, InMemoryCredentialStore>()
            .BuildServiceProvider(validateScopes: true);

        var host = new McpTestHost(services, logs, directory) { Port = FreePort() };

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(cancellationToken);
        }

        await host.SaveSettingsAsync(enabled: true, host.Port, readOnly, cancellationToken);

        if (start)
        {
            var status = await host.Manager.StartAsync(cancellationToken);

            if (!status.IsRunning)
            {
                throw new InvalidOperationException($"O servidor não subiu: {status.Error}");
            }
        }

        return host;
    }

    public Task<McpServerSettings> SaveSettingsAsync(bool enabled, int port, bool readOnly = false, CancellationToken cancellationToken = default) =>
        Runner.RunAsync<UpdateMcpServerSettingsHandler, McpServerSettings>(
            (handler, token) => handler.HandleAsync(new UpdateMcpServerSettings(enabled, port, StartWithApp: false, readOnly), token),
            cancellationToken);

    public async Task<McpClient> ConnectAsync(string? token = null, CancellationToken cancellationToken = default)
    {
        token ??= await Manager.GetAccessTokenAsync(cancellationToken);

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = Endpoint,
            Name = "teste",
            AdditionalHeaders = new Dictionary<string, string> { ["Authorization"] = $"Bearer {token}" },
        });

        return await McpClient.CreateAsync(transport, cancellationToken: cancellationToken);
    }

    /// <summary>Uma porta que estava livre agora há pouco.</summary>
    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();

        // Só o pool deste arquivo: limpar todos derruba conexões de outros testes em paralelo.
        var file = Path.Combine(_directory, "app.db");
        SqliteConnection.ClearPool(new SqliteConnection($"Data Source={file}"));

        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // Pasta temporária: sobra para o sistema limpar.
        }
    }
}

/// <summary>O <c>ScopedUseCaseRunner</c> do Desktop: um escopo por operação (ADR-012).</summary>
internal sealed class ScopedRunner(IServiceScopeFactory scopes) : IUseCaseRunner
{
    public async Task<TResult> RunAsync<THandler, TResult>(
        Func<THandler, CancellationToken, Task<TResult>> operation,
        CancellationToken cancellationToken = default)
        where THandler : notnull
    {
        await using var scope = scopes.CreateAsyncScope();
        return await operation(scope.ServiceProvider.GetRequiredService<THandler>(), cancellationToken);
    }

    public async Task RunAsync<THandler>(
        Func<THandler, CancellationToken, Task> operation,
        CancellationToken cancellationToken = default)
        where THandler : notnull
    {
        await using var scope = scopes.CreateAsyncScope();
        await operation(scope.ServiceProvider.GetRequiredService<THandler>(), cancellationToken);
    }
}

internal sealed class InMemoryTokenStore : IMcpAccessTokenStore
{
    public string? Token { get; private set; }

    public Task<string?> ReadAsync(CancellationToken cancellationToken = default) => Task.FromResult(Token);

    public Task WriteAsync(string token, CancellationToken cancellationToken = default)
    {
        Token = token;
        return Task.CompletedTask;
    }
}

/// <summary>O cofre das senhas de banco, em memória: o teste sabe exatamente o que foi guardado.</summary>
internal sealed class InMemoryCredentialStore : IDatabaseCredentialStore
{
    public ConcurrentDictionary<string, string> Secrets { get; } = new();

    public Task<string> StoreAsync(Guid connectionId, SecretText password, CancellationToken cancellationToken = default)
    {
        var reference = "postgres-" + connectionId.ToString("N");
        Secrets[reference] = password.Reveal();
        return Task.FromResult(reference);
    }

    public Task<bool> HasAsync(string? reference, CancellationToken cancellationToken = default) =>
        Task.FromResult(reference is not null && Secrets.ContainsKey(reference));

    public Task DeleteAsync(string? reference, CancellationToken cancellationToken = default)
    {
        if (reference is not null)
        {
            Secrets.TryRemove(reference, out _);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Tudo o que foi para o log, com os argumentos já formatados — para provar que segredo nenhum foi.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _lines = new();

    public IReadOnlyCollection<string> Lines => _lines;

    public string All => string.Join(Environment.NewLine, _lines);

    public ILogger CreateLogger(string categoryName) => new Logger(categoryName, _lines);

    public void Dispose()
    {
    }

    private sealed class Logger(string category, ConcurrentQueue<string> lines) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            lines.Enqueue($"{logLevel} {category} {formatter(state, exception)} {exception}");
    }
}

/// <summary>Chamar uma ferramenta e ler a resposta como JSON.</summary>
internal static class McpClientCalls
{
    public static async Task<JsonElement> CallJsonAsync(
        this McpClient client,
        string tool,
        Dictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: cancellationToken);

        if (result.IsError is true)
        {
            throw new McpToolError(tool, Text(result));
        }

        return JsonDocument.Parse(Text(result)).RootElement.Clone();
    }

    public static async Task<string> CallErrorAsync(
        this McpClient client,
        string tool,
        Dictionary<string, object?>? arguments = null,
        CancellationToken cancellationToken = default)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: cancellationToken);

        result.IsError.Should().BeTrue($"{tool} deveria ter recusado, e respondeu {Text(result)}");
        return Text(result);
    }

    public static string Text(CallToolResult result) =>
        string.Concat(result.Content.OfType<TextContentBlock>().Select(block => block.Text));
}

internal sealed class McpToolError(string tool, string message) : Exception($"{tool}: {message}");
