using MyTaskApp.Infrastructure.OAuth;

namespace MyTaskApp.Infrastructure.Tests.OAuth;

public sealed class PkceTests
{
    [Fact]
    public void ChallengeFor_MatchesTheRfc7636Example()
    {
        // RFC 7636, apêndice B.
        Pkce.ChallengeFor("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk")
            .Should().Be("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM");
    }

    [Fact]
    public void NewSecret_IsUrlSafeAndUnpredictable()
    {
        var first = Pkce.NewSecret();
        var second = Pkce.NewSecret();

        first.Should().MatchRegex("^[A-Za-z0-9_-]{43}$");
        second.Should().NotBe(first);
    }
}
