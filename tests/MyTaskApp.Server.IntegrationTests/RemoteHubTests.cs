using System.Buffers.Text;
using System.Net;
using System.Security.Cryptography;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Sync.Contracts;
using static MyTaskApp.Server.IntegrationTests.RemoteTestKit;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// O hub da orquestração (fase 2, T-13; ADR-086), com o cliente SignalR de
/// verdade: a máquina alvo ouve o que é dela, a conta ouve as mudanças, outra
/// conta não ouve nada, e a máquina revogada cai e não volta.
/// </summary>
public sealed class RemoteHubTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ApiFactory _api = null!;

    private RemoteTestKit _kit = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _api = new ApiFactory(await postgres.CreateDatabaseAsync(Ct));
        _kit = new RemoteTestKit(_api);
    }

    public async ValueTask DisposeAsync()
    {
        _kit.Dispose();
        await _api.DisposeAsync();
    }

    [Fact]
    public async Task TheTargetHearsItsOperation_AndTheAccountHearsEveryChange()
    {
        var (phone, notebook, workspace) = await PairAsync();
        var stranger = await _kit.DeviceAsync("PC da Bia", DeviceRoles.Executor, "bia");

        await using var agent = await _kit.ListenAsync(notebook.Session);
        await using var controller = await _kit.ListenAsync(phone.Session, tokenInQuery: true);
        await using var outsider = await _kit.ListenAsync(stranger.Session);

        var id = (await CreateAsync(phone, Inspection(workspace, phone, notebook))).OperationId;

        await agent.ExpectAsync(RemoteHubMessages.OperationAvailable, id);
        await controller.ExpectAsync(RemoteHubMessages.OperationChanged, id, RemoteOperationStatus.Queued);

        await GetAsync<List<AgentOperation>>(notebook.Session, RemoteRoutes.AgentOperations);
        await controller.ExpectAsync(RemoteHubMessages.OperationChanged, id, RemoteOperationStatus.Dispatched);

        await MoveAsync(notebook, id, RemoteOperationStatus.Accepted);
        await MoveAsync(notebook, id, RemoteOperationStatus.Running);
        await controller.ExpectAsync(RemoteHubMessages.OperationChanged, id, RemoteOperationStatus.Running);

        await SendAsync<EventBatchAck>(notebook.Session, HttpMethod.Post, RemoteRoutes.OperationEvents(id),
            new EventBatch(id, [new RemoteOperationEvent(id, 1, DateTimeOffset.UtcNow, RemoteEventKind.StepStarted, "inspect")]));
        await controller.ExpectAsync(RemoteHubMessages.EventsAppended, id, 1L);

        await SendAsync<RemoteOperationView>(phone.Session, HttpMethod.Post, RemoteRoutes.CancelOperation(id), null);
        await agent.ExpectAsync(RemoteHubMessages.CancelRequested, id);

        // A conta inteira ouve: o próprio notebook também recebe as mudanças.
        await agent.ExpectAsync(RemoteHubMessages.OperationChanged, id, RemoteOperationStatus.Running);
        controller.Received.Should().NotContain(item => item.Message == RemoteHubMessages.OperationAvailable, "é aviso só para a máquina alvo");
        outsider.Received.Should().BeEmpty();
    }

    [Fact]
    public async Task ThePairing_RingsTheExecutor_AndTheApproval_RingsTheAccount()
    {
        var phone = await _kit.DeviceAsync("Celular", DeviceRoles.Controller);
        var notebook = await _kit.DeviceAsync("Notebook", DeviceRoles.Executor);

        await using var executor = await _kit.ListenAsync(notebook.Session);
        await using var controller = await _kit.ListenAsync(phone.Session);

        var pairing = await SendAsync<PairingInfo>(phone.Session, HttpMethod.Post, RemoteRoutes.Pairings, new PairingRequest(notebook.DeviceId));
        await executor.ExpectAsync(RemoteHubMessages.PairingRequested, pairing.PairingId);

        var signature = Base64Url.EncodeToString(notebook.Key.SignData(
            PairingCode.ApprovalPayload(pairing.PairingId, pairing.ControllerKey.Fingerprint, pairing.ExecutorKeyFingerprint, pairing.Nonce),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        await SendAsync<PairingInfo>(notebook.Session, HttpMethod.Post, RemoteRoutes.ApprovePairing(pairing.PairingId), new PairingApproval(signature));

        await controller.ExpectAsync(RemoteHubMessages.PairingApproved, pairing.PairingId);
        controller.Received.Should().NotContain(item => item.Message == RemoteHubMessages.PairingRequested);
    }

    [Fact]
    public async Task RevokingTheMachine_DropsItsConnection_AndItCannotComeBack()
    {
        var (phone, notebook, _) = await PairAsync();
        await using var agent = await _kit.ListenAsync(notebook.Session);
        await using var controller = await _kit.ListenAsync(phone.Session);

        await phone.Session.SendAsync<object, DeviceInfo>(HttpMethod.Post, ApiRoutes.RevokeDevice(notebook.DeviceId), new { });

        await agent.Closed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
        controller.Closed.IsCompleted.Should().BeFalse("só a revogada cai");

        var again = () => _kit.ListenAsync(notebook.Session, tokenInQuery: true);
        (await again.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WithoutTheRemoteScopeOrAToken_TheHubRefuses()
    {
        var syncOnly = await _api.LoginAsync("ana", deviceName: "Só sync");
        var noToken = new DesktopSession(_api, Guid.CreateVersion7(), new TokenSet(string.Empty, string.Empty, 0));

        var withoutScope = () => _kit.ListenAsync(syncOnly, tokenInQuery: true);
        (await withoutScope.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var anonymous = () => _kit.ListenAsync(noToken, tokenInQuery: true);
        (await anonymous.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>O token na query só vale no hub, e não vai para o log.</summary>
    [Fact]
    public async Task ATokenInTheQuery_OnlyAuthenticatesTheHub_AndIsNeverLogged()
    {
        var (phone, _, _) = await PairAsync();
        var token = phone.Session.Tokens.AccessToken;

        await using (await _kit.ListenAsync(phone.Session, tokenInQuery: true))
        {
        }

        using var client = _api.CreateClient();
        using var response = await client.GetAsync($"{RemoteRoutes.Devices}?access_token={Uri.EscapeDataString(token)}", Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        _api.Logs.Text.Should().NotContain(token);
    }

    private async Task<(RemoteDevice Phone, RemoteDevice Notebook, Guid Workspace)> PairAsync()
    {
        var phone = await _kit.DeviceAsync("Celular", DeviceRoles.Controller);
        var notebook = await _kit.DeviceAsync("Notebook", DeviceRoles.Executor);

        return (phone, notebook, await phone.Session.WorkspaceAsync());
    }
}
