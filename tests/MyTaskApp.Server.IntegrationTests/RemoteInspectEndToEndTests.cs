using System.Buffers.Text;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Remote;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// A E2 de ponta a ponta (fase 2, T-15): um celular pede <c>environment.inspect</c>
/// assinado ao servidor de verdade, e o notebook — só os casos de uso, a fila
/// e o SQLite, sem tela nenhuma, como o agente — confere, roda e conta.
/// </summary>
public sealed class RemoteInspectEndToEndTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string WithRemote = ApiFactory.SyncScopes + " " + RemoteSchema.Scope;

    private readonly List<SyncedDesktop> _desktops = [];

    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DeviceCapabilities PhoneCapabilities => new(
        RemoteLimits.ProtocolVersion, [], "android",
        ToolStatus.Missing, ToolStatus.Missing, ToolStatus.Missing, ToolStatus.Missing, 1, string.Empty);

    public async ValueTask InitializeAsync() => _api = new ApiFactory(await postgres.CreateDatabaseAsync(Ct));

    public async ValueTask DisposeAsync()
    {
        foreach (var desktop in _desktops)
        {
            await desktop.DisposeAsync();
            desktop.DeleteFolder();
        }

        await _api.DisposeAsync();
    }

    [Fact]
    public async Task APairedPhoneAsksAndTheMachineWithoutAScreenAnswersWithItsTools()
    {
        var (notebook, phone, phoneKey) = await PairedAsync();
        using var _ = phoneKey;
        var request = await RequestAsync(notebook, phone, phoneKey);

        await notebook.Services.GetRequiredService<RemoteOperationDispatcher>().PollAsync(Ct);
        var operation = await FinishedAsync(phone, request.OperationId);

        operation.Status.Should().Be(RemoteOperationStatus.Succeeded, operation.ResultSummary);
        operation.ResultSummary.Should().Contain("comando");

        var events = await SendAsync<RemoteEventPage>(phone, HttpMethod.Get, $"{RemoteRoutes.OperationEvents(request.OperationId)}?after=0", null);
        events.Events.Select(item => item.Seq).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        events.LastContiguousSeq.Should().Be(events.Events[^1].Seq);
        var result = events.Events.Should().ContainSingle(item => item.Kind == RemoteEventKind.Result).Subject;
        var inspection = result.Data!.Value.Deserialize<EnvironmentInspection>(RemoteJson.Options)!;
        inspection.Capabilities.Operations.Should().Contain(RemoteOperationKinds.EnvironmentInspect);

        // O catálogo foi publicado antes da resposta, com o mesmo hash.
        var catalog = await SendAsync<AgentCatalog>(phone, HttpMethod.Get, RemoteRoutes.Catalog(notebook.Device.DeviceId), null);
        catalog.Hash.Should().Be(inspection.CatalogHash);

        // A mesma operação de novo na fila não roda outra vez.
        await notebook.Services.GetRequiredService<RemoteOperationDispatcher>().PollAsync(Ct);
        (await SendAsync<RemoteEventPage>(phone, HttpMethod.Get, $"{RemoteRoutes.OperationEvents(request.OperationId)}?after=0", null))
            .Events.Should().HaveCount(events.Events.Count);
    }

    /// <summary>
    /// O servidor aceita todo tipo do contrato; quem diz o que roda é o agente
    /// (4.4, item 5). Um pedido bem assinado de um tipo que esta versão ainda
    /// não roda volta recusado, com o motivo, e nada acontece na máquina.
    /// </summary>
    [Fact]
    public async Task AKindThisAgentDoesNotRunIsRejectedWithTheReason()
    {
        var (notebook, phone, phoneKey) = await PairedAsync();
        using var _ = phoneKey;
        var request = await RequestAsync(
            notebook,
            phone,
            phoneKey,
            RemoteOperationKinds.AgentStop,
            JsonSerializer.SerializeToElement(new AgentStopRequest(Guid.CreateVersion7()), RemoteJson.Options));

        await notebook.Services.GetRequiredService<RemoteOperationDispatcher>().PollAsync(Ct);
        var operation = await FinishedAsync(phone, request.OperationId);

        operation.Status.Should().Be(RemoteOperationStatus.Rejected);
        operation.ResultCode.Should().Be(RemoteErrorCodes.UnknownKind);
    }

    [Fact]
    public async Task TheHubRingsTheMachineWhenAnOperationIsQueuedForIt()
    {
        var (notebook, phone, phoneKey) = await PairedAsync();
        using var _ = phoneKey;
        var rang = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);

        // O servidor em memória não fala WebSocket: o mesmo cliente, por long polling, como atrás de um proxy que barra.
        await using var hub = new SignalRRemoteHub(
            notebook.Services.GetRequiredService<ISyncAuthenticator>(),
            NullLogger<SignalRRemoteHub>.Instance,
            new RemoteHubTransport(() => _api.Server.CreateHandler(), HttpTransportType.LongPolling));
        hub.OperationAvailable += id => rang.TrySetResult(id);
        await hub.ConnectAsync(Ct);
        hub.IsConnected.Should().BeTrue();

        var request = await RequestAsync(notebook, phone, phoneKey);

        (await rang.Task.WaitAsync(TimeSpan.FromSeconds(30), Ct)).Should().Be(request.OperationId);
    }

    /// <summary>O notebook aceita ser controlado e confia no celular, pelo código, como no pareamento de verdade.</summary>
    private async Task<(SyncedDesktop Notebook, DesktopSession Phone, ECDsa PhoneKey)> PairedAsync()
    {
        var notebook = await SyncedDesktop.CreateAsync(_api, "Notebook", Ct);
        _desktops.Add(notebook);

        await notebook.UseCaseAsync<UpdateRemoteSettingsHandler, RemoteSettings>(
            (handler, cancel) => handler.HandleAsync(new UpdateRemoteSettings(Enabled: true, AcceptControl: true), cancel));
        await notebook.ConnectAsync("ana", Ct);
        (await notebook.UseCaseAsync<RegisterThisMachineHandler, RemoteAvailability>(
                (handler, cancel) => handler.HandleAsync(new RegisterThisMachine(), cancel)))
            .Should().Be(RemoteAvailability.Ready);

        var phoneKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var phone = await _api.LoginAsync("ana", deviceName: "Celular", scope: WithRemote);
        await SendAsync<RemoteDeviceInfo>(phone, HttpMethod.Put, RemoteRoutes.CurrentDevice,
            new DeviceRegistration(phone.DeviceId, DeviceRoles.Controller, DevicePublicKey.Es256Of(phoneKey.ExportSubjectPublicKeyInfo()), PhoneCapabilities));

        var pairing = await SendAsync<PairingInfo>(phone, HttpMethod.Post, RemoteRoutes.Pairings, new PairingRequest(notebook.Device.DeviceId));
        var notebookFingerprint = (await SendAsync<List<RemoteDeviceInfo>>(phone, HttpMethod.Get, RemoteRoutes.Devices, null))
            .Single(device => device.DeviceId == notebook.Device.DeviceId).KeyFingerprint!;
        var code = PairingCode.Derive(pairing.ControllerKey.Fingerprint, notebookFingerprint, pairing.Nonce);
        await notebook.UseCaseAsync<ConfirmPairingHandler>((handler, cancel) => handler.HandleAsync(new ConfirmPairing(pairing.PairingId, code), cancel));

        return (notebook, phone, phoneKey);
    }

    /// <summary>O celular monta, assina e põe na fila um <c>environment.inspect</c> para o notebook.</summary>
    private static async Task<RemoteOperationRequest> RequestAsync(
        SyncedDesktop notebook,
        DesktopSession phone,
        ECDsa key,
        string kind = RemoteOperationKinds.EnvironmentInspect,
        JsonElement? payload = null)
    {
        var workspace = (await notebook.RunAsync(services => services.GetRequiredService<ISyncLocalStore>().ReadAsync(Ct))).WorkspaceId!.Value;
        var now = DateTimeOffset.UtcNow;
        var request = new RemoteOperationRequest(
            Guid.CreateVersion7(),
            workspace,
            notebook.Device.DeviceId,
            phone.DeviceId,
            kind,
            payload ?? JsonSerializer.SerializeToElement(new EnvironmentInspectionRequest(), RemoteJson.Options),
            null,
            [],
            now,
            now + RemoteLimits.DefaultAcceptanceWindow);

        var fingerprint = DevicePublicKey.FingerprintOf(key.ExportSubjectPublicKeyInfo());
        var input = RemoteJws.SigningInput(request, fingerprint, RemoteJws.OperationType);
        var signature = key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        var created = await SendAsync<RemoteOperationView>(phone, HttpMethod.Post, RemoteRoutes.Operations,
            RemoteJws.Compose(input, Base64Url.EncodeToString(signature)));

        created.Status.Should().Be(RemoteOperationStatus.Queued);
        return request;
    }

    private static async Task<RemoteOperationView> FinishedAsync(DesktopSession phone, Guid operationId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);

        while (true)
        {
            var operation = (await SendAsync<RemoteOperationView>(phone, HttpMethod.Get, RemoteRoutes.Operation(operationId), null))!;

            if (RemoteOperationLifecycle.IsFinal(operation.Status) || DateTime.UtcNow > deadline)
            {
                return operation;
            }

            await Task.Delay(100, Ct);
        }
    }

    private static async Task<T> SendAsync<T>(DesktopSession session, HttpMethod method, string path, object? body, bool allowFailure = false)
    {
        using var client = session.Api();
        using var request = new HttpRequestMessage(method, path)
        {
            Content = body is null ? null : new StringContent(JsonSerializer.Serialize(body, body.GetType(), RemoteJson.Options), Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request, Ct);

        if (!response.IsSuccessStatusCode)
        {
            allowFailure.Should().BeTrue(await response.Content.ReadAsStringAsync(Ct));
            return default!;
        }

        return (await response.Content.ReadFromJsonAsync<T>(RemoteJson.Options, Ct))!;
    }
}
