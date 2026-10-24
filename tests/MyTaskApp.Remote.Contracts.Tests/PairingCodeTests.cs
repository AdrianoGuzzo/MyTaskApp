using System.Buffers.Text;
using System.Security.Cryptography;

namespace MyTaskApp.Remote.Contracts.Tests;

/// <summary>
/// O código do pareamento (RS-08): sai igual nas duas pontas, muda com
/// qualquer uma das chaves ou com o nonce, e a assinatura da aprovação só vale
/// para aquele pareamento.
/// </summary>
public sealed class PairingCodeTests
{
    private const string Controller = "aa11";

    private const string Executor = "bb22";

    private const string Nonce = "bm9uY2U";

    [Fact]
    public void TheCode_HasSixDigits_AndIsTheSameForTheSameInputs()
    {
        var code = PairingCode.Derive(Controller, Executor, Nonce);

        code.Should().MatchRegex("^[0-9]{6}$").And.Be(PairingCode.Derive(Controller, Executor, Nonce));
    }

    /// <summary>Um servidor que troque uma chave, ou reaproveite outro nonce, muda o código.</summary>
    [Fact]
    public void TheCode_ChangesWithEachInput()
    {
        var code = PairingCode.Derive(Controller, Executor, Nonce);

        string[] others =
        [
            PairingCode.Derive("cc33", Executor, Nonce),
            PairingCode.Derive(Controller, "cc33", Nonce),
            PairingCode.Derive(Controller, Executor, "b3V0cm8"),
            PairingCode.Derive(Executor, Controller, Nonce),
        ];

        others.Should().NotContain(code);
    }

    [Theory]
    [InlineData("482913", true)]
    [InlineData("482 913", true)]
    [InlineData("482-913", true)]
    [InlineData("482914", false)]
    [InlineData("48291", false)]
    [InlineData("4829130", false)]
    [InlineData("", false)]
    public void Matches_IgnoresSpacesAndHyphens_AndNothingElse(string typed, bool matches)
    {
        PairingCode.Matches("482913", typed).Should().Be(matches);
    }

    [Fact]
    public void TheApprovalSignature_VerifiesOnlyForItsPairing()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicKey = DevicePublicKey.Es256Of(key.ExportSubjectPublicKeyInfo());
        var pairing = Guid.CreateVersion7();
        var payload = PairingCode.ApprovalPayload(pairing, Controller, Executor, Nonce);
        var signature = Base64Url.EncodeToString(key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        publicKey.Verifies(payload, signature).Should().BeTrue();
        publicKey.Verifies(PairingCode.ApprovalPayload(Guid.CreateVersion7(), Controller, Executor, Nonce), signature).Should().BeFalse();
        publicKey.Verifies(PairingCode.ApprovalPayload(pairing, "cc33", Executor, Nonce), signature).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("não é base64url")]
    [InlineData("AAAA")]
    public void ASignatureThatIsNotES256_DoesNotVerify(string signature)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        DevicePublicKey.Es256Of(key.ExportSubjectPublicKeyInfo()).Verifies("dados"u8, signature).Should().BeFalse();
    }

    [Fact]
    public void AMalformedKey_VerifiesNothing()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var signature = Base64Url.EncodeToString(key.SignData("dados"u8, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var malformed = DevicePublicKey.Es256Of(key.ExportSubjectPublicKeyInfo()) with { Fingerprint = "00" };

        malformed.Verifies("dados"u8, signature).Should().BeFalse();
    }
}
