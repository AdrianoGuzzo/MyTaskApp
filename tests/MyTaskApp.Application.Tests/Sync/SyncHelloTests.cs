using MyTaskApp.Application.Sync;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Application.Tests.Sync;

/// <summary>O "olá" ao servidor: o sistema vai para a lista de dispositivos (ADR-061, ADR-067).</summary>
public sealed class SyncHelloTests
{
    [Fact]
    public void TheHello_NamesThisSystem_AndTheContractVersion()
    {
        var hello = SyncEngine.Hello();

        var expected = OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsLinux() ? "Linux"
            : OperatingSystem.IsMacOS() ? "macOS"
            : null;

        if (expected is not null)
        {
            hello.Platform.Should().Be(expected);
        }

        hello.Platform.Should().Be(SyncEngine.PlatformName());
        hello.SchemaVersion.Should().Be(SyncSchema.Current);
        hello.AppVersion.Should().MatchRegex(@"^\d+\.\d+\.\d+$");
    }
}
