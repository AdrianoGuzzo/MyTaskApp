using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.Application.Tests;

/// <summary>
/// O registro na orquestração (fase 2, T-04): o que entra é conferido inteiro,
/// sem cortar nem limpar nada, e a lista junta o dispositivo do sync com o que
/// ele registrou.
/// </summary>
public sealed class RemoteDeviceUseCasesTests
{
    private static readonly Guid User = Guid.CreateVersion7();

    private static readonly Guid Device = Guid.CreateVersion7();

    private readonly InMemorySyncServer _server = new();

    private readonly FakeRemoteDeviceStore _remote = new();

    private readonly RemoteDeviceUseCases _useCases;

    public RemoteDeviceUseCasesTests() =>
        _useCases = new RemoteDeviceUseCases(_server, _remote, _server.Clock, NullLogger<RemoteDeviceUseCases>.Instance);

    private static SyncCaller Caller => new(User, Device);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DeviceCapabilities Capabilities => new(
        RemoteLimits.ProtocolVersion,
        [RemoteOperationKinds.EnvironmentInspect],
        "windows",
        new ToolStatus(true, "2.51.0"),
        ToolStatus.Missing,
        ToolStatus.Missing,
        ToolStatus.Missing,
        2,
        "c0ffee");

    private static DevicePublicKey NewKey(ECCurve? curve = null)
    {
        using var key = ECDsa.Create(curve ?? ECCurve.NamedCurves.nistP256);
        return DevicePublicKey.Es256Of(key.ExportSubjectPublicKeyInfo());
    }

    private static DeviceRegistration Registration(DevicePublicKey? key = null, DeviceCapabilities? capabilities = null) =>
        new(Device, DeviceRoles.Controller | DeviceRoles.Executor, key ?? NewKey(), capabilities ?? Capabilities);

    private async Task AddDeviceAsync(Guid deviceId, string nickname, DateTimeOffset? revokedAt = null) =>
        await _server.SaveAsync(new DeviceRecord { UserId = User, DeviceId = deviceId, Nickname = nickname, RevokedAt = revokedAt }, Ct);

    [Fact]
    public async Task AValidRegistration_IsStored_AndComesBackAsTheCurrentDevice()
    {
        await AddDeviceAsync(Device, "Notebook");
        var registration = Registration();

        var info = await _useCases.RegisterAsync(Caller, registration, Ct);

        info.Should().Be(new RemoteDeviceInfo(
            Device, "Notebook", string.Empty, string.Empty, IsCurrent: true,
            registration.Roles, registration.Key.Fingerprint, registration.Capabilities, InMemorySyncServer.Now));
        info.IsOnlineAt(InMemorySyncServer.Now.Add(RemoteLimits.OfflineAfter)).Should().BeTrue();
        info.IsOnlineAt(InMemorySyncServer.Now.Add(RemoteLimits.OfflineAfter).AddSeconds(1)).Should().BeFalse();
    }

    [Fact]
    public async Task AnotherDevicesRegistration_IsForbidden()
    {
        var act = () => _useCases.RegisterAsync(Caller, Registration() with { DeviceId = Guid.CreateVersion7() }, Ct);

        (await act.Should().ThrowAsync<SyncServerException>()).Which.Code.Should().Be(SyncErrorCodes.Forbidden);
        _remote.States.Should().BeEmpty();
    }

    public static TheoryData<string, DeviceRegistration> Invalid()
    {
        var key = NewKey();
        var other = NewKey();
        var data = new TheoryData<string, DeviceRegistration>
        {
            { "outro algoritmo", Registration(key with { Algorithm = "RS256" }) },
            { "impressão digital de outra chave", Registration(key with { Fingerprint = other.Fingerprint }) },
            { "SPKI que não é base64url", Registration(key with { SubjectPublicKeyInfo = key.SubjectPublicKeyInfo + "+" }) },
            { "SPKI que não é chave", Registration(DevicePublicKey.Es256Of("não é chave"u8)) },
            { "P-384", Registration(NewKey(ECCurve.NamedCurves.nistP384)) },
            { "papel desconhecido", Registration() with { Roles = (DeviceRoles)4 } },
            { "protocolo zero", Registration(capabilities: Capabilities with { ProtocolVersion = 0 }) },
            { "operações demais", Registration(capabilities: Capabilities with { Operations = [.. Enumerable.Range(0, 33).Select(index => $"op.{index}")] }) },
            { "operação repetida", Registration(capabilities: Capabilities with { Operations = ["a", "a"] }) },
            { "operação vazia", Registration(capabilities: Capabilities with { Operations = [string.Empty] }) },
            { "operação com ESC", Registration(capabilities: Capabilities with { Operations = ["env\u001b[31m"] }) },
            { "sistema vazio", Registration(capabilities: Capabilities with { OperatingSystem = string.Empty }) },
            { "versão com U+202E", Registration(capabilities: Capabilities with { Git = new ToolStatus(true, "2.‮15") }) },
            { "versão longa", Registration(capabilities: Capabilities with { ClaudeCode = new ToolStatus(true, new string('9', 41)) }) },
            { "hash longo", Registration(capabilities: Capabilities with { CatalogHash = new string('a', 129) }) },
            { "nenhuma simultânea", Registration(capabilities: Capabilities with { MaxConcurrentOperations = 0 }) },
            { "simultâneas demais", Registration(capabilities: Capabilities with { MaxConcurrentOperations = 17 }) },
        };

        return data;
    }

    [Theory]
    [MemberData(nameof(Invalid))]
    public async Task AnInvalidRegistration_IsRefused_AndNothingIsStored(string reason, DeviceRegistration registration)
    {
        await AddDeviceAsync(Device, "Notebook");

        var act = () => _useCases.RegisterAsync(Caller, registration, Ct);

        (await act.Should().ThrowAsync<SyncServerException>(reason)).Which.Code.Should().Be(SyncErrorCodes.BadRequest);
        _remote.States.Should().BeEmpty();
    }

    [Fact]
    public async Task TheList_ShowsEveryActiveDevice_RegisteredOrNot_TheLiveOnesFirst()
    {
        var phone = Guid.CreateVersion7();
        var revoked = Guid.CreateVersion7();
        await AddDeviceAsync(Device, "Notebook");
        await AddDeviceAsync(phone, "Celular");
        await AddDeviceAsync(revoked, "Antigo", revokedAt: InMemorySyncServer.Now);

        await _useCases.RegisterAsync(Caller, Registration(), Ct);

        var list = await _useCases.ListAsync(Caller, Ct);

        list.Select(device => (device.Nickname, device.IsCurrent, device.Roles, device.KeyFingerprint is null))
            .Should().Equal(
                ("Notebook", true, DeviceRoles.Controller | DeviceRoles.Executor, false),
                ("Celular", false, DeviceRoles.None, true));
    }

    private sealed class FakeRemoteDeviceStore : IRemoteDeviceStore
    {
        public Dictionary<Guid, RemoteDeviceState> States { get; } = [];

        public Task<RemoteRegistrationOutcome> RegisterAsync(SyncCaller caller, DeviceRegistration registration, DateTimeOffset at, CancellationToken cancellationToken)
        {
            var outcome = States.GetValueOrDefault(caller.DeviceId) switch
            {
                null => RemoteRegistrationOutcome.Registered,
                { } state when state.KeyFingerprint == registration.Key.Fingerprint => RemoteRegistrationOutcome.Renewed,
                _ => RemoteRegistrationOutcome.KeyReplaced,
            };

            States[caller.DeviceId] = new(caller.DeviceId, registration.Roles, registration.Key.Fingerprint, registration.Capabilities, at);
            return Task.FromResult(outcome);
        }

        public Task<IReadOnlyList<RemoteDeviceState>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteDeviceState>>([.. States.Values]);

        public Task<DevicePublicKey?> FindActiveKeyAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException("O registro e a lista não leem a chave sozinha.");
    }
}
