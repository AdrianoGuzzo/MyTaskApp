using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Infrastructure.Tests.Sync;

/// <summary>
/// Um desktop inteiro para a sincronização: Application e Infrastructure de
/// verdade sobre um SQLite em pasta própria, com a API trocada pelo que o
/// teste quiser. Reabrir a mesma pasta é reiniciar o app.
/// </summary>
internal sealed class SyncDeviceHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private SyncDeviceHarness(string folder, ServiceProvider services, FakeDeviceIdentity device, FakeTimeProvider clock)
    {
        Folder = folder;
        Services = services;
        Device = device;
        Clock = clock;
    }

    public string Folder { get; }

    public ServiceProvider Services { get; }

    public FakeDeviceIdentity Device { get; }

    public FakeTimeProvider Clock { get; }

    public SyncEngine Engine => Services.GetRequiredService<SyncEngine>();

    public static async Task<SyncDeviceHarness> CreateAsync(
        ISyncApi api,
        CancellationToken cancellationToken,
        string? folder = null,
        FakeDeviceIdentity? device = null,
        ISyncAuthenticator? authenticator = null)
    {
        folder ??= Path.Combine(Path.GetTempPath(), $"mytaskapp-sync-{Guid.NewGuid():N}");
        device ??= new FakeDeviceIdentity();
        var clock = new FakeTimeProvider(Now);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Database:Directory"] = folder,
                ["Application:TimeZoneId"] = "UTC",
            })
            .Build();

        var services = new ServiceCollection()
            .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<IDeviceIdentity>(device)
            .AddSingleton<IUseCaseRunner, ScopedRunner>()
            .AddApplication(configuration)
            .AddInfrastructure(configuration)
            .AddSingleton(api)
            .AddSingleton(authenticator ?? new AlwaysSignedIn())
            .BuildServiceProvider(validateScopes: true);

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(cancellationToken);
        }

        return new SyncDeviceHarness(folder, services, device, clock);
    }

    public async Task<T> RunAsync<T>(Func<IServiceProvider, Task<T>> work)
    {
        await using var scope = Services.CreateAsyncScope();
        return await work(scope.ServiceProvider);
    }

    public async Task RunAsync(Func<IServiceProvider, Task> work)
    {
        await using var scope = Services.CreateAsyncScope();
        await work(scope.ServiceProvider);
    }

    public Task<T> StoreAsync<T>(Func<ISyncLocalStore, Task<T>> work) =>
        RunAsync(provider => work(provider.GetRequiredService<ISyncLocalStore>()));

    public Task StoreAsync(Func<ISyncLocalStore, Task> work) =>
        RunAsync(provider => work(provider.GetRequiredService<ISyncLocalStore>()));

    /// <summary>Liga à conta; <paramref name="joined"/> pula a primeira conexão (o pull completo e a varredura).</summary>
    public async Task LinkAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken, bool joined = true)
    {
        await StoreAsync(store => store.LinkAsync(
            new SyncAccountLink(userId, "Ana", "ana@exemplo.com", workspaceId, Device.DeviceId, new Uri("https://sync.exemplo.com/")),
            cancellationToken));

        if (joined)
        {
            await StoreAsync(store => store.MarkSchemaCurrentAsync(SyncSchema.Current, cancellationToken));
        }
    }

    public async Task SaveAsync(Action<MyTaskAppDbContext> change, CancellationToken cancellationToken)
    {
        await using var scope = Services.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<MyTaskAppDbContext>();
        change(context);
        await context.SaveChangesAsync(cancellationToken);
    }

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

    /// <summary>Apaga a pasta. Separado do dispose, para "reiniciar" reabrindo a mesma.</summary>
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

    private sealed class AlwaysSignedIn : ISyncAuthenticator
    {
        public bool IsAvailable => true;

        public Uri? ServerBaseUrl { get; } = new("https://sync.exemplo.com/");

        public Task<SyncAuthorization> BeginAuthorizationAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("token");

        public void ForgetAccessToken()
        {
        }

        public Task<bool> HasCredentialsAsync(CancellationToken cancellationToken) => Task.FromResult(true);

        public Task SignOutAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

/// <summary>
/// Um servidor de mentira, roteirizado: guarda cada push, responde com o que
/// o teste mandar e entrega as páginas de pull que o teste enfileirar.
/// </summary>
internal sealed class ScriptedSyncApi : ISyncApi
{
    public List<string> Calls { get; } = [];

    public List<SyncOperation> Pushed { get; } = [];

    /// <summary>O que o servidor responde a cada operação; o padrão aplica tudo.</summary>
    public Func<SyncOperation, OperationResult> Respond { get; set; } = Applied;

    /// <summary>Operações já vistas: o reenvio devolve o mesmo resultado, marcado como duplicado.</summary>
    public Dictionary<Guid, OperationResult> Processed { get; } = [];

    public Queue<PullResponse> Pages { get; } = new();

    /// <summary>Grava o push e então falha, como uma resposta perdida depois do commit.</summary>
    public bool LoseTheNextPushResponse { get; set; }

    public SyncApiException? FailHello { get; set; }

    public static OperationResult Applied(SyncOperation operation) =>
        new(operation.OpId, OperationOutcome.Applied, false, operation.BaseVersion + 1, operation.BaseVersion, []);

    public Task<MeResponse> MeAsync(CancellationToken cancellationToken) =>
        Task.FromResult(new MeResponse(new UserInfo(Guid.CreateVersion7(), "Ana", "ana@exemplo.com"), null));

    public Task<IReadOnlyList<WorkspaceInfo>> WorkspacesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<WorkspaceInfo>>([new WorkspaceInfo(Guid.CreateVersion7(), "Pessoal", WorkspaceRole.Owner, true)]);

    public Task<DeviceInfo> HelloAsync(DeviceHello hello, CancellationToken cancellationToken)
    {
        Calls.Add("hello");

        if (FailHello is { } failure)
        {
            throw failure;
        }

        return Task.FromResult(new DeviceInfo(Guid.Empty, "PC", hello.Platform, hello.AppVersion, SyncDeviceHarness.Now, null, null, true));
    }

    /// <summary>Os dispositivos da conta que o servidor devolve; o padrão é nenhum.</summary>
    public IReadOnlyList<DeviceInfo> Devices { get; set; } = [];

    public Task<IReadOnlyList<DeviceInfo>> DevicesAsync(CancellationToken cancellationToken) => Task.FromResult(Devices);

    public Task<DeviceInfo> RenameDeviceAsync(Guid deviceId, string nickname, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<DeviceInfo> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<PushResponse> PushAsync(Guid workspaceId, PushRequest request, CancellationToken cancellationToken)
    {
        Calls.Add("push");
        Pushed.AddRange(request.Operations);

        var results = request.Operations
            .Select(operation => Processed.TryGetValue(operation.OpId, out var stored)
                ? stored with { Duplicate = true }
                : Processed[operation.OpId] = Respond(operation))
            .ToList();

        if (LoseTheNextPushResponse)
        {
            LoseTheNextPushResponse = false;
            throw new SyncApiException(SyncFailureKind.Transient, "O servidor demorou demais para responder.");
        }

        return Task.FromResult(new PushResponse(results, SyncDeviceHarness.Now));
    }

    public Task<PullResponse> PullAsync(Guid workspaceId, string cursor, int limit, CancellationToken cancellationToken)
    {
        Calls.Add("pull");
        return Task.FromResult(Pages.TryDequeue(out var page) ? page : new PullResponse([], cursor, false));
    }

    /// <summary>O estado do servidor que a busca por id devolve; o que não está aqui não existe.</summary>
    public List<SyncChange> Fetchable { get; } = [];

    public Task<FetchResponse> FetchAsync(Guid workspaceId, FetchRequest request, CancellationToken cancellationToken)
    {
        Calls.Add("fetch");

        return Task.FromResult(new FetchResponse(
            [.. Fetchable.Where(change => request.Entities.Contains(new EntityRef(change.EntityType, change.EntityId)))]));
    }
}
