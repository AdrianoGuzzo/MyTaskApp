using MyTaskApp.Application.Sync;

namespace MyTaskApp.Application.Tests.Sync;

/// <summary>
/// O endereço do servidor de sincronização (ADR-078): a mesma regra para a
/// tela do celular, o arquivo do desktop e o cliente HTTP.
/// </summary>
public sealed class SyncServerUrlTests
{
    [Theory]
    [InlineData("https://sync.exemplo.com", "https://sync.exemplo.com/")]
    [InlineData("  https://sync.exemplo.com/  ", "https://sync.exemplo.com/")]
    [InlineData("https://sync.exemplo.com:8443", "https://sync.exemplo.com:8443/")]
    [InlineData("https://exemplo.com/sync", "https://exemplo.com/sync/")]
    [InlineData("https://exemplo.com/sync//", "https://exemplo.com/sync/")]
    [InlineData("HTTPS://Sync.Exemplo.com", "https://sync.exemplo.com/")]
    [InlineData("https://192.0.2.8", "https://192.0.2.8/")]
    public void AValidAddress_IsNormalized_WithOneTrailingSlash(string text, string expected)
    {
        var check = SyncServerUrl.Check(text, allowHttpLoopback: false);

        check.IsValid.Should().BeTrue(check.Error);
        check.Uri!.AbsoluteUri.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("sync.exemplo.com")]
    [InlineData("/api")]
    [InlineData("https://")]
    [InlineData("https:///caminho")]
    [InlineData("não é endereço")]
    public void Malformed_IsRejected(string? text)
    {
        var check = SyncServerUrl.Check(text, allowHttpLoopback: true);

        check.IsValid.Should().BeFalse();
        check.Error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("ftp://sync.exemplo.com")]
    [InlineData("file:///C:/sync")]
    [InlineData("javascript:alert(1)")]
    [InlineData("ws://sync.exemplo.com")]
    [InlineData("mailto:ana@exemplo.com")]
    public void UnexpectedSchemes_AreRejected(string text)
    {
        SyncServerUrl.Check(text, allowHttpLoopback: true).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("https://ana:senha@sync.exemplo.com")]
    [InlineData("https://sync.exemplo.com/?servidor=outro")]
    [InlineData("https://sync.exemplo.com/#fragmento")]
    public void Credentials_QueryAndFragment_AreRejected(string text)
    {
        SyncServerUrl.Check(text, allowHttpLoopback: false).IsValid.Should().BeFalse();
    }

    [Theory]
    [InlineData("http://sync.exemplo.com")]
    [InlineData("http://192.0.2.8:5280")]
    public void HttpOverTheNetwork_IsRejected_EvenInDevelopment(string text)
    {
        var check = SyncServerUrl.Check(text, allowHttpLoopback: true);

        check.IsValid.Should().BeFalse();
        check.Error.Should().Contain("https://");
    }

    [Theory]
    [InlineData("http://localhost:5280")]
    [InlineData("http://127.0.0.1:5280/")]
    public void HttpOnLoopback_OnlyWhenDevelopmentAllows(string text)
    {
        SyncServerUrl.Check(text, allowHttpLoopback: false).IsValid.Should().BeFalse("a Release exige HTTPS");
        SyncServerUrl.Check(text, allowHttpLoopback: true).IsValid.Should().BeTrue();
    }

    [Fact]
    public void SameServer_ComparesTheNormalizedAddresses()
    {
        SyncServerUrl.SameServer(new Uri("https://Sync.Exemplo.com"), new Uri("https://sync.exemplo.com/")).Should().BeTrue();
        SyncServerUrl.SameServer(new Uri("https://sync.exemplo.com/"), new Uri("https://outro.exemplo.com/")).Should().BeFalse();
        SyncServerUrl.SameServer(new Uri("https://sync.exemplo.com/"), new Uri("https://sync.exemplo.com:8443/")).Should().BeFalse();
        SyncServerUrl.SameServer(null, new Uri("https://sync.exemplo.com/")).Should().BeFalse();
        SyncServerUrl.SameServer(null, null).Should().BeFalse("nenhum servidor não é o mesmo servidor");
    }
}
