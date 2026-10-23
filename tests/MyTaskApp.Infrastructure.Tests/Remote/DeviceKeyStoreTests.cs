using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Remote;
using MyTaskApp.Infrastructure.Secrets;
using MyTaskApp.Infrastructure.Tests.Jira;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Infrastructure.Tests.Remote;

/// <summary>
/// A chave do dispositivo (fase 2, T-03): nasce uma vez, sobrevive a reabrir o
/// app, mora só no cofre e assina como o JWS pede.
/// </summary>
public sealed class DeviceKeyStoreTests : IDisposable
{
    private static readonly byte[] Payload = "{\"operationId\":\"0199\"}"u8.ToArray();

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MyTaskApp.Tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DeviceKeyStore Store(ISecretStore secrets, CapturingLog? log = null) =>
        new(secrets, log?.For<DeviceKeyStore>() ?? NullLogger<DeviceKeyStore>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task TheFirstCall_CreatesAP256Key_AndWritesOnlyTheSecret()
    {
        var secrets = new InMemorySecretStore();

        using var key = await Store(secrets).GetOrCreateAsync(Ct);

        secrets.Secrets.Keys.Should().Equal(DeviceKeyStore.SecretName);
        key.PublicKey.Algorithm.Should().Be(DevicePublicKey.Es256);

        var spki = Base64Url.DecodeFromChars(key.PublicKey.SubjectPublicKeyInfo);
        key.PublicKey.Fingerprint.Should().Be(DevicePublicKey.FingerprintOf(spki));

        using var imported = ECDsa.Create();
        imported.ImportSubjectPublicKeyInfo(spki, out _);
        imported.KeySize.Should().Be(256);
    }

    [Fact]
    public async Task ReopeningTheApp_KeepsTheFingerprint()
    {
        var secrets = new InMemorySecretStore();

        using var first = await Store(secrets).GetOrCreateAsync(Ct);
        using var reopened = await Store(secrets).GetOrCreateAsync(Ct);

        reopened.PublicKey.Should().Be(first.PublicKey);
    }

    /// <summary>64 bytes de r e s, conferidos pela chave pública publicada: é o que o agente e o servidor vão fazer.</summary>
    [Fact]
    public async Task TheSignature_IsEs256AsJwsWantsIt()
    {
        using var key = await Store(new InMemorySecretStore()).GetOrCreateAsync(Ct);

        var signature = key.Sign(Payload);

        signature.Should().HaveCount(64);
        using var verifier = ECDsa.Create();
        verifier.ImportSubjectPublicKeyInfo(Base64Url.DecodeFromChars(key.PublicKey.SubjectPublicKeyInfo), out _);
        verifier.VerifyData(Payload, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            .Should().BeTrue();
        verifier.VerifyData("{\"operationId\":\"0200\"}"u8, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)
            .Should().BeFalse();
    }

    [Fact]
    public async Task ManyFirstCallsAtOnce_CreateOneKey()
    {
        var secrets = new CountingSecretStore();
        var store = Store(secrets);

        var keys = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() => store.GetOrCreateAsync(Ct), Ct)));

        keys.Select(key => key.PublicKey.Fingerprint).Distinct().Should().ContainSingle();
        secrets.Writes.Should().Be(1);
        Array.ForEach(keys, key => key.Dispose());
    }

    [Theory]
    [InlineData("isto não é base64")]
    [InlineData("AAECAwQF")]
    public async Task AnUnreadableKey_IsReplaced_AndTheLogSaysSo(string stored)
    {
        var secrets = new InMemorySecretStore();
        secrets.Secrets[DeviceKeyStore.SecretName] = stored;
        var log = new CapturingLog();

        using var key = await Store(secrets, log).GetOrCreateAsync(Ct);

        secrets.Secrets[DeviceKeyStore.SecretName].Should().NotBe(stored);
        log.Mentions("DeviceKeyUnreadable").Should().BeTrue();
        log.Mentions("DeviceKeyCreated").Should().BeTrue();
    }

    /// <summary>Uma chave válida, mas de outra curva, não assina ES256.</summary>
    [Fact]
    public async Task AKeyOfAnotherCurve_IsReplacedByAP256One()
    {
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        var secrets = new InMemorySecretStore();
        secrets.Secrets[DeviceKeyStore.SecretName] = Convert.ToBase64String(p384.ExportPkcs8PrivateKey());

        using var key = await Store(secrets).GetOrCreateAsync(Ct);

        key.Sign(Payload).Should().HaveCount(64);
        DevicePublicKey.FingerprintOf(p384.ExportSubjectPublicKeyInfo()).Should().NotBe(key.PublicKey.Fingerprint);
    }

    /// <summary>Com o cofre de verdade: reabrir lê a mesma chave, e o arquivo não a traz em claro.</summary>
    [Fact]
    public async Task WithTheWindowsVault_TheKeySurvivesAndIsNotInPlainText()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI é do Windows.");

        using var first = await Store(Vault()).GetOrCreateAsync(Ct);
        using var reopened = await Store(Vault()).GetOrCreateAsync(Ct);

        reopened.PublicKey.Should().Be(first.PublicKey);

        var stored = await Vault().ReadAsync(DeviceKeyStore.SecretName, Ct);
        var file = await File.ReadAllBytesAsync(Path.Combine(_directory, DeviceKeyStore.SecretName + ".bin"), Ct);
        System.Text.Encoding.UTF8.GetString(file).Should().NotContain(stored![..32]);
    }

    /// <summary>Sem cofre, sem chave: melhor recusar do que guardá-la em claro.</summary>
    [Fact]
    public async Task WithoutAVault_ThereIsNoKey()
    {
        var act = () => Store(new UnsupportedSecretStore()).GetOrCreateAsync(Ct);

        await act.Should().ThrowAsync<DomainException>();
    }

    private ISecretStore Vault() =>
        OperatingSystem.IsWindows()
            ? new DpapiSecretStore(_directory, NullLogger<DpapiSecretStore>.Instance)
            : throw new PlatformNotSupportedException();

    private sealed class CountingSecretStore : ISecretStore
    {
        private readonly ConcurrentDictionary<string, string> _secrets = new();

        private int _writes;

        public int Writes => _writes;

        public Task<string?> ReadAsync(string name, CancellationToken cancellationToken = default) =>
            Task.FromResult(_secrets.GetValueOrDefault(name));

        public async Task WriteAsync(string name, string value, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _writes);

            // Uma gravação que demora, como a do DPAPI ou do secret-tool: dá tempo de os outros chegarem.
            await Task.Delay(50, cancellationToken);
            _secrets[name] = value;
        }

        public Task DeleteAsync(string name, CancellationToken cancellationToken = default)
        {
            _secrets.TryRemove(name, out _);
            return Task.CompletedTask;
        }
    }
}
