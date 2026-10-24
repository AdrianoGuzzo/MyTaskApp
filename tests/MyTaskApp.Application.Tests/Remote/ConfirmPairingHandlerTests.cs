using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>
/// O pareamento confirmado na máquina executora (fase 2, T-05; RS-08): o
/// código que a pessoa lê no controlador precisa bater com o calculado aqui,
/// ou nada é aprovado nem gravado.
/// </summary>
public sealed class ConfirmPairingHandlerTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private readonly ECDsa _controllerKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private readonly FakeDeviceSigner _signer = new();

    private readonly FakeDeviceIdentity _identity = new();

    private readonly FakeRemoteApi _server = new();

    private readonly FakeTrustedControllerRepository _trusted = new();

    private readonly FakeRemoteSettingsStore _settings = new();

    private readonly Guid _controller = Guid.CreateVersion7();

    public ConfirmPairingHandlerTests() => _server.ExecutorKeys[_identity.DeviceId] = _signer.PublicKey;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DevicePublicKey ControllerKey => DevicePublicKey.Es256Of(_controllerKey.ExportSubjectPublicKeyInfo());

    public void Dispose()
    {
        _controllerKey.Dispose();
        _signer.Dispose();
    }

    private ConfirmPairingHandler Handler() =>
        new(_settings, _server, _signer, _identity, _trusted, _trusted, new FakeTimeProvider(Now), NullLogger<ConfirmPairingHandler>.Instance);

    private PairingInfo Ask(DevicePublicKey? controllerKey = null, PairingStatus status = PairingStatus.Pending, Guid? executor = null, string? executorFingerprint = null)
    {
        var pairing = new PairingInfo(
            Guid.CreateVersion7(),
            _controller,
            "Celular",
            controllerKey ?? ControllerKey,
            executor ?? _identity.DeviceId,
            executorFingerprint ?? _signer.PublicKey.Fingerprint,
            "bm9uY2UtZGUtdGVzdGU",
            status,
            Now.AddMinutes(10));

        _server.Pairings[pairing.PairingId] = pairing;
        return pairing;
    }

    /// <summary>O código como o controlador o mostra: das chaves que ele conhece, a dele e a do executor.</summary>
    private string CodeOnTheController(PairingInfo pairing) =>
        PairingCode.Derive(ControllerKey.Fingerprint, _signer.PublicKey.Fingerprint, pairing.Nonce);

    [Fact]
    public async Task TheCodeShownOnTheController_ApprovesOnTheServer_AndTrustsTheKeyHere()
    {
        var pairing = Ask();
        var code = CodeOnTheController(pairing);

        var trusted = await Handler().HandleAsync(new ConfirmPairing(pairing.PairingId, $"{code[..3]} {code[3..]}"), Ct);

        _server.Approved.Should().Equal(pairing.PairingId);
        _trusted.All.Should().ContainSingle().Which.Should().BeSameAs(trusted);
        trusted.Fingerprint.Should().Be(ControllerKey.Fingerprint);
        trusted.SubjectPublicKeyInfo.Should().Be(ControllerKey.SubjectPublicKeyInfo);
        trusted.ControllerDeviceId.Should().Be(_controller);
        trusted.Nickname.Should().Be("Celular");
        trusted.ApprovedAt.Should().Be(Now);
        _trusted.SaveCount.Should().Be(1);
    }

    /// <summary>RS-10: sem "Aceitar controle remoto" ligado aqui, nem o código certo pareia.</summary>
    [Fact]
    public async Task AMachineThatDoesNotAcceptControl_PairsWithNobody()
    {
        _settings.Settings = new RemoteSettings(Enabled: true, AcceptControl: false);
        var pairing = Ask();

        var act = () => Handler().HandleAsync(new ConfirmPairing(pairing.PairingId, CodeOnTheController(pairing)), Ct);

        await act.Should().ThrowAsync<DomainException>().WithMessage("*Aceitar controle remoto*");
        _server.Approved.Should().BeEmpty();
        _trusted.All.Should().BeEmpty();
    }

    [Fact]
    public async Task AWrongCode_ApprovesNothing_AndStoresNothing()
    {
        var pairing = Ask();
        var wrong = ((int.Parse(CodeOnTheController(pairing), System.Globalization.CultureInfo.InvariantCulture) + 1) % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);

        var act = () => Handler().HandleAsync(new ConfirmPairing(pairing.PairingId, wrong), Ct);

        await act.Should().ThrowAsync<DomainException>().WithMessage("*não confere*");
        _signer.Signatures.Should().Be(0);
        _server.Approved.Should().BeEmpty();
        _trusted.All.Should().BeEmpty();
        _trusted.SaveCount.Should().Be(0);
    }

    /// <summary>
    /// O servidor comprometido entrega a chave de outro no lugar da do celular.
    /// O celular mostra o código da chave dele; aqui sai outro, e a troca é pega.
    /// </summary>
    [Fact]
    public async Task AServerThatSwappedTheControllersKey_IsCaughtByTheCode()
    {
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pairing = Ask(controllerKey: DevicePublicKey.Es256Of(attacker.ExportSubjectPublicKeyInfo()));

        var act = () => Handler().HandleAsync(new ConfirmPairing(pairing.PairingId, CodeOnTheController(pairing)), Ct);

        await act.Should().ThrowAsync<DomainException>();
        _server.Approved.Should().BeEmpty();
        _trusted.All.Should().BeEmpty();
    }

    [Fact]
    public async Task ARequestForAnotherMachine_IsRefused()
    {
        var pairing = Ask(executor: Guid.CreateVersion7());

        var act = () => Handler().HandleAsync(new ConfirmPairing(pairing.PairingId, CodeOnTheController(pairing)), Ct);

        await act.Should().ThrowAsync<DomainException>().WithMessage("*outra máquina*");
        _trusted.All.Should().BeEmpty();
    }

    /// <summary>O pedido foi feito para uma chave antiga desta máquina (o cofre foi trocado depois).</summary>
    [Fact]
    public async Task ARequestForAnotherKeyOfThisMachine_IsRefused()
    {
        var pairing = Ask(executorFingerprint: new string('0', 64));

        var act = () => Handler().HandleAsync(new ConfirmPairing(pairing.PairingId, CodeOnTheController(pairing)), Ct);

        await act.Should().ThrowAsync<DomainException>().WithMessage("*chaves*");
        _trusted.All.Should().BeEmpty();
    }

    [Theory]
    [InlineData(PairingStatus.Expired)]
    [InlineData(PairingStatus.Approved)]
    public async Task ARequestThatIsNoLongerOpen_IsRefused(PairingStatus status)
    {
        var pairing = Ask(status: status);

        var act = () => Handler().HandleAsync(new ConfirmPairing(pairing.PairingId, CodeOnTheController(pairing)), Ct);

        await act.Should().ThrowAsync<DomainException>().WithMessage("*não está mais aberto*");
        _trusted.All.Should().BeEmpty();
    }

    /// <summary>Uma chave por controlador: parear de novo o mesmo celular deixa só a nova valendo.</summary>
    [Fact]
    public async Task PairingTheSameControllerAgain_RevokesThePreviousKey()
    {
        var first = Ask();
        await Handler().HandleAsync(new ConfirmPairing(first.PairingId, CodeOnTheController(first)), Ct);

        var second = Ask();
        await Handler().HandleAsync(new ConfirmPairing(second.PairingId, CodeOnTheController(second)), Ct);

        _trusted.All.Should().HaveCount(2);
        _trusted.All.Where(controller => controller.IsActive).Should().ContainSingle().Which.PairingId.Should().Be(second.PairingId);
        _trusted.All.Single(controller => controller.PairingId == first.PairingId).RevokedAt.Should().Be(Now);
    }

    [Fact]
    public async Task TheOpenRequests_ShowWhoAsks_AndOnlyThePendingOnes()
    {
        var pending = Ask();
        Ask(status: PairingStatus.Expired);

        var open = await new GetOpenPairingsHandler(_server).HandleAsync(new GetOpenPairings(), Ct);

        open.Should().Equal(new OpenPairingView(pending.PairingId, _controller, "Celular", pending.ExpiresAt));
    }
}
