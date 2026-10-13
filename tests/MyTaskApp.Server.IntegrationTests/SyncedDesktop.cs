using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Secrets;
using MyTaskApp.Infrastructure.Sync.Http;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// Um desktop inteiro contra a API de verdade (ADR-061): Application e
/// Infrastructure sobre um SQLite em pasta própria, o login pelo "navegador"
/// do <see cref="ApiFactory"/> e a volta pela porta de loopback real. Só a
/// rede é trocada — pelo servidor em memória, com uma tomada que o teste puxa.
/// </summary>
internal sealed class SyncedDesktop : IAsyncDisposable
{
    private readonly ApiFactory _api;

    private SyncedDesktop(ApiFactory api, string folder, ServiceProvider services, TestDevice device, MemorySecretStore secrets, Network network, int callbackPort)
    {
        _api = api;
        Folder = folder;
        Services = services;
        Device = device;
        Secrets = secrets;
        Net = network;
        CallbackPort = callbackPort;
    }

    public string Folder { get; }

    public ServiceProvider Services { get; }

    public TestDevice Device { get; }

    public MemorySecretStore Secrets { get; }

    public Network Net { get; }

    public int CallbackPort { get; }

    public SyncEngine Engine => Services.GetRequiredService<SyncEngine>();

    public ISyncAccount Account => Services.GetRequiredService<ISyncAccount>();

    /// <summary>Uma porta livre para a volta do login, registrada no servidor do teste.</summary>
    public static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    public static async Task<SyncedDesktop> CreateAsync(
        ApiFactory api,
        int callbackPort,
        string nickname,
        CancellationToken cancellationToken,
        string? folder = null,
        TestDevice? device = null,
        MemorySecretStore? secrets = null,
        Network? network = null)
    {
        folder ??= Path.Combine(Path.GetTempPath(), $"mytaskapp-e2e-{Guid.NewGuid():N}");
        device ??= new TestDevice(nickname);
        secrets ??= new MemorySecretStore();
        network ??= new Network();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Directory"] = folder,
                ["Application:TimeZoneId"] = "UTC",
                ["Sync:ApiBaseUrl"] = "http://localhost/",
                ["Sync:CallbackPort"] = callbackPort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            })
            .Build();

        // O relógio do desktop fica parado: o access token nunca vence deste
        // lado, e só o servidor decide quando ele expirou.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);

        var services = new ServiceCollection()
            .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<IDeviceIdentity>(device)
            .AddSingleton<IUseCaseRunner, ScopedRunner>()
            .AddApplication(configuration)
            .AddInfrastructure(configuration)
            .AddSingleton<ISecretStore>(secrets)
            .AddSingleton(provider =>
            {
                return new SyncHttp(new HttpClient(network.Over(api.Server.CreateHandler())), provider.GetRequiredService<SyncOptions>(), provider.GetRequiredService<ILogger<SyncHttp>>());
            })
            .BuildServiceProvider(validateScopes: true);

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(cancellationToken);
        }

        return new SyncedDesktop(api, folder, services, device, secrets, network, callbackPort);
    }

    /// <summary>Fecha e abre o app de novo sobre o mesmo banco, o mesmo cofre e o mesmo DeviceId.</summary>
    public async Task<SyncedDesktop> RestartAsync(CancellationToken cancellationToken)
    {
        await DisposeAsync();
        return await CreateAsync(_api, CallbackPort, Device.DefaultNickname, cancellationToken, Folder, Device, Secrets, Net);
    }

    /// <summary>"Conectar com o Google": o navegador segue os redirects e volta pela porta de loopback.</summary>
    public async Task ConnectAsync(string googleSubject, CancellationToken cancellationToken)
    {
        var authorization = await Account.BeginConnectAsync(cancellationToken);
        var callback = await _api.FollowToCallbackAsync(googleSubject, authorization.AuthorizeUrl.PathAndQuery);

        callback.Port.Should().Be(CallbackPort);

        using (var browser = new HttpClient())
        {
            using var landing = await browser.GetAsync(callback, cancellationToken);
            landing.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await authorization.Completion.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
    }

    public async Task<SyncCycleResult> SyncAsync(CancellationToken cancellationToken) =>
        await Engine.RunCycleAsync(cancellationToken);

    /// <summary>Um ciclo que tem de terminar bem.</summary>
    public async Task SyncedAsync(CancellationToken cancellationToken)
    {
        var result = await SyncAsync(cancellationToken);
        result.Status.Should().Be(SyncCycleStatus.Completed, result.Message);
    }

    public async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    public Task<T> UseCaseAsync<THandler, T>(Func<THandler, CancellationToken, Task<T>> operation)
        where THandler : notnull =>
        Services.GetRequiredService<IUseCaseRunner>().RunAsync(operation);

    public Task UseCaseAsync<THandler>(Func<THandler, CancellationToken, Task> operation)
        where THandler : notnull =>
        Services.GetRequiredService<IUseCaseRunner>().RunAsync(operation);

    public Task<SyncLocalState> StateAsync(CancellationToken cancellationToken) =>
        RunAsync(provider => provider.GetRequiredService<ISyncLocalStore>().ReadAsync(cancellationToken));

    public Task<IReadOnlyList<SyncConflictView>> ConflictsAsync(CancellationToken cancellationToken) =>
        RunAsync(provider => provider.GetRequiredService<ISyncLocalStore>().ListConflictsAsync(cancellationToken));

    /// <summary>Uma mudança como o app faz: pelo DbContext, com a captura do outbox ligada.</summary>
    public async Task ChangeAsync(Func<MyTaskAppDbContext, Task> change, CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MyTaskAppDbContext>();
        await change(context);
        await context.SaveChangesAsync(cancellationToken);
    }

    public Task ChangeAsync(Action<MyTaskAppDbContext> change, CancellationToken cancellationToken) =>
        ChangeAsync(context =>
        {
            change(context);
            return Task.CompletedTask;
        }, cancellationToken);

    public async Task<T> ReadAsync<T>(Func<MyTaskAppDbContext, Task<T>> query)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MyTaskAppDbContext>();
        return await query(context);
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearPool(new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(Folder, "mytaskapp.db")}"));
    }

    public void DeleteFolder()
    {
        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // Antivírus segurando o arquivo: fica na pasta temporária.
        }
    }

    private sealed class ScopedRunner(IServiceScopeFactory scopes) : IUseCaseRunner
    {
        public async Task<TResult> RunAsync<THandler, TResult>(Func<THandler, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
            where THandler : notnull
        {
            await using var scope = scopes.CreateAsyncScope();
            return await operation(scope.ServiceProvider.GetRequiredService<THandler>(), cancellationToken);
        }

        public async Task RunAsync<THandler>(Func<THandler, CancellationToken, Task> operation, CancellationToken cancellationToken = default)
            where THandler : notnull
        {
            await using var scope = scopes.CreateAsyncScope();
            await operation(scope.ServiceProvider.GetRequiredService<THandler>(), cancellationToken);
        }
    }
}

/// <summary>O DeviceId da instalação, trocável como o arquivo de verdade.</summary>
internal sealed class TestDevice(string nickname) : IDeviceIdentity
{
    public Guid DeviceId { get; private set; } = Guid.CreateVersion7();

    public string DefaultNickname { get; } = nickname;

    public void Rotate() => DeviceId = Guid.CreateVersion7();
}

/// <summary>O cofre do sistema, na memória: sobrevive ao "reinício" do app no teste.</summary>
internal sealed class MemorySecretStore : ISecretStore
{
    private readonly ConcurrentDictionary<string, string> _values = new();

    public Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default) =>
        Task.FromResult(_values.TryGetValue(name, out var value) ? value : null);

    public Task WriteAsync(string name, string value, CancellationToken cancellationToken = default)
    {
        _values[name] = value;
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
    {
        _values.TryRemove(name, out _);
        return Task.CompletedTask;
    }
}

/// <summary>
/// A rede do desktop: registra cada chamada e deixa o teste tirar o cabo ou
/// perder a resposta de um push que o servidor já gravou.
/// </summary>
internal sealed class Network
{
    private readonly ConcurrentQueue<string> _calls = new();

    public bool Offline { get; set; }

    public bool LoseTheNextPushResponse { get; set; }

    public IReadOnlyList<string> Calls => [.. _calls];

    /// <summary>Um handler novo a cada "abertura" do app; o estado da rede é este objeto.</summary>
    public HttpMessageHandler Over(HttpMessageHandler server) => new Cable(this, server);

    private sealed class Cable(Network network, HttpMessageHandler server) : DelegatingHandler(server)
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;

            if (network.Offline)
            {
                network._calls.Enqueue($"offline {path}");
                throw new HttpRequestException(HttpRequestError.ConnectionError, "Sem rede no teste.");
            }

            var response = await base.SendAsync(request, cancellationToken);
            network._calls.Enqueue($"{(int)response.StatusCode} {path}");

            if (network.LoseTheNextPushResponse && path.EndsWith("/sync/push", StringComparison.Ordinal))
            {
                network.LoseTheNextPushResponse = false;
                response.Dispose();
                throw new HttpRequestException(HttpRequestError.ConnectionError, "A resposta se perdeu no teste.");
            }

            return response;
        }
    }
}
