using System.Net;
using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Processes;
using MyTaskApp.Infrastructure.Sync.Http;
using MyTaskApp.Infrastructure.Tests.Jira;
using MyTaskApp.Infrastructure.Tests.Persistence;
using MyTaskApp.Infrastructure.Tests.Processes;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Infrastructure.Tests.Sync;

/// <summary>
/// O desktop e o agente sobre o mesmo banco (ADR-085, T-08): um ciclo de
/// sincronização por vez no computador, e o refresh token girado por um
/// processo de cada vez, sem revogar a cadeia.
/// </summary>
public sealed class TwoProcessSyncTests : IAsyncLifetime
{
    private static readonly ProcessOwner Desktop = new(100, SyncDeviceHarness.Now.AddHours(-2));

    private static readonly ProcessOwner Agent = new(200, SyncDeviceHarness.Now.AddHours(-1));

    private readonly ScriptedSyncApi _desktopApi = new();

    private readonly ScriptedSyncApi _agentApi = new();

    private readonly FakeLiveness _liveness = new();

    private readonly Guid _user = Guid.CreateVersion7();

    private readonly Guid _workspace = Guid.CreateVersion7();

    private SyncDeviceHarness _desktop = null!;

    private SyncDeviceHarness _agent = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _desktop = await SyncDeviceHarness.CreateAsync(_desktopApi, Ct, process: Desktop, liveness: _liveness);
        _agent = await SyncDeviceHarness.CreateAsync(_agentApi, Ct, folder: _desktop.Folder, device: _desktop.Device, process: Agent, liveness: _liveness);
        await _desktop.LinkAsync(_user, _workspace, Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _agent.DisposeAsync();
        await _desktop.DisposeAsync();
        _desktop.DeleteFolder();
    }

    [Fact]
    public async Task WhileOneProcessSyncs_TheOtherDoesNotTouchTheServer()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _desktopApi.BeforeHello = () =>
        {
            entered.TrySetResult();
            return release.Task;
        };

        var desktopCycle = Task.Run(() => _desktop.Engine.RunCycleAsync(Ct), Ct);
        await entered.Task.WaitAsync(Ct);

        var agentResult = await _agent.Engine.RunCycleAsync(Ct);

        release.SetResult();
        (await desktopCycle).Status.Should().Be(SyncCycleStatus.Completed);
        agentResult.Status.Should().Be(SyncCycleStatus.Elsewhere);
        _agentApi.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task WhoSyncedKeepsSyncing_UntilItStopsRenewing()
    {
        (await _desktop.Engine.RunCycleAsync(Ct)).Status.Should().Be(SyncCycleStatus.Completed);
        (await _agent.Engine.RunCycleAsync(Ct)).Status.Should().Be(SyncCycleStatus.Elsewhere, "o lease fica com quem sincronizava");
        _agentApi.Calls.Should().BeEmpty();

        // O desktop parou de sincronizar (fechou a tela de outro jeito, ficou congelado): o lease vence.
        _agent.Clock.Advance(SyncEngine.LeaseDuration);

        (await _agent.Engine.RunCycleAsync(Ct)).Status.Should().Be(SyncCycleStatus.Completed);
        (await _desktop.Engine.RunCycleAsync(Ct)).Status.Should().Be(SyncCycleStatus.Elsewhere, "agora o dono é o agente");
    }

    [Fact]
    public async Task WhenTheOwnerDies_TheOtherSyncsRightAway()
    {
        (await _desktop.Engine.RunCycleAsync(Ct)).Status.Should().Be(SyncCycleStatus.Completed);

        _liveness.Dead.Add(Desktop);

        (await _agent.Engine.RunCycleAsync(Ct)).Status.Should().Be(SyncCycleStatus.Completed, "não espera os 2 min do lease de quem morreu");
    }

    [Fact]
    public async Task WithoutAnAccount_NoLeaseIsWritten()
    {
        var fresh = await SyncDeviceHarness.CreateAsync(new ScriptedSyncApi(), Ct, process: Desktop, liveness: _liveness);

        try
        {
            (await fresh.Engine.RunCycleAsync(Ct)).Status.Should().Be(SyncCycleStatus.Idle);
            (await fresh.ReadAsync(context => context.ProcessLeases.CountAsync(Ct)))
                .Should().Be(0, "quem não usa a sincronização não ganha escrita nova no banco");
        }
        finally
        {
            await fresh.DisposeAsync();
            fresh.DeleteFolder();
        }
    }

    [Fact]
    public async Task TwoProcessesRenewingTogether_NeverPresentAnOldRefreshToken()
    {
        await using var database = await new TempSqliteDatabase().MigrateAsync(Ct);
        var secrets = new InMemorySecretStore();
        var server = new RotatingTokenServer();
        var log = new CapturingLog();
        var options = new SyncOptions { ApiBaseUrl = "https://sync.exemplo.com" };
        secrets.Secrets[SyncOptions.SecretName] = "https://sync.exemplo.com/ refresh-0";

        SyncAuthenticationService Process(ProcessOwner owner) => new(
            options,
            new SyncHttp(new HttpClient(server.Handler), options, TimeProvider.System, log.For<SyncHttp>()),
            secrets,
            new FakeDeviceIdentity(),
            TimeProvider.System,
            log.For<SyncAuthenticationService>(),
            leases: new SqliteProcessLeases(database.ConnectionString, owner, _liveness, TimeProvider.System));

        using var desktop = Process(Desktop);
        using var agent = Process(Agent);

        var tokens = await Task.WhenAll(
            Task.Run(() => desktop.GetAccessTokenAsync(Ct), Ct),
            Task.Run(() => agent.GetAccessTokenAsync(Ct), Ct));

        tokens.Should().OnlyHaveUniqueItems("cada processo renovou com o refresh que o outro acabou de gravar");
        server.Reused.Should().BeFalse();
        secrets.Secrets[SyncOptions.SecretName].Should().Be("https://sync.exemplo.com/ refresh-2");
    }

    /// <summary>
    /// O servidor de verdade (ADR-061): cada refresh vale uma vez, e o antigo
    /// apresentado de novo revogaria a cadeia. Responde devagar, para as duas
    /// renovações se cruzarem se nada as separar.
    /// </summary>
    private sealed class RotatingTokenServer
    {
        private readonly Lock _gate = new();

        private int _current;

        public RotatingTokenServer() =>
            Handler = new FakeJiraServer().On(ApiRoutes.Token, request =>
            {
                var body = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
                Thread.Sleep(150);

                lock (_gate)
                {
                    if (!body.Contains($"refresh_token=refresh-{_current}", StringComparison.Ordinal))
                    {
                        Reused = true;
                        return FakeJiraServer.Json(HttpStatusCode.BadRequest, """{"error":"invalid_grant"}""");
                    }

                    var next = ++_current;
                    return FakeJiraServer.Json(HttpStatusCode.OK, $$"""{"access_token":"access-{{next}}","refresh_token":"refresh-{{next}}","expires_in":900}""");
                }
            });

        public FakeJiraServer Handler { get; }

        public bool Reused { get; private set; }
    }
}
