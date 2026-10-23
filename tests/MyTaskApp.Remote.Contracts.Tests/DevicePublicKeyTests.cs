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
}
