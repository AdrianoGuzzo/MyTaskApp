using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Infrastructure.Persistence;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// O pareamento pelo servidor (fase 2, T-05; RS-08), de ponta a ponta: o
/// celular pede, o notebook vê o pedido, os dois chegam ao mesmo código sem
/// que o servidor o diga, e o notebook aprova assinando com a chave dele.
/// </summary>
public sealed class RemotePairingTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string WithRemote = ApiFactory.SyncScopes + " " + RemoteSchema.Scope;

    private readonly List<ECDsa> _keys = [];

    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DeviceCapabilities Capabilities => new(
        RemoteLimits.ProtocolVersion, [RemoteOperationKinds.EnvironmentInspect], "windows",
        ToolStatus.Missing, ToolStatus.Missing, ToolStatus.Missing, ToolStatus.Missing, 1, string.Empty);

    public async ValueTask InitializeAsync() => _api = new ApiFactory(await postgres.CreateDatabaseAsync(Ct));

    public async ValueTask DisposeAsync()
    {
        _keys.ForEach(key => key.Dispose());
        await _api.DisposeAsync();
    }

    [Fact]
    public async Task ThePhoneAsks_BothSidesComputeTheSameCode_AndTheNotebookApprovesBySigning()
    {
        var (phone, _) = await DeviceAsync("Celular", DeviceRoles.Controller);
        var (notebook, notebookKey) = await DeviceAsync("Notebook", DeviceRoles.Executor);

        var asked = await SendAsync<PairingInfo>(phone, HttpMethod.Post, RemoteRoutes.Pairings, new PairingRequest(notebook.DeviceId));
        asked.Status.Should().Be(PairingStatus.Pending);

        // O notebook vê o pedido, com a chave do celular e o nonce.
        var seen = (await GetAsync<List<PairingInfo>>(notebook, RemoteRoutes.Pairings)).Should().ContainSingle().Subject;
        seen.PairingId.Should().Be(asked.PairingId);
        seen.ControllerNickname.Should().Be("Celular");

        // Cada lado calcula o código com o que tem; o servidor nunca o manda.
        var executorFingerprint = (await GetAsync<List<RemoteDeviceInfo>>(phone, RemoteRoutes.Devices))
            .Single(device => device.DeviceId == notebook.DeviceId).KeyFingerprint!;
        var onThePhone = PairingCode.Derive(asked.ControllerKey.Fingerprint, executorFingerprint, asked.Nonce);
        var onTheNotebook = PairingCode.Derive(seen.ControllerKey.Fingerprint, Fingerprint(notebookKey), seen.Nonce);
        onThePhone.Should().Be(onTheNotebook).And.MatchRegex("^[0-9]{6}$");

        var approved = await SendAsync<PairingInfo>(notebook, HttpMethod.Post, RemoteRoutes.ApprovePairing(asked.PairingId), Approval(notebookKey, seen));

        approved.Status.Should().Be(PairingStatus.Approved);
        (await GetAsync<PairingInfo>(phone, RemoteRoutes.Pairing(asked.PairingId))).Status.Should().Be(PairingStatus.Approved);
        (await GetAsync<List<PairingInfo>>(notebook, RemoteRoutes.Pairings)).Should().BeEmpty();

        // Aprovar de novo devolve o mesmo pareamento.
        (await SendAsync<PairingInfo>(notebook, HttpMethod.Post, RemoteRoutes.ApprovePairing(asked.PairingId), Approval(notebookKey, seen)))
            .Status.Should().Be(PairingStatus.Approved);
    }

    [Fact]
    public async Task AMachineThatDoesNotAcceptControl_CannotBeAskedFor()
    {
        var (phone, _) = await DeviceAsync("Celular", DeviceRoles.Controller);
        var (other, _) = await DeviceAsync("Outro celular", DeviceRoles.Controller);

        (await ErrorAsync(phone, HttpMethod.Post, RemoteRoutes.Pairings, new PairingRequest(other.DeviceId)))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.NotAnExecutor));
    }

    [Fact]
    public async Task AnUnregisteredController_CannotAsk()
    {
        var phone = await _api.LoginAsync("ana", deviceName: "Celular", scope: WithRemote);
        var (notebook, _) = await DeviceAsync("Notebook", DeviceRoles.Executor);

        (await ErrorAsync(phone, HttpMethod.Post, RemoteRoutes.Pairings, new PairingRequest(notebook.DeviceId)))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.NotRegistered));
    }

    [Fact]
    public async Task AMachine_DoesNotPairWithItself()
    {
        var (notebook, _) = await DeviceAsync("Notebook", DeviceRoles.Controller | DeviceRoles.Executor);

        (await ErrorAsync(notebook, HttpMethod.Post, RemoteRoutes.Pairings, new PairingRequest(notebook.DeviceId))).Status
            .Should().Be(HttpStatusCode.BadRequest);
    }

    /// <summary>Uma assinatura de outra chave, ou de outro pareamento, não aprova nada.</summary>
    [Fact]
    public async Task ASignatureThatIsNotTheExecutors_IsRefused()
    {
        var (phone, phoneKey) = await DeviceAsync("Celular", DeviceRoles.Controller);
        var (notebook, notebookKey) = await DeviceAsync("Notebook", DeviceRoles.Executor);
        var pairing = await SendAsync<PairingInfo>(phone, HttpMethod.Post, RemoteRoutes.Pairings, new PairingRequest(notebook.DeviceId));

        (await ErrorAsync(notebook, HttpMethod.Post, RemoteRoutes.ApprovePairing(pairing.PairingId), Approval(phoneKey, pairing)))
            .Should().Be((HttpStatusCode.Forbidden, RemoteErrorCodes.UntrustedSignature));
        (await ErrorAsync(notebook, HttpMethod.Post, RemoteRoutes.ApprovePairing(pairing.PairingId), Approval(notebookKey, pairing with { PairingId = Guid.CreateVersion7() })))
            .Should().Be((HttpStatusCode.Forbidden, RemoteErrorCodes.UntrustedSignature));
        (await ErrorAsync(notebook, HttpMethod.Post, RemoteRoutes.ApprovePairing(pairing.PairingId), new PairingApproval("não é assinatura")))
            .Should().Be((HttpStatusCode.Forbidden, RemoteErrorCodes.UntrustedSignature));

        (await GetAsync<PairingInfo>(phone, RemoteRoutes.Pairing(pairing.PairingId))).Status.Should().Be(PairingStatus.Pending);
    }

    /// <summary>Só o executor aprova; outra conta nem fica sabendo que o pedido existe.</summary>
    [Fact]
    public async Task OnlyTheExecutorApproves_AndAnotherAccountSeesNothing()
    {
        var (phone, phoneKey) = await DeviceAsync("Celular", DeviceRoles.Controller);
        var (notebook, _) = await DeviceAsync("Notebook", DeviceRoles.Executor);
        var stranger = await _api.LoginAsync("bia", scope: WithRemote);
        var pairing = await SendAsync<PairingInfo>(phone, HttpMethod.Post, RemoteRoutes.Pairings, new PairingRequest(notebook.DeviceId));

        (await ErrorAsync(phone, HttpMethod.Post, RemoteRoutes.ApprovePairing(pairing.PairingId), Approval(phoneKey, pairing))).Status
            .Should().Be(HttpStatusCode.NotFound);
        (await ErrorAsync(stranger, HttpMethod.Get, RemoteRoutes.Pairing(pairing.PairingId), null)).Status
            .Should().Be(HttpStatusCode.NotFound);
        (await ErrorAsync(stranger, HttpMethod.Post, RemoteRoutes.Pairings, new PairingRequest(notebook.DeviceId))).Status
            .Should().Be(HttpStatusCode.Conflict, "bia ainda não registrou chave; e a máquina nem é dela");
    }

    [Fact]
    public async Task AnExpiredRequest_CannotBeApproved()
    {
        var (phone, _) = await DeviceAsync("Celular", DeviceRoles.Controller);
        var (notebook, notebookKey) = await DeviceAsync("Notebook", DeviceRoles.Executor);
        var pairing = await SendAsync<PairingInfo>(phone, HttpMethod.Post, RemoteRoutes.Pairings, new PairingRequest(notebook.DeviceId));

        await using (var scope = _api.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<ServerDbContext>().RemotePairings
                .Where(row => row.PairingId == pairing.PairingId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct);
        }

        (await ErrorAsync(notebook, HttpMethod.Post, RemoteRoutes.ApprovePairing(pairing.PairingId), Approval(notebookKey, pairing)))
            .Should().Be((HttpStatusCode.Gone, RemoteErrorCodes.PairingExpired));
        (await GetAsync<PairingInfo>(phone, RemoteRoutes.Pairing(pairing.PairingId))).Status.Should().Be(PairingStatus.Expired);
        (await GetAsync<List<PairingInfo>>(notebook, RemoteRoutes.Pairings)).Should().BeEmpty();
    }

    /// <summary>O executor trocou de chave depois do pedido: o código mostrado no celular já não seria o dele.</summary>
    [Fact]
    public async Task AKeyThatChangedAfterTheRequest_StopsTheApproval()
    {
        var (phone, _) = await DeviceAsync("Celular", DeviceRoles.Controller);
        var (notebook, notebookKey) = await DeviceAsync("Notebook", DeviceRoles.Executor);
        var pairing = await SendAsync<PairingInfo>(phone, HttpMethod.Post, RemoteRoutes.Pairings, new PairingRequest(notebook.DeviceId));

        await RegisterAsync(notebook, NewKey(), DeviceRoles.Executor);

        (await ErrorAsync(notebook, HttpMethod.Post, RemoteRoutes.ApprovePairing(pairing.PairingId), Approval(notebookKey, pairing)))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.KeyChanged));
    }

    [Fact]
    public async Task OpenRequests_HaveACeiling()
    {
        var (phone, _) = await DeviceAsync("Celular", DeviceRoles.Controller);
        var (notebook, _) = await DeviceAsync("Notebook", DeviceRoles.Executor);

        for (var index = 0; index < RemoteLimits.MaxOpenPairings; index++)
        {
            await SendAsync<PairingInfo>(phone, HttpMethod.Post, RemoteRoutes.Pairings, new PairingRequest(notebook.DeviceId));
        }

        (await ErrorAsync(phone, HttpMethod.Post, RemoteRoutes.Pairings, new PairingRequest(notebook.DeviceId)))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.TooManyPairings));
    }

    private async Task<(DesktopSession Session, ECDsa Key)> DeviceAsync(string name, DeviceRoles roles)
    {
        var session = await _api.LoginAsync("ana", deviceName: name, scope: WithRemote);
        var key = NewKey();
        await RegisterAsync(session, key, roles);
        return (session, key);
    }

    private ECDsa NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _keys.Add(key);
        return key;
    }

    private static string Fingerprint(ECDsa key) => DevicePublicKey.FingerprintOf(key.ExportSubjectPublicKeyInfo());

    private static async Task RegisterAsync(DesktopSession session, ECDsa key, DeviceRoles roles) =>
        await SendAsync<RemoteDeviceInfo>(session, HttpMethod.Put, RemoteRoutes.CurrentDevice,
            new DeviceRegistration(session.DeviceId, roles, DevicePublicKey.Es256Of(key.ExportSubjectPublicKeyInfo()), Capabilities));

    private static PairingApproval Approval(ECDsa key, PairingInfo pairing) =>
        new(Base64Url.EncodeToString(key.SignData(
            PairingCode.ApprovalPayload(pairing.PairingId, pairing.ControllerKey.Fingerprint, pairing.ExecutorKeyFingerprint, pairing.Nonce),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));

    private static async Task<T> GetAsync<T>(DesktopSession session, string path)
    {
        using var client = session.Api();
        return (await client.GetFromJsonAsync<T>(path, RemoteJson.Options, Ct))!;
    }

    private static async Task<T> SendAsync<T>(DesktopSession session, HttpMethod method, string path, object body)
    {
        using var response = await RequestAsync(session, method, path, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(RemoteJson.Options, Ct))!;
    }

    private static async Task<(HttpStatusCode Status, string? Code)> ErrorAsync(DesktopSession session, HttpMethod method, string path, object? body)
    {
        using var response = await RequestAsync(session, method, path, body);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct) is { Length: > 0 } text ? text : "{}");
        return (response.StatusCode, problem.RootElement.TryGetProperty("code", out var code) ? code.GetString() : null);
    }

    private static async Task<HttpResponseMessage> RequestAsync(DesktopSession session, HttpMethod method, string path, object? body)
    {
        using var client = session.Api();
        using var request = new HttpRequestMessage(method, path);

        if (body is not null)
        {
            request.Content = new StringContent(JsonSerializer.Serialize(body, body.GetType(), RemoteJson.Options), Encoding.UTF8, "application/json");
        }

        return await client.SendAsync(request, Ct);
    }
}
