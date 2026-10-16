using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Secrets;
using MyTaskApp.Infrastructure.Sync.Http;
using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// Um celular inteiro contra a API de verdade (ADR-065): a composição do app
/// (<see cref="MobileServices.AddMobileCore"/>) e os ViewModels das telas, só
/// sem MAUI. O login é o da tela Entrar, com um "navegador" que segue os
/// redirects do servidor até o esquema do app — como o Custom Tabs faria.
/// </summary>
internal sealed class SyncedPhone : IAsyncDisposable
{
    private SyncedPhone(string folder, ServiceProvider services, Network network)
    {
        Folder = folder;
        Services = services;
        Net = network;
    }

    public string Folder { get; }

    public ServiceProvider Services { get; }

    public Network Net { get; }

    public SyncEngine Engine => Services.GetRequiredService<SyncEngine>();

    public TaskListViewModel List => Services.GetRequiredService<TaskListViewModel>();

    public Guid DeviceId => Services.GetRequiredService<IDeviceIdentity>().DeviceId;

    public static async Task<SyncedPhone> CreateAsync(ApiFactory api, string googleSubject, string nickname, CancellationToken cancellationToken)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"mytaskapp-phone-{Guid.NewGuid():N}");
        var network = new Network();

        var services = new ServiceCollection()
            .AddLogging(logging => logging.SetMinimumLevel(LogLevel.Warning))
            .AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow))
            .AddSingleton<ISecretStore, MemorySecretStore>()
            .AddSingleton<IUiDispatcher, Inline>()
            .AddSingleton<INavigator, NoNavigation>()
            .AddSingleton<IDialogService, SilentDialogs>()
            .AddSingleton<IWebAuthenticatorPort>(new ServerBrowser(api, googleSubject))
            .AddMobileCore(new MobileHostOptions { DataDirectory = folder, DeviceName = nickname, ServerUrl = "http://localhost/" })
            .AddSingleton(provider => new SyncHttp(
                new HttpClient(network.Over(api.Server.CreateHandler())),
                provider.GetRequiredService<SyncOptions>(),
                provider.GetRequiredService<TimeProvider>(),
                provider.GetRequiredService<ILogger<SyncHttp>>()))
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(cancellationToken);
        }

        return new SyncedPhone(folder, services, network);
    }

    /// <summary>"Entrar com Google" na tela Entrar, do começo ao fim.</summary>
    public async Task ConnectAsync()
    {
        var connect = Services.GetRequiredService<ConnectViewModel>();

        await connect.SignInAsync();

        connect.State.Should().Be(ConnectState.Done, connect.Message);
    }

    public async Task SyncedAsync(CancellationToken cancellationToken)
    {
        var result = await Engine.RunCycleAsync(cancellationToken);
        result.Status.Should().Be(SyncCycleStatus.Completed, result.Message);
    }

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    public Task<T> UseCaseAsync<THandler, T>(Func<THandler, CancellationToken, Task<T>> operation)
        where THandler : notnull =>
        Services.GetRequiredService<IUseCaseRunner>().RunAsync(operation);

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        try
        {
            Directory.Delete(Folder, recursive: true);
        }
        catch (IOException)
        {
            // Antivírus segurando o arquivo: fica na pasta temporária.
        }
    }

    /// <summary>O Custom Tabs do teste: segue os redirects do servidor e devolve a query da volta pelo esquema do app.</summary>
    private sealed class ServerBrowser(ApiFactory api, string googleSubject) : IWebAuthenticatorPort
    {
        public async Task<IReadOnlyDictionary<string, string>?> AuthenticateAsync(Uri url, Uri callback, CancellationToken cancellationToken)
        {
            var back = await api.FollowToCallbackAsync(googleSubject, url.PathAndQuery);

            back.Scheme.Should().Be(callback.Scheme, "a volta é pelo esquema do app, não pelo loopback");

            return back.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(pair => pair.Split('=', 2))
                .ToDictionary(pair => Uri.UnescapeDataString(pair[0]), pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty);
        }
    }

    private sealed class Inline : IUiDispatcher
    {
        public void Post(Action action) => action();
    }

    private sealed class NoNavigation : INavigator
    {
        public Task OpenTaskAsync(Guid taskId) => Task.CompletedTask;

        public Task NewTaskAsync() => Task.CompletedTask;

        public Task EditTaskAsync(Guid taskId) => Task.CompletedTask;

        public Task OpenFiltersAsync() => Task.CompletedTask;

        public Task OpenConnectAsync() => Task.CompletedTask;

        public Task OpenTasksAsync() => Task.CompletedTask;

        public Task OpenAccountAsync() => Task.CompletedTask;

        public Task BackAsync() => Task.CompletedTask;
    }

    private sealed class SilentDialogs : IDialogService
    {
        public Task AlertAsync(string title, string message) =>
            throw new InvalidOperationException($"Alerta inesperado: {title} — {message}");

        public Task<bool> ConfirmAsync(string title, string message, string accept, string cancel) => Task.FromResult(true);

        public Task ToastAsync(string message) => Task.CompletedTask;

        public Task<string?> PromptAsync(string title, string message, string initialValue, int maxLength) => Task.FromResult<string?>(null);
    }
}
