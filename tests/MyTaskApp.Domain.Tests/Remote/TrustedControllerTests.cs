using MyTaskApp.Domain.Remote;

namespace MyTaskApp.Domain.Tests.Remote;

public sealed class TrustedControllerTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 23, 12, 0, 0, TimeSpan.Zero);

    private static readonly string Fingerprint = new('a', TrustedController.FingerprintLength);

    private static TrustedController Approve(string nickname = "Celular", string? fingerprint = null, string spki = "MFkw", Guid? controller = null, Guid? pairing = null) =>
        TrustedController.Approve(controller ?? Guid.CreateVersion7(), nickname, fingerprint ?? Fingerprint, spki, pairing ?? Guid.CreateVersion7(), Now);

    [Fact]
    public void AnApprovedController_IsActive()
    {
        var controller = Approve();

        controller.IsActive.Should().BeTrue();
        controller.ApprovedAt.Should().Be(Now);
    }

    [Theory]
    [InlineData("  Celular  ", "Celular")]
    [InlineData("   ", "Controlador")]
    public void TheNickname_IsTrimmed_AndNeverEmpty(string nickname, string expected)
    {
        Approve(nickname).Nickname.Should().Be(expected);
    }

    [Fact]
    public void ALongNickname_IsCut()
    {
        Approve(new string('x', 80)).Nickname.Should().HaveLength(TrustedController.MaxNicknameLength);
    }

    [Theory]
    [InlineData("ABCDEF")]
    [InlineData("000000000000000000000000000000000000000000000000000000000000000z")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void AFingerprintThatIsNotLowercaseSha256Hex_IsRefused(string fingerprint)
    {
        var act = () => Approve(fingerprint: fingerprint);

        act.Should().Throw<DomainException>();
    }

    [Fact]
    public void WithoutKey_ControllerOrPairing_ItIsRefused()
    {
        ((Action)(() => Approve(spki: string.Empty))).Should().Throw<DomainException>();
        ((Action)(() => Approve(spki: new string('A', TrustedController.MaxSubjectPublicKeyInfoLength + 1)))).Should().Throw<DomainException>();
        ((Action)(() => Approve(controller: Guid.Empty))).Should().Throw<DomainException>();
        ((Action)(() => Approve(pairing: Guid.Empty))).Should().Throw<DomainException>();
    }

    [Fact]
    public void RevokingAgain_KeepsTheFirstMoment()
    {
        var controller = Approve();

        controller.Revoke(Now.AddHours(1));
        controller.Revoke(Now.AddHours(2));

        controller.IsActive.Should().BeFalse();
        controller.RevokedAt.Should().Be(Now.AddHours(1));
    }
}
