using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Infrastructure.Terminals.Windows;

namespace MyTaskApp.Infrastructure.Tests.Agents;

/// <summary>
/// O que o launcher recusa antes de abrir qualquer janela (ADR-030). Nenhum
/// teste aqui abre terminal: todos param na validação.
/// </summary>
[SupportedOSPlatform("windows")]
public class WindowsTerminalLauncherTests
{
    private const string Claude = @"C:\Users\dev\.local\bin\claude.exe";
    private const string Worktree = @"C:\Projects\eco core-feature-123";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static WindowsTerminalLauncher Launcher(bool fileExists = true, bool directoryExists = true)
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("O launcher do Windows só existe no Windows.");
        }

        return new WindowsTerminalLauncher(
            _ => fileExists,
            _ => directoryExists,
            NullLogger<WindowsTerminalLauncher>.Instance);
    }

    [Fact]
    public void AnAbsoluteExistingExecutable_InAnExistingFolder_IsAccepted()
    {
        Launcher().Validate(new TerminalLaunchOptions(Claude, [], Worktree)).Should().BeNull();
    }

    /// <summary>Nome solto deixaria o sistema "achar" outro executável.</summary>
    [Theory]
    [InlineData("claude")]
    [InlineData(@"bin\claude.exe")]
    [InlineData("")]
    public void ARelativeExecutable_IsRefused(string executable)
    {
        Launcher().Validate(new TerminalLaunchOptions(executable, [], Worktree))
            .Should().Contain("executável");
    }

    [Fact]
    public async Task AMissingExecutable_IsRefused_WithoutOpeningAnything()
    {
        var result = await Launcher(fileExists: false).LaunchAsync(new TerminalLaunchOptions(Claude, [], Worktree), Ct);

        result.Started.Should().BeFalse();
        result.ProcessId.Should().Be(0);
        result.Error.Should().Contain(Claude);
    }

    [Fact]
    public async Task AMissingFolder_IsRefused_WithoutOpeningAnything()
    {
        var result = await Launcher(directoryExists: false).LaunchAsync(new TerminalLaunchOptions(Claude, [], Worktree), Ct);

        result.Started.Should().BeFalse();
        result.Error.Should().Contain(Worktree);
    }

    [Fact]
    public void ARelativeFolder_IsRefused()
    {
        Launcher().Validate(new TerminalLaunchOptions(Claude, [], @"Projects\x"))
            .Should().Contain("pasta");
    }
}
