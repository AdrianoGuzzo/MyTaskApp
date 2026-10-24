using System.Buffers.Text;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Infrastructure.Persistence;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// O que os testes da fila, do hub e das aprovações repetem: o login com o
/// escopo remote, o registro com uma chave nova, o envelope assinado e as
/// chamadas com o JSON estrito da orquestração.
/// </summary>
internal sealed class RemoteTestKit(ApiFactory api) : IDisposable
{
    public const string WithRemote = ApiFactory.SyncScopes + " " + RemoteSchema.Scope;

    private readonly List<ECDsa> _keys = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DeviceCapabilities Capabilities => new(
        RemoteLimits.ProtocolVersion, [RemoteOperationKinds.EnvironmentInspect], "windows",
        ToolStatus.Missing, ToolStatus.Missing, ToolStatus.Missing, ToolStatus.Missing, 1, string.Empty);

    public void Dispose() => _keys.ForEach(key => key.Dispose());

    /// <summary>Um dispositivo da conta de <paramref name="subject"/>, registrado com uma chave nova.</summary>
    public async Task<RemoteDevice> DeviceAsync(string name, DeviceRoles roles, string subject = "ana")
    {
        var session = await api.LoginAsync(subject, deviceName: name, scope: WithRemote);
        var key = NewKey();

        await SendAsync<RemoteDeviceInfo>(session, HttpMethod.Put, RemoteRoutes.CurrentDevice,
            new DeviceRegistration(session.DeviceId, roles, DevicePublicKey.Es256Of(key.ExportSubjectPublicKeyInfo()), Capabilities));

        return new RemoteDevice(session, key);
    }

    public ECDsa NewKey()
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        _keys.Add(key);
        return key;
    }

    /// <summary>Uma inspeção de <paramref name="from"/> para <paramref name="to"/>, que vale por dez minutos.</summary>
    public static RemoteOperationRequest Inspection(Guid workspaceId, RemoteDevice from, RemoteDevice to)
    {
        var now = DateTimeOffset.UtcNow;

        return new RemoteOperationRequest(
            Guid.CreateVersion7(),
            workspaceId,
            to.DeviceId,
            from.DeviceId,
            RemoteOperationKinds.EnvironmentInspect,
            JsonSerializer.SerializeToElement(new EnvironmentInspectionRequest(), RemoteJson.Options),
            null,
            [],
            now,
            now.AddMinutes(10));
    }

    public static SignedEnvelope Sign<T>(ECDsa key, T payload, string type, string? kid = null)
    {
        var input = RemoteJws.SigningInput(payload, kid ?? DevicePublicKey.FingerprintOf(key.ExportSubjectPublicKeyInfo()), type);
        var signature = key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return RemoteJws.Compose(input, Base64Url.EncodeToString(signature));
    }

    /// <summary>Cria a operação assinada pelo próprio controlador.</summary>
    public static Task<RemoteOperationView> CreateAsync(RemoteDevice controller, RemoteOperationRequest request) =>
        SendAsync<RemoteOperationView>(controller.Session, HttpMethod.Post, RemoteRoutes.Operations,
            Sign(controller.Key, request, RemoteJws.OperationType), HttpStatusCode.Created);

    public static Task<RemoteOperationView> MoveAsync(RemoteDevice agent, Guid operationId, RemoteOperationStatus status, string? code = null, string? summary = null) =>
        SendAsync<RemoteOperationView>(agent.Session, HttpMethod.Post, RemoteRoutes.OperationStatus(operationId), new OperationStatusUpdate(status, code, summary));

    /// <summary>Mexe no banco direto, para o que o teste não espera acontecer (o prazo, a hora da entrega).</summary>
    public async Task WithDatabaseAsync(Func<ServerDbContext, Task> change)
    {
        await using var scope = api.Services.CreateAsyncScope();
        await change(scope.ServiceProvider.GetRequiredService<ServerDbContext>());
    }

    public static async Task<T> GetAsync<T>(DesktopSession session, string path)
    {
        using var response = await RequestAsync(session, HttpMethod.Get, path, null);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(RemoteJson.Options, Ct))!;
    }

    public static async Task<T> SendAsync<T>(DesktopSession session, HttpMethod method, string path, object? body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await RequestAsync(session, method, path, body);
        response.StatusCode.Should().Be(expected, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(RemoteJson.Options, Ct))!;
    }

    public static async Task<(HttpStatusCode Status, string? Code)> ErrorAsync(DesktopSession session, HttpMethod method, string path, object? body)
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

/// <summary>Um dispositivo registrado na orquestração, com a chave dele.</summary>
internal sealed record RemoteDevice(DesktopSession Session, ECDsa Key)
{
    public Guid DeviceId => Session.DeviceId;
}
