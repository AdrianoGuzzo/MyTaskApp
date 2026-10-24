using System.Buffers.Text;
using System.Security.Cryptography;

namespace MyTaskApp.Remote.Contracts.Tests;

/// <summary>A impressão digital e o SPKI saem iguais em quem os calcula: o código de pareamento depende disso.</summary>
public sealed class DevicePublicKeyTests
{
    /// <summary>O vetor do SHA-256 para "abc" (FIPS 180-2), em hexadecimal minúsculo.</summary>
    [Fact]
    public void TheFingerprint_IsTheSha256OfTheSpki_InLowercaseHex()
    {
        DevicePublicKey.FingerprintOf("abc"u8)
            .Should().Be("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad");
    }

    /// <summary>0xFB 0xFF é "+/8=" em base64: em base64url, sem os dois símbolos e sem o preenchimento.</summary>
    [Fact]
    public void Es256Of_CarriesTheSpkiInBase64Url_AndItsFingerprint()
    {
        byte[] spki = [0xFB, 0xFF];

        DevicePublicKey.Es256Of(spki).Should().Be(
            new DevicePublicKey(DevicePublicKey.Es256, "-_8", DevicePublicKey.FingerprintOf(spki)));
    }

    [Fact]
    public void AP256Key_IsWellFormed()
    {
        Es256().IsWellFormedEs256().Should().BeTrue();
    }

    public static TheoryData<string, DevicePublicKey> Malformed()
    {
        var key = Es256();
        var spki = Base64Url.DecodeFromChars(key.SubjectPublicKeyInfo);
        using var p384 = ECDsa.Create(ECCurve.NamedCurves.nistP384);

        return new TheoryData<string, DevicePublicKey>
        {
            { "outro algoritmo", key with { Algorithm = "ES384" } },
            { "longo demais", key with { SubjectPublicKeyInfo = new string('A', RemoteLimits.MaxSubjectPublicKeyInfoLength + 1) } },
            { "base64 comum, com + e /", key with { SubjectPublicKeyInfo = Convert.ToBase64String(spki) } },
            { "impressão digital de outra chave", key with { Fingerprint = Es256().Fingerprint } },
            { "bytes que não são chave", DevicePublicKey.Es256Of([1, 2, 3, 4]) },
            { "bytes sobrando depois do SPKI", DevicePublicKey.Es256Of([.. spki, 0]) },
            { "outra curva", DevicePublicKey.Es256Of(p384.ExportSubjectPublicKeyInfo()) },
        };
    }

    [Theory]
    [MemberData(nameof(Malformed))]
    public void AMalformedKey_IsNotWellFormed(string reason, DevicePublicKey key)
    {
        key.IsWellFormedEs256().Should().BeFalse(reason);
    }

    [Fact]
    public void ADevice_IsOnline_UpToTheLimitAfterItsLastSignOfLife()
    {
        var at = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);
        var device = new RemoteDeviceInfo(Guid.CreateVersion7(), "Notebook", "windows", "1.25.0", false, DeviceRoles.Executor, null, null, at);

        device.IsOnlineAt(at + RemoteLimits.OfflineAfter).Should().BeTrue();
        device.IsOnlineAt(at + RemoteLimits.OfflineAfter + TimeSpan.FromSeconds(1)).Should().BeFalse();
        (device with { LastHeartbeatAt = null }).IsOnlineAt(at).Should().BeFalse();
    }

    private static DevicePublicKey Es256()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return DevicePublicKey.Es256Of(key.ExportSubjectPublicKeyInfo());
    }
}
