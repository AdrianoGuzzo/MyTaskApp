using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Remote.Contracts;
using NSubstitute;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>O laço da fila (seção 4.2): a busca de 30 s, a campainha do hub e o "Aceitar controle".</summary>
public sealed class RemoteOperationSchedulerTests
{
    [Fact]
    public async Task WithoutTheHubAnOperationArrivesByThePollWithinThirtySeconds()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        kit.Scheduler.Start();
        await TicksAsync(kit, 1);

        var request = kit.Request();
        kit.Queue(request);
        kit.Time.Advance(RemoteLimits.PollInterval);

        Assert.Equal(RemoteOperationStatus.Succeeded, await kit.WaitFinishedAsync(request.OperationId));
        Assert.True(RemoteLimits.PollInterval <= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task TheHubRingingFetchesRightAwayWithoutWaitingForThePoll()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        kit.Scheduler.Start();
        await TicksAsync(kit, 1);
        Assert.True(kit.Hub.IsConnected);

        var request = kit.Request();
        kit.Queue(request);
        kit.Hub.Ring(request.OperationId);

        Assert.Equal(RemoteOperationStatus.Succeeded, await kit.WaitFinishedAsync(request.OperationId));
    }

    [Fact]
    public async Task WhileThisMachineDoesNotAcceptControlNothingIsFetchedAndTheHubStaysOff()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        kit.Settings.Settings = new RemoteSettings(true, false);
        var request = kit.Request();
        kit.Queue(request);

        kit.Scheduler.Start();
        await TicksAsync(kit, 1);

        Assert.False(kit.Hub.IsConnected);
        Assert.Equal(RemoteOperationStatus.Queued, kit.Server.StatusOf(request.OperationId));

        // Ligou: a próxima volta conecta e busca.
        kit.Settings.Settings = new RemoteSettings(true, true);
        kit.Time.Advance(RemoteLimits.PollInterval);

        Assert.Equal(RemoteOperationStatus.Succeeded, await kit.WaitFinishedAsync(request.OperationId));
        Assert.True(kit.Hub.IsConnected);

        // Desligou de novo: a campainha cai.
        kit.Settings.Settings = new RemoteSettings(true, false);
        await kit.Scheduler.TickAsync();
        Assert.False(kit.Hub.IsConnected);
    }

    [Fact]
    public async Task TheFirstTurnRecoversWhatWasRunningWhenTheAgentFell()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var crashed = kit.Request();
        kit.Queue(crashed);
        _ = await kit.Server.ListAgentOperationsAsync(TestContext.Current.CancellationToken);
        await kit.Server.UpdateStatusAsync(crashed.OperationId, new OperationStatusUpdate(RemoteOperationStatus.Accepted), TestContext.Current.CancellationToken);
        await kit.Server.UpdateStatusAsync(crashed.OperationId, new OperationStatusUpdate(RemoteOperationStatus.Running), TestContext.Current.CancellationToken);
        var now = kit.Time.GetUtcNow();
        kit.Journal.Seed(new RemoteJournalEntry(
            crashed.OperationId, kit.Phone.DeviceId, "Pixel 8", crashed.Kind, null, kit.Phone.Sign(crashed).Jws,
            RemoteOperationStatus.Running, RemoteOperationStatus.Running, null, null, null, 1, 0, now, now));

        kit.Scheduler.Start();
        await TicksAsync(kit, 1);

        Assert.Equal(RemoteOperationStatus.Interrupted, kit.Server.StatusOf(crashed.OperationId));
        Assert.Equal(0, kit.Executor.Runs);
    }

    [Fact]
    public async Task ATurnThatFailsDoesNotStopTheLoop()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        kit.Server.Offline = true;
        kit.Scheduler.Start();
        await TicksAsync(kit, 1);

        kit.Server.Offline = false;
        var request = kit.Request();
        kit.Queue(request);
        kit.Time.Advance(RemoteLimits.PollInterval);

        Assert.Equal(RemoteOperationStatus.Succeeded, await kit.WaitFinishedAsync(request.OperationId));
    }

    /// <summary>A lista de tipos do registro é a dos executores: um tipo novo sem a lista (ou o contrário) quebra aqui.</summary>
    [Fact]
    public void TheSupportedKindsAreTheKindsOfTheRegisteredExecutors()
    {
        var services = new ServiceCollection();
        services.AddApplication(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

        var executors = services.Where(service => service.ServiceType == typeof(IRemoteOperationExecutor)).Select(service => service.ImplementationType!).ToList();
        var supported = (SupportedRemoteOperations)services.Single(service => service.ServiceType == typeof(SupportedRemoteOperations)).ImplementationInstance!;

        Assert.Equal(
            supported.Kinds.Order(StringComparer.Ordinal),
            executors.Select(KindOf).Order(StringComparer.Ordinal));
        Assert.All(supported.Kinds, kind => Assert.True(RemoteOperationKinds.IsKnown(kind)));
    }

    [Fact]
    public async Task TheInspectionAnswersWithTheToolsAndThePublishedCatalog()
    {
        var catalogQuery = new FakeCatalogQuery();
        catalogQuery.Commands.Add(new CatalogCommand(Guid.CreateVersion7(), "testes", CommandScope.Global, null, false, []));
        var queue = new FakeOperationQueue();
        var publisher = new RemoteCatalogPublisher(
            new Abstractions.ScopedUseCaseRunner(new ServiceCollection().AddSingleton<IRemoteCatalogQuery>(catalogQuery).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>()),
            queue,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RemoteCatalogPublisher>.Instance);
        var capabilities = new MachineCapabilities(
            new Fakes.FakeGitClient(),
            new MyTaskApp.Application.Agents.AgentCliProviders([new Fakes.FakeAgentCliProvider()]),
            new Fakes.FakeToolLocator(),
            new SupportedRemoteOperations([RemoteOperationKinds.EnvironmentInspect]),
            RemoteDispatchLimits.Default,
            TimeProvider.System);
        var executor = new EnvironmentInspectExecutor(capabilities, publisher);
        var events = new List<RemoteOperationEvent>();
        var request = new RemoteOperationRequest(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(),
            RemoteOperationKinds.EnvironmentInspect, JsonSerializer.SerializeToElement(new EnvironmentInspectionRequest(), RemoteJson.Options),
            null, [], DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(5));
        var execution = new RemoteExecution(request, "Pixel 8", (item, _) => { events.Add(item); return Task.CompletedTask; }, TimeProvider.System);

        var result = await executor.ExecuteAsync(execution, TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        var inspection = result.Data!.Value.Deserialize<EnvironmentInspection>(RemoteJson.Options)!;
        var published = Assert.Single(queue.Catalogs);
        Assert.Equal(published.Hash, inspection.CatalogHash);
        Assert.Equal(published.Hash, inspection.Capabilities.CatalogHash);
        Assert.Equal(1, inspection.Commands);
        Assert.Equal([RemoteOperationKinds.EnvironmentInspect], inspection.Capabilities.Operations);
        Assert.Equal(["ferramentas", "catálogo"], events.Select(item => item.Step));
        Assert.EndsWith("1 comando liberado.", result.Summary, StringComparison.Ordinal);
    }

    private static string KindOf(Type executor) => executor.Name switch
    {
        nameof(EnvironmentInspectExecutor) => RemoteOperationKinds.EnvironmentInspect,
        nameof(CommandRunExecutor) => RemoteOperationKinds.CommandRun,
        nameof(WorktreeProvisionExecutor) => RemoteOperationKinds.WorktreeProvision,
        nameof(WorktreeRemoveExecutor) => RemoteOperationKinds.WorktreeRemove,
        nameof(AgentStartExecutor) => RemoteOperationKinds.AgentStart,
        nameof(AgentPromptExecutor) => RemoteOperationKinds.AgentPrompt,
        nameof(AgentStopExecutor) => RemoteOperationKinds.AgentStop,
        nameof(DatabaseCopyExecutor) => RemoteOperationKinds.DatabaseCopy,
        _ => throw new InvalidOperationException($"Diga aqui o tipo de {executor.Name}."),
    };

    /// <summary>4.6: revogado na conta, o agente para de aceitar e encerra o que roda, com o aviso na sincronização.</summary>
    [Fact]
    public async Task ARevokedDevice_StopsWhatRuns_AndStopsAsking()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Executor.Behavior = async (_, token) =>
        {
            started.TrySetResult();
            await using (token.Register(() => stopped.TrySetResult()))
            {
                await Task.Delay(Timeout.Infinite, token);
            }

            return RemoteOperationResult.Success("não devia terminar");
        };
        var request = kit.Request();
        kit.Queue(request);
        kit.Scheduler.Start();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

        kit.Server.Revoked = true;
        await kit.Scheduler.TickAsync();

        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.False(kit.Hub.IsConnected);
        await kit.Sync.Received(1).SetStateAsync(SyncLinkState.DeviceRevoked, Arg.Any<CancellationToken>());

        // Revogado, o laço não pergunta mais nada ao servidor.
        kit.Sync.ReadAsync(Arg.Any<CancellationToken>()).Returns(SyncLocalState.Unlinked with { State = SyncLinkState.DeviceRevoked });
        kit.Server.Revoked = false;
        var next = kit.Request();
        kit.Queue(next);
        await kit.Scheduler.TickAsync();

        Assert.Equal(RemoteOperationStatus.Queued, kit.Server.StatusOf(next.OperationId));
    }

    /// <summary>4.6: o banco copiado de outro computador traz a conta dele; o agente não liga, e diz por quê.</summary>
    [Fact]
    public async Task ADatabaseCopiedFromAnotherComputer_DoesNotTakeOperations()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        kit.Sync.ReadAsync(Arg.Any<CancellationToken>()).Returns(SyncLocalState.Unlinked with
        {
            State = SyncLinkState.Linked,
            IsEnabled = true,
            WorkspaceId = kit.LinkedWorkspace,
            LinkedDeviceId = Guid.CreateVersion7(),
        });
        var request = kit.Request();
        kit.Queue(request);

        kit.Scheduler.Start();
        await TicksAsync(kit, 1);
        await kit.Scheduler.TickAsync();

        Assert.False(kit.Hub.IsConnected);
        Assert.Equal(RemoteOperationStatus.Queued, kit.Server.StatusOf(request.OperationId));
        Assert.Equal(0, kit.Executor.Runs);
        await kit.Sync.Received().RecordErrorAsync(RemoteOperationScheduler.DeviceChangedCode, Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static async Task TicksAsync(RemoteDispatchKit kit, int count)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);

        while (kit.Scheduler.Ticks < count)
        {
            Assert.True(DateTime.UtcNow < deadline, "O laço não deu a volta.");
            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
