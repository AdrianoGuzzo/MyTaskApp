using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Infrastructure.Secrets;

namespace MyTaskApp.Infrastructure.Tests.Secrets;

/// <summary>O cofre de verdade, com o DPAPI do Windows (ADR-045).</summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStoreTests : IDisposable
{
    private const string Token = "eyJraWQiOiJhdXRoLmF0bGFzc2lhbi5jb20vMzw-segredo-de-teste";

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "MyTaskApp.Tests", Guid.NewGuid().ToString("N"));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private DpapiSecretStore Store() => new(_directory, NullLogger<DpapiSecretStore>.Instance);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task ASecret_RoundTrips()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI é do Windows.");

        await Store().WriteAsync("jira", Token, Ct);

        (await Store().ReadAsync("jira", Ct)).Should().Be(Token);
    }

    [Fact]
    public async Task TheFile_DoesNotContainTheSecretInPlainText()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI é do Windows.");

        await Store().WriteAsync("jira", Token, Ct);

        var bytes = await File.ReadAllBytesAsync(Path.Combine(_directory, "jira.bin"), Ct);
        Encoding.UTF8.GetString(bytes).Should().NotContain("segredo-de-teste");
        Encoding.Unicode.GetString(bytes).Should().NotContain("segredo-de-teste");
    }

    [Fact]
    public async Task NothingStored_ReadsAsNull()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI é do Windows.");

        (await Store().ReadAsync("jira", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task ACorruptedFile_ReadsAsNull_InsteadOfThrowing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI é do Windows.");

        Directory.CreateDirectory(_directory);
        await File.WriteAllBytesAsync(Path.Combine(_directory, "jira.bin"), [1, 2, 3, 4], Ct);

        (await Store().ReadAsync("jira", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Delete_ForgetsTheSecret()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI é do Windows.");

        await Store().WriteAsync("jira", Token, Ct);
        await Store().DeleteAsync("jira", Ct);

        (await Store().ReadAsync("jira", Ct)).Should().BeNull();
    }

    [Theory]
    [InlineData("../fora")]
    [InlineData("a/b")]
    [InlineData("")]
    public async Task AName_CannotEscapeTheFolder(string name)
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "DPAPI é do Windows.");

        var write = () => Store().WriteAsync(name, Token, Ct);

        await write.Should().ThrowAsync<ArgumentException>();
    }
}
