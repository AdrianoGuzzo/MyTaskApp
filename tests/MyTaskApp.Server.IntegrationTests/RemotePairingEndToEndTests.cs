using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Remote;
using MyTaskApp.Domain;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// O pareamento inteiro (fase 2, T-05): o desktop de verdade — SQLite, cofre,
/// cliente HTTP e casos de uso — como máquina executora, e um celular pedindo
/// pelo servidor de verdade. Código errado não grava em lado nenhum; o certo
/// grava nos dois, e a confiança sobrevive a reabrir o app.
/// </summary>
public sealed class RemotePairingEndToEndTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string WithRemote = ApiFactory.SyncScopes + " " + RemoteSchema.Scope;

    private readonly List<SyncedDesktop> _desktops = [];

    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DeviceCapabilities Capabilities => new(
        RemoteLimits.ProtocolVersion, [RemoteOperationKinds.EnvironmentInspect], "windows",
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
    public async Task TheRightCode_IsStoredOnBothSides_AndAWrongOneOnNeither()
    {
        var notebook = await DesktopAsync("Notebook");
        using var phoneKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var phone = await _api.LoginAsync("ana", deviceName: "Celular", scope: WithRemote);
        await SendAsync<RemoteDeviceInfo>(phone, HttpMethod.Put, RemoteRoutes.CurrentDevice,
            new DeviceRegistration(phone.DeviceId, DeviceRoles.Controller, DevicePublicKey.Es256Of(phoneKey.ExportSubjectPublicKeyInfo()), Capabilities));

        // O celular pede, e mostra o código das chaves que ele conhece.
        var pairing = await SendAsync<PairingInfo>(phone, HttpMethod.Post, RemoteRoutes.Pairings, new PairingRequest(notebook.Device.DeviceId));
        var notebookFingerprint = (await SendAsync<List<RemoteDeviceInfo>>(phone, HttpMethod.Get, RemoteRoutes.Devices, null))
            .Single(device => device.DeviceId == notebook.Device.DeviceId).KeyFingerprint!;
        var shownOnThePhone = PairingCode.Derive(pairing.ControllerKey.Fingerprint, notebookFingerprint, pairing.Nonce);

        // O notebook vê o pedido, sem o código.
        (await notebook.UseCaseAsync<GetOpenPairingsHandler, IReadOnlyList<OpenPairingView>>((handler, cancel) => handler.HandleAsync(new GetOpenPairings(), cancel)))
            .Should().ContainSingle().Which.ControllerNickname.Should().Be("Celular");

        // Código errado: nada no servidor, nada aqui.
        var wrong = shownOnThePhone == "000000" ? "000001" : "000000";
        var refused = () => Confirm(notebook, pairing.PairingId, wrong);
        await refused.Should().ThrowAsync<DomainException>();
        (await SendAsync<PairingInfo>(phone, HttpMethod.Get, RemoteRoutes.Pairing(pairing.PairingId), null)).Status.Should().Be(PairingStatus.Pending);
        (await notebook.ReadAsync(context => context.TrustedControllers.CountAsync(Ct))).Should().Be(0);

        // O código do celular: aprovado lá, confiado aqui.
        await Confirm(notebook, pairing.PairingId, shownOnThePhone);

        (await SendAsync<PairingInfo>(phone, HttpMethod.Get, RemoteRoutes.Pairing(pairing.PairingId), null)).Status.Should().Be(PairingStatus.Approved);
        var trusted = await notebook.ReadAsync(context => context.TrustedControllers.AsNoTracking().SingleAsync(Ct));
        trusted.ControllerDeviceId.Should().Be(phone.DeviceId);
        trusted.Fingerprint.Should().Be(DevicePublicKey.FingerprintOf(phoneKey.ExportSubjectPublicKeyInfo()));

        // Reabrir o app: a confiança fica no banco, e a chave da máquina, no cofre.
        var reopened = await notebook.RestartAsync(Ct);
        _desktops.Add(reopened);
        (await reopened.ReadAsync(context => context.TrustedControllers.CountAsync(Ct))).Should().Be(1);
        (await reopened.Services.GetRequiredService<IDeviceSigner>().GetPublicKeyAsync(Ct)).Fingerprint.Should().Be(notebookFingerprint);
    }

    /// <summary>
    /// O caminho da pessoa (T-06): liga o controle remoto e "Aceitar controle
    /// remoto", conecta pela tela de sempre (que agora pede o escopo remote) e
    /// se registra pelo caso de uso, com a chave do próprio cofre.
    /// </summary>
    private async Task<SyncedDesktop> DesktopAsync(string nickname)
    {
        var desktop = await SyncedDesktop.CreateAsync(_api, nickname, Ct);
        _desktops.Add(desktop);

        await desktop.UseCaseAsync<UpdateRemoteSettingsHandler, RemoteSettings>(
            (handler, cancel) => handler.HandleAsync(new UpdateRemoteSettings(Enabled: true, AcceptControl: true), cancel));
        await desktop.ConnectAsync("ana", Ct);

        (await desktop.UseCaseAsync<RegisterThisMachineHandler, RemoteAvailability>(
                (handler, cancel) => handler.HandleAsync(new RegisterThisMachine(), cancel)))
            .Should().Be(RemoteAvailability.Ready);

        return desktop;
    }

    private static Task Confirm(SyncedDesktop desktop, Guid pairingId, string code) =>
        desktop.UseCaseAsync<ConfirmPairingHandler>((handler, cancel) => handler.HandleAsync(new ConfirmPairing(pairingId, code), cancel));

    private static StringContent Body(object value) =>
        new(JsonSerializer.Serialize(value, value.GetType(), RemoteJson.Options), Encoding.UTF8, "application/json");

    private static async Task<T> SendAsync<T>(DesktopSession session, HttpMethod method, string path, object? body)
    {
        using var client = session.Api();
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : Body(body) };
        using var response = await client.SendAsync(request, Ct);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync(Ct));
        return (await response.Content.ReadFromJsonAsync<T>(RemoteJson.Options, Ct))!;
    }
}
