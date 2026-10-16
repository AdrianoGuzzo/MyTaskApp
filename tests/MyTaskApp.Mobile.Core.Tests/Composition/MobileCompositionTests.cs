using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Development;
using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Lifecycle;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tags;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Infrastructure.Sync.Http;
using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.ViewModels;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Mobile.Core.Tests.Composition;

/// <summary>
/// A composição do celular de verdade (ADR-065): cada caso de uso que as telas
/// pedem monta com o que <c>AddMobileCore</c> registra — sem Git, terminal,
/// Jira nem PostgreSQL.
/// </summary>
public sealed class MobileCompositionTests : IAsyncLifetime
{
    private MobileHarness _app = null!;

    public async ValueTask InitializeAsync() => _app = await MobileHarness.CreateAsync();

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    [Theory]
    [InlineData(typeof(SearchTasksHandler))]
    [InlineData(typeof(GetTaskDetailsHandler))]
    [InlineData(typeof(CreateDetailedTaskHandler))]
    [InlineData(typeof(EditTaskHandler))]
    [InlineData(typeof(PlaceTaskHandler))]
    [InlineData(typeof(CompleteOccurrenceHandler))]
    [InlineData(typeof(ReopenOccurrenceHandler))]
    [InlineData(typeof(CancelOccurrenceHandler))]
    [InlineData(typeof(ArchiveChecklistHandler))]
    [InlineData(typeof(RestoreChecklistHandler))]
    [InlineData(typeof(MoveChecklistToTrashHandler))]
    [InlineData(typeof(RestoreChecklistFromTrashHandler))]
    [InlineData(typeof(GetChecklistAuditHandler))]
    [InlineData(typeof(GetTagsHandler))]
    [InlineData(typeof(GetTaskDevelopmentsHandler))]
    [InlineData(typeof(GetTaskOriginHandler))]
    [InlineData(typeof(GetSyncStatusHandler))]
    [InlineData(typeof(ListSyncConflictsHandler))]
    [InlineData(typeof(ResolveSyncConflictHandler))]
    [InlineData(typeof(ListSyncFailuresHandler))]
    [InlineData(typeof(RetrySyncOperationHandler))]
    [InlineData(typeof(DiscardSyncOperationHandler))]
    [InlineData(typeof(ReconcileAfterRemoteChangeHandler))]
    [InlineData(typeof(SettleDevicePlacementsHandler))]
    [InlineData(typeof(RemoteRefetcher))]
    [InlineData(typeof(TaskDetailViewModel))]
    [InlineData(typeof(TaskFormViewModel))]
    [InlineData(typeof(ConnectViewModel))]
    public void EveryUseCaseThePhoneUses_Resolves(Type type)
    {
        using var scope = _app.Services.CreateScope();

        scope.ServiceProvider.GetRequiredService(type).Should().NotBeNull();
    }

    [Fact]
    public void TheSingletons_AreTheSameAcrossTheApp()
    {
        _app.Get<TaskListViewModel>().Should().BeSameAs(_app.Get<TaskListViewModel>());
        _app.Get<AccountViewModel>().Should().BeSameAs(_app.Get<AccountViewModel>());
        _app.Get<SyncScheduler>().Should().BeSameAs(_app.Get<SyncScheduler>());
    }

    [Fact]
    public void TheLogin_GoesThroughThePhoneClient_AndTheAppScheme()
    {
        var redirect = _app.Get<ISyncAuthorizationRedirect>();

        redirect.Should().BeSameAs(_app.Get<AppLinkAuthorizationRedirect>());
        redirect.ClientId.Should().Be(SyncSchema.MobileClientId);
        redirect.RedirectUri.Should().Be(new Uri(SyncSchema.MobileRedirectUri));
        _app.Get<ISyncAccount>().IsAvailable.Should().BeTrue();
    }

    [Fact]
    public void TheDevice_IsNamedAfterThePhone_AndTheAuditToo()
    {
        _app.Get<IDeviceIdentity>().DefaultNickname.Should().Be("Pixel de teste");
        _app.Get<ICurrentUser>().Name.Should().Be("Pixel de teste");
        File.Exists(Path.Combine(_app.Folder, "mytaskapp.db")).Should().BeTrue();
    }

    [Fact]
    public async Task WithoutAServer_TheAppIsLocalOnly()
    {
        await using var local = await MobileHarness.CreateAsync(serverUrl: null);

        local.Get<ISyncAccount>().IsAvailable.Should().BeFalse("o teste não grava servidor na build");
    }

    [Fact]
    public async Task TheLifecycle_StartsOnce_StopsInTheBackground_AndComesBack()
    {
        var lifecycle = _app.Get<MobileLifecycle>();
        var scheduler = _app.Get<SyncScheduler>();
        var cycles = 0;
        scheduler.CycleCompleted += _ => Interlocked.Increment(ref cycles);

        var start = lifecycle.StartAsync();
        start.Should().BeSameAs(lifecycle.StartAsync(), "abrir de novo não migra duas vezes");
        await start;
        await WaitUntilAsync(() => Volatile.Read(ref cycles) >= 1);

        await lifecycle.SleepAsync();
        lifecycle.Resume();
        await WaitUntilAsync(() => Volatile.Read(ref cycles) >= 2);

        await lifecycle.SleepAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline);
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
