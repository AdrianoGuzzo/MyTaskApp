using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Infrastructure.Persistence;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// Os dispositivos na orquestração (fase 2, T-04), de ponta a ponta no
/// PostgreSQL: registrar chave e capacidades, listar, trocar de chave, e o
/// desktop e o agente da mesma instalação registrando juntos.
/// </summary>
public sealed class RemoteDeviceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string WithRemote = ApiFactory.SyncScopes + " " + RemoteSchema.Scope;

    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DeviceCapabilities Capabilities => new(
        RemoteLimits.ProtocolVersion,
        [RemoteOperationKinds.EnvironmentInspect],
        "windows",
        new ToolStatus(true, "2.51.0"),
        new ToolStatus(true, "2.1.309"),
        ToolStatus.Missing,
        ToolStatus.Missing,
        2,
        "c0ffee");

    public async ValueTask InitializeAsync() => _api = new ApiFactory(await postgres.CreateDatabaseAsync(Ct));

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task ARegisteredDevice_ShowsUpWithItsKeyAndCapabilities_NextToOneThatOnlySyncs()
    {
        var notebook = await _api.LoginAsync("ana", deviceName: "Notebook", scope: WithRemote);
        await _api.LoginAsync("ana", deviceName: "Celular");
        var key = NewKey();

        var registered = await RegisterAsync(notebook, key, DeviceRoles.Controller | DeviceRoles.Executor);

        registered.KeyFingerprint.Should().Be(key.Fingerprint);

        var list = await ListAsync(notebook);
        list.Should().HaveCount(2);
        list[0].Should().BeEquivalentTo(new
        {
            notebook.DeviceId,
            Nickname = "Notebook",
            IsCurrent = true,
            Roles = DeviceRoles.Controller | DeviceRoles.Executor,
            KeyFingerprint = key.Fingerprint,
        });
        list[0].Capabilities!.Operations.Should().Equal(RemoteOperationKinds.EnvironmentInspect);
        list[0].Capabilities!.ClaudeCode.Version.Should().Be("2.1.309");
        list[0].IsOnlineAt(DateTimeOffset.UtcNow).Should().BeTrue();
        list[1].Should().BeEquivalentTo(new { Nickname = "Celular", IsCurrent = false, Roles = DeviceRoles.None, KeyFingerprint = (string?)null, Capabilities = (DeviceCapabilities?)null });
    }

    [Fact]
    public async Task WithoutTheRemoteScope_BothRoutesAreForbidden()
    {
        var session = await _api.LoginAsync("ana");
        using var client = session.Api();

        (await client.GetAsync(RemoteRoutes.Devices, Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.PutAsync(RemoteRoutes.CurrentDevice, Body(Registration(session, NewKey())), Ct)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await KeysAsync(session.DeviceId)).Should().BeEmpty();
    }

    /// <summary>O contrato remoto é estrito: um campo que o servidor não conhece não passa calado.</summary>
    [Fact]
    public async Task AnUnknownField_IsRefused()
    {
        var session = await _api.LoginAsync("ana", scope: WithRemote);
        var json = JsonSerializerText(Registration(session, NewKey())).Replace("\"roles\"", "\"novoCampo\":1,\"roles\"", StringComparison.Ordinal);
        using var client = session.Api();

        using var response = await client.PutAsync(RemoteRoutes.CurrentDevice, new StringContent(json, Encoding.UTF8, "application/json"), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await KeysAsync(session.DeviceId)).Should().BeEmpty();
    }

    [Fact]
    public async Task ABadKey_IsRefused()
    {
        var session = await _api.LoginAsync("ana", scope: WithRemote);
        var key = NewKey() with { Fingerprint = NewKey().Fingerprint };
        using var client = session.Api();

        using var response = await client.PutAsync(RemoteRoutes.CurrentDevice, Body(Registration(session, key)), Ct);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task TheSameKeyAgain_KeepsOneRow_AndANewKey_RevokesTheOldOne()
    {
        var session = await _api.LoginAsync("ana", scope: WithRemote);
        var first = NewKey();
        var second = NewKey();

        await RegisterAsync(session, first);
        await RegisterAsync(session, first);
        (await KeysAsync(session.DeviceId)).Should().ContainSingle().Which.RevokedAt.Should().BeNull();

        (await RegisterAsync(session, second)).KeyFingerprint.Should().Be(second.Fingerprint);

        var keys = await KeysAsync(session.DeviceId);
        keys.Should().HaveCount(2);
        keys.Single(key => key.Fingerprint == first.Fingerprint).RevokedAt.Should().NotBeNull();
        keys.Single(key => key.Fingerprint == second.Fingerprint).RevokedAt.Should().BeNull();
        _api.Logs.Text.Should().Contain("RemoteDeviceKeyReplaced");
    }

    /// <summary>O cofre restaurado traz de volta uma chave já revogada: ela volta a valer, sem linha nova.</summary>
    [Fact]
    public async Task AKeyThatComesBack_IsValidAgain()
    {
        var session = await _api.LoginAsync("ana", scope: WithRemote);
        var first = NewKey();

        await RegisterAsync(session, first);
        await RegisterAsync(session, NewKey());
        (await RegisterAsync(session, first)).KeyFingerprint.Should().Be(first.Fingerprint);

        (await KeysAsync(session.DeviceId)).Where(key => key.RevokedAt is null).Should().ContainSingle()
            .Which.Fingerprint.Should().Be(first.Fingerprint);
    }

    /// <summary>O desktop e o agente da mesma instalação sobem juntos: a trava do dispositivo põe um atrás do outro.</summary>
    [Fact]
    public async Task TheDesktopAndTheAgent_RegisteringAtOnce_BothSucceed_WithOneKey()
    {
        var session = await _api.LoginAsync("ana", scope: WithRemote);
        var key = NewKey();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RegisterAsync(session, key)));

        results.Should().AllSatisfy(result => result.KeyFingerprint.Should().Be(key.Fingerprint));
        (await KeysAsync(session.DeviceId)).Should().ContainSingle();
    }

    [Fact]
    public async Task AnotherAccount_DoesNotSeeTheDevices()
    {
        var ana = await _api.LoginAsync("ana", scope: WithRemote);
        var bia = await _api.LoginAsync("bia", scope: WithRemote);
        await RegisterAsync(ana, NewKey());

        (await ListAsync(bia)).Select(device => device.DeviceId).Should().Equal(bia.DeviceId);
    }

    [Fact]
    public async Task ARevokedDevice_LeavesTheList()
    {
        var notebook = await _api.LoginAsync("ana", deviceName: "Notebook", scope: WithRemote);
        var desktop = await _api.LoginAsync("ana", deviceName: "Desktop", scope: WithRemote);
        await RegisterAsync(desktop, NewKey());

        using var client = notebook.Api();
        (await client.PostAsync(ApiRoutes.RevokeDevice(desktop.DeviceId), null, Ct)).EnsureSuccessStatusCode();

        (await ListAsync(notebook)).Select(device => device.DeviceId).Should().Equal(notebook.DeviceId);
    }

    /// <summary>A orquestração tem cota própria: esgotá-la não derruba o resto da API.</summary>
    [Fact]
    public async Task TheRemoteQuota_IsSeparateFromTheApis()
    {
        await using var api = new ApiFactory(await postgres.CreateDatabaseAsync(Ct), new Dictionary<string, string?> { ["Sync:RateLimits:RemotePerMinute"] = "2" });
        var session = await api.LoginAsync("ana", scope: WithRemote);
        using var client = session.Api();

        var statuses = new List<HttpStatusCode>();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var response = await client.GetAsync(RemoteRoutes.Devices, Ct);
            statuses.Add(response.StatusCode);
        }

        statuses.Should().Equal(HttpStatusCode.OK, HttpStatusCode.OK, HttpStatusCode.TooManyRequests);
        (await session.GetAsync<MeResponse>(ApiRoutes.Me)).Device!.DeviceId.Should().Be(session.DeviceId);
    }

    /// <summary>A cota é de cada dispositivo: o agente que manda a saída ao vivo não tira a do celular que a lê.</summary>
    [Fact]
    public async Task TheRemoteQuota_IsPerDevice()
    {
        await using var api = new ApiFactory(await postgres.CreateDatabaseAsync(Ct), new Dictionary<string, string?> { ["Sync:RateLimits:RemotePerMinute"] = "2" });
        var notebook = await api.LoginAsync("ana", deviceName: "Notebook", scope: WithRemote);
        var phone = await api.LoginAsync("ana", deviceName: "Celular", scope: WithRemote);
        using var busy = notebook.Api();
        using var other = phone.Api();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var _ = await busy.GetAsync(RemoteRoutes.Devices, Ct);
        }

        using var exhausted = await busy.GetAsync(RemoteRoutes.Devices, Ct);
        using var fresh = await other.GetAsync(RemoteRoutes.Devices, Ct);

        exhausted.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        fresh.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static DevicePublicKey NewKey()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return DevicePublicKey.Es256Of(key.ExportSubjectPublicKeyInfo());
    }

    private static DeviceRegistration Registration(DesktopSession session, DevicePublicKey key, DeviceRoles roles = DeviceRoles.Controller) =>
        new(session.DeviceId, roles, key, Capabilities);

    private static string JsonSerializerText<T>(T value) => System.Text.Json.JsonSerializer.Serialize(value, RemoteJson.Options);

    private static StringContent Body<T>(T value) => new(JsonSerializerText(value), Encoding.UTF8, "application/json");

    private static async Task<RemoteDeviceInfo> RegisterAsync(DesktopSession session, DevicePublicKey key, DeviceRoles roles = DeviceRoles.Controller)
    {
        using var client = session.Api();
        using var response = await client.PutAsync(RemoteRoutes.CurrentDevice, Body(Registration(session, key, roles)), Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<RemoteDeviceInfo>(RemoteJson.Options, Ct))!;
    }

    private static async Task<IReadOnlyList<RemoteDeviceInfo>> ListAsync(DesktopSession session)
    {
        using var client = session.Api();
        return (await client.GetFromJsonAsync<List<RemoteDeviceInfo>>(RemoteRoutes.Devices, RemoteJson.Options, Ct))!;
    }

    private async Task<List<DeviceKeyRow>> KeysAsync(Guid deviceId)
    {
        await using var scope = _api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ServerDbContext>().DeviceKeys.AsNoTracking()
            .Where(key => key.DeviceId == deviceId)
            .ToListAsync(Ct);
    }
}
