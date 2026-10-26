using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Remote;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>
/// A janela "Máquinas" e o sinal de vida (fase 2, T-06): o que a máquina
/// manda ao se registrar, como as recusas do servidor viram estado da tela, e
/// o código do pareamento do lado do controlador.
/// </summary>
public sealed class RemoteMachinesTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeRemoteApi _server = new();

    private readonly FakeDeviceSigner _signer = new();

    private readonly FakeDeviceIdentity _identity = new();

    private readonly FakeRemoteSettingsStore _settings = new();

    private readonly FakeTrustedControllerRepository _trusted = new();

    private readonly FakeTimeProvider _clock = new(Now);

    private readonly FakeGitClient _git = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _signer.Dispose();

    private readonly FakeOperationQueue _queue = new();

    private readonly FakeCatalogQuery _catalog = new();

    private RemoteCatalogPublisher? _publisher;

    private MachineCapabilities Capabilities() =>
        new(
            _git,
            new AgentCliProviders([new FakeAgentCliProvider()]),
            new FakeToolLocator(),
            new SupportedRemoteOperations([RemoteOperationKinds.EnvironmentInspect]),
            RemoteDispatchLimits.Default,
            _clock);

    private RemoteCatalogPublisher Publisher() =>
        _publisher ??= new RemoteCatalogPublisher(
            new ScopedUseCaseRunner(new ServiceCollection().AddSingleton<IRemoteCatalogQuery>(_catalog).BuildServiceProvider().GetRequiredService<IServiceScopeFactory>()),
            _queue,
            NullLogger<RemoteCatalogPublisher>.Instance);

    private RegisterThisMachineHandler Register(MachineCapabilities? capabilities = null) =>
        new(_settings, _server, _signer, _identity, capabilities ?? Capabilities(), Publisher(), NullLogger<RegisterThisMachineHandler>.Instance);

    private GetMachinesHandler Machines() => new(_settings, _server, _trusted, _clock);

    [Theory]
    [InlineData(true, DeviceRoles.Controller | DeviceRoles.Executor)]
    [InlineData(false, DeviceRoles.Controller)]
    public async Task Registering_SendsTheKey_AndTheExecutorRoleOnlyWhenControlIsAccepted(bool acceptControl, DeviceRoles roles)
    {
        _settings.Settings = new RemoteSettings(true, acceptControl);

        (await Register().HandleAsync(new RegisterThisMachine(), Ct)).Should().Be(RemoteAvailability.Ready);

        var registration = _server.Registrations.Should().ContainSingle().Subject;
        registration.DeviceId.Should().Be(_identity.DeviceId);
        registration.Roles.Should().Be(roles);
        registration.Key.Should().Be(_signer.PublicKey);
        registration.Capabilities.ProtocolVersion.Should().Be(RemoteLimits.ProtocolVersion);
        registration.Capabilities.Operations.Should().Equal(RemoteOperationKinds.EnvironmentInspect);
        registration.Capabilities.MaxConcurrentOperations.Should().Be(RemoteDispatchLimits.Default.MaxConcurrent);
    }

    /// <summary>O registro grava o hash do catálogo de agora; o catálogo sobe só quando muda, e só de quem é executora.</summary>
    [Fact]
    public async Task AnExecutor_SendsTheHashOfItsCatalog_AndPublishesItOnlyWhenItChanges()
    {
        _catalog.Commands.Add(new CatalogCommand(Guid.CreateVersion7(), "testes", CommandScope.Global, null, false, []));

        await Register().HandleAsync(new RegisterThisMachine(), Ct);
        await Register().HandleAsync(new RegisterThisMachine(), Ct);

        var published = _queue.Catalogs.Should().ContainSingle().Subject;
        published.Hash.Should().HaveLength(64);
        _server.Registrations.Should().HaveCount(2).And.OnlyContain(registration => registration.Capabilities.CatalogHash == published.Hash);

        _catalog.Commands.Add(new CatalogCommand(Guid.CreateVersion7(), "lint", CommandScope.Global, null, true, []));
        await Register().HandleAsync(new RegisterThisMachine(), Ct);

        _queue.Catalogs.Should().HaveCount(2);
        _queue.Catalogs[1].Hash.Should().NotBe(published.Hash);
    }

    [Fact]
    public async Task OnlyAController_PublishesNoCatalog()
    {
        _settings.Settings = new RemoteSettings(true, false);

        await Register().HandleAsync(new RegisterThisMachine(), Ct);

        _queue.Catalogs.Should().BeEmpty();
        _server.Registrations.Should().ContainSingle().Which.Capabilities.CatalogHash.Should().BeEmpty();
    }

    [Fact]
    public void TheSameCatalogInAnotherOrder_HasTheSameHash()
    {
        var first = new CatalogCommand(Guid.CreateVersion7(), "a", CommandScope.Global, null, false, []);
        var second = new CatalogCommand(Guid.CreateVersion7(), "b", CommandScope.Global, null, false, []);

        var one = RemoteCatalogs.Seal(new AgentCatalog(string.Empty, [], [first, second], [], []));
        var other = RemoteCatalogs.Seal(new AgentCatalog("qualquer", [], [second, first], [], []));

        other.Hash.Should().Be(one.Hash);
    }

    [Fact]
    public async Task WithTheRemoteOff_NothingIsSent()
    {
        _settings.Settings = RemoteSettings.Off;

        (await Register().HandleAsync(new RegisterThisMachine(), Ct)).Should().Be(RemoteAvailability.Off);

        _server.Registrations.Should().BeEmpty();
    }

    /// <summary>O registro de cada minuto não roda <c>git --version</c> de novo; depois de uma hora, roda.</summary>
    [Fact]
    public async Task TheTools_AreDetectedOncePerHour()
    {
        var capabilities = Capabilities();

        await Register(capabilities).HandleAsync(new RegisterThisMachine(), Ct);
        _clock.Advance(TimeSpan.FromMinutes(59));
        await Register(capabilities).HandleAsync(new RegisterThisMachine(), Ct);
        _git.Calls.Count(call => call == "--version").Should().Be(1);

        _clock.Advance(TimeSpan.FromMinutes(2));
        await Register(capabilities).HandleAsync(new RegisterThisMachine(), Ct);
        _git.Calls.Count(call => call == "--version").Should().Be(2);
    }

    /// <summary>A saída de <c>--version</c> não é nossa: controle e excesso sairiam do registro inteiro no servidor.</summary>
    [Theory]
    [InlineData("2.51.0\r\n", "2.51.0")]
    [InlineData("\u001b[1m2.1.309\u001b[0m", "[1m2.1.309[0m")]
    [InlineData("   ", null)]
    public void AToolVersion_IsCleanedBeforeGoing(string reported, string? sent)
    {
        MachineCapabilities.Tool(true, reported).Should().Be(new ToolStatus(true, sent));
    }

    [Fact]
    public void ALongToolVersion_IsCut()
    {
        MachineCapabilities.Tool(true, new string('9', 60)).Version.Should().HaveLength(40);
        MachineCapabilities.Tool(false, "2.0").Should().Be(ToolStatus.Missing);
    }

    [Fact]
    public async Task TheMachines_ShowWhoIsOnline_AndTheirTools()
    {
        var notebook = Guid.CreateVersion7();
        _server.Devices.Add(new RemoteDeviceInfo(
            notebook, "Notebook", "windows", "1.25.0", false, DeviceRoles.Executor, "ab12",
            new DeviceCapabilities(1, [], "windows", new ToolStatus(true, "2.51.0"), new ToolStatus(true, null), ToolStatus.Missing, ToolStatus.Missing, 1, string.Empty),
            Now.AddSeconds(-30)));
        _server.Devices.Add(new RemoteDeviceInfo(
            Guid.CreateVersion7(), "Antigo", "linux", "1.20.0", false, DeviceRoles.None, null, null, Now.AddMinutes(-5)));

        var view = await Machines().HandleAsync(new GetMachines(), Ct);

        view.Availability.Should().Be(RemoteAvailability.Ready);
        view.Machines.Select(machine => (machine.Nickname, machine.IsOnline, machine.AcceptsControl)).Should().Equal(
            ("Notebook", true, true),
            ("Antigo", false, false));
        view.Machines[0].Tools.Should().Equal("Git 2.51.0", "Claude Code");
    }

    /// <summary>Os pedidos de pareamento só aparecem para quem aceita ser controlado.</summary>
    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public async Task TheOpenRequests_ShowOnlyWhenControlIsAccepted(bool acceptControl, int shown)
    {
        _settings.Settings = new RemoteSettings(true, acceptControl);
        _server.Pairings[Guid.CreateVersion7()] = new PairingInfo(
            Guid.CreateVersion7(), Guid.CreateVersion7(), "Celular", DevicePublicKey.Es256Of([1]), _identity.DeviceId, "ff", "bm9uY2U", PairingStatus.Pending, Now.AddMinutes(5));

        (await Machines().HandleAsync(new GetMachines(), Ct)).OpenPairings.Should().HaveCount(shown);
    }

    public static TheoryData<SyncApiException, RemoteAvailability> Refusals() => new()
    {
        { new SyncApiException(SyncFailureKind.NeedsReconnect, "Conecte de novo."), RemoteAvailability.NotConnected },
        { new SyncApiException(SyncFailureKind.Forbidden, "Sem permissão."), RemoteAvailability.NeedsRemoteLogin },
        { new SyncApiException(SyncFailureKind.NotFound, "Não encontrado."), RemoteAvailability.ServerWithoutRemote },
        { new SyncApiException(SyncFailureKind.Transient, "Sem conexão com o servidor."), RemoteAvailability.Unreachable },
        { new SyncApiException(SyncFailureKind.DeviceRevoked, "Revogado.", "device_revoked"), RemoteAvailability.NotConnected },
    };

    /// <summary>A recusa do servidor vira o estado da janela, e os confiáveis locais aparecem mesmo assim.</summary>
    [Theory]
    [MemberData(nameof(Refusals))]
    public async Task ARefusal_BecomesTheStateOfTheWindow(SyncApiException refusal, RemoteAvailability availability)
    {
        _server.Failure = refusal;
        await _trusted.AddAsync(TrustedController.Approve(Guid.CreateVersion7(), "Celular", new string('a', 64), "MFkw", Guid.CreateVersion7(), Now), Ct);

        var view = await Machines().HandleAsync(new GetMachines(), Ct);

        view.Availability.Should().Be(availability);
        view.Problem.Should().NotBeNullOrWhiteSpace();
        view.TrustedControllers.Should().ContainSingle().Which.Nickname.Should().Be("Celular");
    }

    [Fact]
    public async Task WithTheRemoteOff_TheWindowAsksNothingOfTheServer()
    {
        _settings.Settings = RemoteSettings.Off;
        _server.Failure = new InvalidOperationException("não devia chamar o servidor");

        (await Machines().HandleAsync(new GetMachines(), Ct)).Availability.Should().Be(RemoteAvailability.Off);
    }

    /// <summary>O código do controlador sai da chave dele mesmo, e é o mesmo que o executor vai calcular.</summary>
    [Fact]
    public async Task ThePairingCode_IsTheOneTheExecutorWillExpect()
    {
        var executor = Guid.CreateVersion7();
        _server.Devices.Add(new RemoteDeviceInfo(executor, "Notebook", "windows", "1.25.0", false, DeviceRoles.Executor, new string('e', 64), null, Now));
        var server = new ControllerKeyServer(_server, _signer.PublicKey);

        var view = await new RequestPairingHandler(server, _signer).HandleAsync(new RequestPairing(executor), Ct);

        var pairing = _server.Pairings[view.PairingId];
        view.Code.Should().Be(PairingCode.Derive(_signer.PublicKey.Fingerprint, new string('e', 64), pairing.Nonce));
        view.ExecutorDeviceId.Should().Be(executor);
    }

    /// <summary>O servidor devolveu o pedido com outra chave no lugar da deste controlador: não há código para mostrar.</summary>
    [Fact]
    public async Task AServerThatSwappedThisControllersKey_GetsNoCode()
    {
        var executor = Guid.CreateVersion7();
        _server.Devices.Add(new RemoteDeviceInfo(executor, "Notebook", "windows", "1.25.0", false, DeviceRoles.Executor, new string('e', 64), null, Now));
        using var attacker = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var server = new ControllerKeyServer(_server, DevicePublicKey.Es256Of(attacker.ExportSubjectPublicKeyInfo()));

        var act = () => new RequestPairingHandler(server, _signer).HandleAsync(new RequestPairing(executor), Ct);

        await act.Should().ThrowAsync<DomainException>().WithMessage("*não é seguro*");
    }

    [Fact]
    public async Task UpdatingTheSettings_NeverAcceptsControlWithTheRemoteOff()
    {
        var autostart = new RecordingAutostart();
        var handler = new UpdateRemoteSettingsHandler(_settings, _trusted, autostart, NullLogger<UpdateRemoteSettingsHandler>.Instance);

        (await handler.HandleAsync(new UpdateRemoteSettings(Enabled: false, AcceptControl: true), Ct)).Should().Be(RemoteSettings.Off);
        (await handler.HandleAsync(new UpdateRemoteSettings(Enabled: true, AcceptControl: true), Ct)).Should().Be(new RemoteSettings(true, true));
        _trusted.SaveCount.Should().Be(2);
        autostart.Applied.Should().Equal(false, true);
    }

    /// <summary>T-16: aceitar controle põe o agente no logon; o desktop reaplica o gravado ao abrir.</summary>
    [Fact]
    public async Task TheAgentAutostart_FollowsAcceptingControl()
    {
        var autostart = new RecordingAutostart();
        await _settings.SaveAsync(new RemoteSettings(true, true), Ct);

        await new ApplyAgentAutostartHandler(_settings, autostart).HandleAsync(new ApplyAgentAutostart(), Ct);
        await _settings.SaveAsync(new RemoteSettings(true, false), Ct);
        await new ApplyAgentAutostartHandler(_settings, autostart).HandleAsync(new ApplyAgentAutostart(), Ct);

        autostart.Applied.Should().Equal(true, false);
    }

    private sealed class RecordingAutostart : IAgentAutostart
    {
        public List<bool> Applied { get; } = [];

        public Task ApplyAsync(bool wanted, CancellationToken cancellationToken = default)
        {
            Applied.Add(wanted);
            return Task.CompletedTask;
        }
    }

    [Fact]
    public async Task RevokingAController_StopsTrustingItHere()
    {
        var controller = TrustedController.Approve(Guid.CreateVersion7(), "Celular", new string('a', 64), "MFkw", Guid.CreateVersion7(), Now);
        await _trusted.AddAsync(controller, Ct);

        await new RevokeTrustedControllerHandler(_trusted, _trusted, _clock, NullLogger<RevokeTrustedControllerHandler>.Instance)
            .HandleAsync(new RevokeTrustedController(controller.Id), Ct);

        controller.IsActive.Should().BeFalse();
        (await Machines().HandleAsync(new GetMachines(), Ct)).TrustedControllers.Should().BeEmpty();
    }

    /// <summary>O servidor de mentira devolvendo, como chave do controlador, a que o teste quiser.</summary>
    private sealed class ControllerKeyServer(FakeRemoteApi inner, DevicePublicKey controllerKey) : IRemoteApi
    {
        public Task<RemoteDeviceInfo> RegisterAsync(DeviceRegistration registration, CancellationToken cancellationToken) =>
            inner.RegisterAsync(registration, cancellationToken);

        public Task<IReadOnlyList<RemoteDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken) => inner.ListDevicesAsync(cancellationToken);

        public async Task<PairingInfo> RequestPairingAsync(Guid executorDeviceId, CancellationToken cancellationToken)
        {
            var pairing = await inner.RequestPairingAsync(executorDeviceId, cancellationToken) with { ControllerKey = controllerKey };
            inner.Pairings[pairing.PairingId] = pairing;
            return pairing;
        }

        public Task<PairingInfo> GetPairingAsync(Guid pairingId, CancellationToken cancellationToken) => inner.GetPairingAsync(pairingId, cancellationToken);

        public Task<IReadOnlyList<PairingInfo>> ListOpenPairingsAsync(CancellationToken cancellationToken) => inner.ListOpenPairingsAsync(cancellationToken);

        public Task<PairingInfo> ApprovePairingAsync(Guid pairingId, PairingApproval approval, CancellationToken cancellationToken) =>
            inner.ApprovePairingAsync(pairingId, approval, cancellationToken);
    }

    private RegisterThisControllerHandler RegisterController() =>
        new(_settings, _server, _signer, _identity, NullLogger<RegisterThisControllerHandler>.Instance);

    [Fact]
    public async Task ThePhone_RegistersAsControllerOnly_WithItsKeyAndNoTools()
    {
        _settings.Settings = new RemoteSettings(true, AcceptControl: false);

        (await RegisterController().HandleAsync(new RegisterThisController(), Ct)).Should().Be(RemoteAvailability.Ready);

        var registration = _server.Registrations.Should().ContainSingle().Subject;
        registration.Roles.Should().Be(DeviceRoles.Controller);
        registration.Key.Should().Be(_signer.PublicKey);
        registration.Capabilities.Operations.Should().BeEmpty();
        registration.Capabilities.Git.Should().Be(ToolStatus.Missing);
        registration.Capabilities.MaxConcurrentOperations.Should().Be(1, "o servidor pede de 1 a 16");
        _signer.Signatures.Should().Be(0);
    }

    [Fact]
    public async Task ThePhone_WithTheRemoteOff_RegistersNothing()
    {
        _settings.Settings = new RemoteSettings(false, false);

        (await RegisterController().HandleAsync(new RegisterThisController(), Ct)).Should().Be(RemoteAvailability.Off);

        _server.Registrations.Should().BeEmpty();
    }

    [Fact]
    public async Task ThePhone_ALoginWithoutTheRemoteScope_SaysToSignInAgain()
    {
        _settings.Settings = new RemoteSettings(true, AcceptControl: false);
        _server.Failure = new SyncApiException(SyncFailureKind.Forbidden, "Sem escopo.");

        (await RegisterController().HandleAsync(new RegisterThisController(), Ct)).Should().Be(RemoteAvailability.NeedsRemoteLogin);
    }
}
