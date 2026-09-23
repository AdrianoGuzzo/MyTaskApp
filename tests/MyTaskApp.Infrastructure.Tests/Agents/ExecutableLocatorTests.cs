using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Tests.Agents;

/// <summary>A procura de executáveis que o Git e os agentes compartilham (ADR-027, ADR-029).</summary>
public class ExecutableLocatorTests
{
    [Fact]
    public void EachFolder_IsTriedWithEveryName_BeforeTheNextFolder()
    {
        var locator = new ExecutableLocator(
            (name, target) => name == "PATH" && target == EnvironmentVariableTarget.Process ? @"C:\a;C:\b" : null,
            _ => false,
            isWindows: true);

        locator.Candidates(["x.exe", "x.cmd"], [@"C:\known"]).Should().Equal(
            @"C:\a\x.exe",
            @"C:\a\x.cmd",
            @"C:\b\x.exe",
            @"C:\b\x.cmd",
            @"C:\known\x.exe",
            @"C:\known\x.cmd");
    }

    [Fact]
    public void RelativeAndRepeatedEntries_AreIgnored()
    {
        var locator = new ExecutableLocator(
            (name, target) => name == "PATH" && target == EnvironmentVariableTarget.Process ? @".\bin;C:\a;c:\A" : null,
            _ => false,
            isWindows: true);

        locator.Candidates(["x.exe"], []).Should().Equal(@"C:\a\x.exe");
    }

    [Fact]
    public void Locate_ReturnsTheFirstThatExists()
    {
        var locator = new ExecutableLocator(
            (name, target) => name == "PATH" && target == EnvironmentVariableTarget.Process ? @"C:\a;C:\b" : null,
            path => path is @"C:\b\x.cmd" or @"C:\known\x.exe",
            isWindows: true);

        locator.Locate(["x.exe", "x.cmd"], [@"C:\known"]).Should().Be(@"C:\b\x.cmd");
    }

    [Fact]
    public void AnEmptyVariable_IsNoVariable()
    {
        var locator = new ExecutableLocator((_, _) => "", _ => false, isWindows: true);

        locator.Variable("APPDATA").Should().BeNull();
    }
}
