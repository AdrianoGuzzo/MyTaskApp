using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace MyTaskApp.Remote.Contracts.Tests;

/// <summary>
/// O envelope assinado dos pedidos (ADR-087): só a chave do <c>kid</c> e o
/// tipo certo conferem, e o payload lido é o dos bytes assinados.
/// </summary>
public sealed class RemoteJwsTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ASignedOperation_VerifiesWithItsKey_AndReadsBack()
    {
        using var controller = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = Request();

        var envelope = Sign(controller, request, RemoteJws.OperationType);

        RemoteJws.TryVerify<RemoteOperationRequest>(envelope, RemoteJws.OperationType, KeyOf(controller), out var read).Should().BeTrue();
        read.OperationId.Should().Be(request.OperationId);
        read.Kind.Should().Be(RemoteOperationKinds.EnvironmentInspect);
    }

    [Fact]
    public void AnotherKey_DoesNotVerify()
    {
        using var controller = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var intruder = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var envelope = Sign(intruder, Request(), RemoteJws.OperationType, kid: KeyOf(controller).Fingerprint);

        RemoteJws.TryVerify<RemoteOperationRequest>(envelope, RemoteJws.OperationType, KeyOf(controller), out _).Should().BeFalse();
    }

    [Fact]
    public void AnApprovalSignature_IsNeverAnOperation()
    {
        using var controller = ECDsa.Create(ECCurve.NamedCurves.nistP256);

        var envelope = Sign(controller, Request(), RemoteJws.ApprovalType);

        RemoteJws.TryVerify<RemoteOperationRequest>(envelope, RemoteJws.OperationType, KeyOf(controller), out _).Should().BeFalse();
    }

    [Fact]
    public void ChangingThePayload_BreaksTheSignature()
    {
        using var controller = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var envelope = Sign(controller, Request(), RemoteJws.OperationType);
        var parts = envelope.Jws.Split('.');
        var forged = Request() with { TargetDeviceId = Guid.CreateVersion7() };
        var forgedBody = Base64Url.EncodeToString(JsonSerializer.SerializeToUtf8Bytes(forged, RemoteJson.Options));

        var tampered = new SignedEnvelope($"{parts[0]}.{forgedBody}.{parts[2]}");

        RemoteJws.TryVerify<RemoteOperationRequest>(tampered, RemoteJws.OperationType, KeyOf(controller), out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData("!!.@@.##")]
    public void Malformed_IsNotParsed(string jws)
    {
        RemoteJws.TryParse(new SignedEnvelope(jws), out _).Should().BeFalse();
    }

    [Fact]
    public void AnotherAlgorithm_IsNotParsed()
    {
        var header = Base64Url.EncodeToString(Encoding.UTF8.GetBytes("""{"alg":"none","kid":"x","typ":"mytaskapp-operation+jws"}"""));

        RemoteJws.TryParse(new SignedEnvelope($"{header}.e30.AAAA"), out _).Should().BeFalse();
    }

    [Fact]
    public void ApprovalHash_ChangesWithWhatThePersonSees()
    {
        var request = ApprovalHash.Seal(new ApprovalRequest(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            ApprovalKind.CommandConfirmation,
            "Rodar: dotnet test",
            JsonSerializer.SerializeToElement(new { command = "dotnet test" }),
            null,
            Now.AddMinutes(10),
            string.Empty));

        ApprovalHash.IsSealed(request).Should().BeTrue();
        ApprovalHash.IsSealed(request with { Summary = "Rodar: rm -rf /" }).Should().BeFalse();
        ApprovalHash.IsSealed(request with { Details = JsonSerializer.SerializeToElement(new { command = "outro" }) }).Should().BeFalse();
    }

    internal static DevicePublicKey KeyOf(ECDsa key) => DevicePublicKey.Es256Of(key.ExportSubjectPublicKeyInfo());

    internal static SignedEnvelope Sign<T>(ECDsa key, T payload, string type, string? kid = null)
    {
        var input = RemoteJws.SigningInput(payload, kid ?? KeyOf(key).Fingerprint, type);
        var signature = key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

        return RemoteJws.Compose(input, Base64Url.EncodeToString(signature));
    }

    private static RemoteOperationRequest Request() => new(
        Guid.Parse("01920000-0000-7000-8000-000000000001"),
        Guid.Parse("01920000-0000-7000-8000-000000000002"),
        Guid.Parse("01920000-0000-7000-8000-000000000003"),
        Guid.Parse("01920000-0000-7000-8000-000000000004"),
        RemoteOperationKinds.EnvironmentInspect,
        JsonSerializer.SerializeToElement(new EnvironmentInspectionRequest(), RemoteJson.Options),
        null,
        [],
        Now,
        Now.AddMinutes(10));
}
