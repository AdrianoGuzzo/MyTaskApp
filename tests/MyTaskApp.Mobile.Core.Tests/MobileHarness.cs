using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Secrets;
using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.Tests.Fakes;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Mobile.Core.Tests;

/// <summary>
/// O celular inteiro, menos a tela: a composição de verdade
/// (<see cref="MobileServices.AddMobileCore"/>), os handlers de verdade e um
/// SQLite numa pasta temporária, migrado. Só as portas da plataforma são de mentira.
/// </summary>
internal sealed class MobileHarness : IAsyncDisposable
{
    public static readonly DateTimeOffset Now = new(2026, 10, 15, 15, 0, 0, TimeSpan.Zero);

    private MobileHarness(string folder, ServiceProvider services, FakeTimeProvider clock)
    {
        Folder = folder;
        Services = services;
        Clock = clock;
    }

    public string Folder { get; }

    public ServiceProvider Services { get; }

    public FakeTimeProvider Clock { get; }

    public RecordingNavigator Navigator => (RecordingNavigator)Services.GetRequiredService<INavigator>();

    public ScriptedDialogs Dialogs => (ScriptedDialogs)Services.GetRequiredService<IDialogService>();

    public FakeBrowser Browser => (FakeBrowser)Services.GetRequiredService<IWebAuthenticatorPort>();

    public IUseCaseRunner Runner => Services.GetRequiredService<IUseCaseRunner>();

    public static async Task<MobileHarness> CreateAsync(string? serverUrl = "https://sync.exemplo.com")
    {
        var folder = Path.Combine(Path.GetTempPath(), $"mytaskapp-mobile-{Guid.NewGuid():N}");
        var clock = new FakeTimeProvider(Now);

        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton<IUiDispatcher, InlineDispatcher>()
            .AddSingleton<INavigator, RecordingNavigator>()
            .AddSingleton<IDialogService, ScriptedDialogs>()
            .AddSingleton<IWebAuthenticatorPort, FakeBrowser>()
            .AddSingleton<ISecretStore, MemorySecretStore>()
            .AddMobileCore(new MobileHostOptions { DataDirectory = folder, DeviceName = "Pixel de teste", ServerUrl = serverUrl })
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        await using (var scope = services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<IDatabaseInitializer>().InitializeAsync(TestContext.Current.CancellationToken);
        }

        return new MobileHarness(folder, services, clock);
    }

    public T Get<T>()
        where T : notnull => Services.GetRequiredService<T>();

    public async Task<CreateTaskResult> CreateTaskAsync(
        string title,
        TaskPriority priority = TaskPriority.Normal,
        string? notes = null,
        DevicePlacement? placement = null,
        IReadOnlyCollection<Guid>? tags = null,
        TaskDeadline? deadline = null) =>
        await Runner.RunAsync<CreateDetailedTaskHandler, CreateTaskResult>(
            (handler, token) => handler.HandleAsync(
                new CreateDetailedTask(
                    new CreateTask(title, notes, priority, Placement: placement),
                    new EditTask(Guid.Empty)
                    {
                        TagIds = tags,
                        Deadline = deadline is null ? null : new Change<TaskDeadline?>(deadline),
                    }),
                token));

    public async Task<Guid> CreateTagAsync(string name, string color = "#3B82F6") =>
        await Runner.RunAsync<MyTaskApp.Application.Tags.CreateTagHandler, Guid>(
            (handler, token) => handler.HandleAsync(new MyTaskApp.Application.Tags.CreateTag(name, color), token));

    public Task<TaskListItem> DetailsAsync(Guid taskId) =>
        Runner.RunAsync<GetTaskDetailsHandler, TaskListItem>((handler, token) => handler.HandleAsync(new GetTaskDetails(taskId), token));

    /// <summary>Como se a conta estivesse ligada: este celular e mais um notebook.</summary>
    public (Guid Phone, Guid Notebook) Link()
    {
        var phone = Get<IDeviceIdentity>().DeviceId;
        var notebook = Guid.CreateVersion7();

        Get<SyncDeviceDirectory>().Update(
        [
            new DeviceInfo(phone, "Pixel de teste", "Android", "1.15.0", Now, Now, null, IsCurrent: true),
            new DeviceInfo(notebook, "Notebook", "Windows", "1.15.0", Now, Now, null, IsCurrent: false),
        ]);
        Get<DevicePlacementState>().Update(phone, []);

        return (phone, notebook);
    }

    public async ValueTask DisposeAsync()
    {
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();

        if (Directory.Exists(Folder))
        {
            Directory.Delete(Folder, recursive: true);
        }
    }

    public static string ServerUri => SyncSchema.MobileRedirectUri;
}
