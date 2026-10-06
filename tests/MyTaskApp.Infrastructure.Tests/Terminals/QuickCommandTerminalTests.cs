using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Infrastructure.Agents;
using MyTaskApp.Infrastructure.Processes;
using MyTaskApp.Infrastructure.Terminals;
using MyTaskApp.Infrastructure.Terminals.Windows;

namespace MyTaskApp.Infrastructure.Tests.Terminals;

/// <summary>
/// O comando rápido num terminal visível (ADR-051): a linha do shell, a linha
/// crua até o lançador e o exit code de um processo que o app não segura.
/// Nenhum teste aqui abre janela.
/// </summary>
public class QuickCommandTerminalTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string? ComSpec(string name) => name == "ComSpec" ? @"C:\Windows\system32\cmd.exe" : null;

    [Fact]
    public void Windows_KeepOpen_UsesK_WithTheLineVerbatim()
    {
        var launch = ShellCommandPlanner.ForTerminal(
            isWindows: true,
            "dotnet run --project \"C:\\eco core\\Eco.Web\"",
            keepOpen: true,
            ComSpec,
            @"C:\Windows\system32");

        launch.FileName.Should().Be(@"C:\Windows\system32\cmd.exe");
        launch.RawArguments.Should().Be("/d /s /k \"chcp 65001>nul & dotnet run --project \"C:\\eco core\\Eco.Web\"\"");
        launch.Arguments.Should().BeEmpty();
    }

    [Fact]
    public void Windows_Close_UsesC()
    {
        ShellCommandPlanner.ForTerminal(isWindows: true, "dotnet build", keepOpen: false, ComSpec, @"C:\Windows\system32")
            .RawArguments.Should().StartWith("/d /s /c ");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("cmd.exe")]
    public void Windows_WithoutAnAbsoluteComSpec_UsesTheSystemCmd(string? comSpec)
    {
        ShellCommandPlanner.ForTerminal(isWindows: true, "dir", keepOpen: true, _ => comSpec, @"D:\Win\system32")
            .FileName.Should().Be(Path.Combine(@"D:\Win\system32", "cmd.exe"));
    }

    [Fact]
    public void Linux_PassesTheLineAsOneArgumentToSh()
    {
        var launch = ShellCommandPlanner.ForTerminal(isWindows: false, "npm run dev", keepOpen: true, _ => null, "/");

        launch.FileName.Should().Be("/bin/sh");
        launch.RawArguments.Should().BeNull();
        launch.Arguments.Should().Equal("-c", "npm run dev");
    }

    [Fact]
    public async Task TheLauncher_HandsTheRawLineAndTheFolderToTheTerminal()
    {
        var terminal = new RecordingTerminalLauncher();
        var launcher = new ShellTerminalCommandLauncher(terminal, isWindows: true, ComSpec, @"C:\Windows\system32");

        var result = await launcher.LaunchAsync(new TerminalCommandRequest("dotnet run", @"C:\wt", KeepOpen: true), Ct);

        result.Started.Should().BeTrue();
        terminal.Launched.Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Executable = @"C:\Windows\system32\cmd.exe",
            WorkingDirectory = @"C:\wt",
            RawArguments = "/d /s /k \"chcp 65001>nul & dotnet run\"",
        });
        terminal.Launched[0].Arguments.Should().BeEmpty();
    }

    [Fact]
    [SupportedOSPlatform("windows")]
    public void TheWindowsLauncher_RefusesRawLineAndArgumentsTogether()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("O launcher do Windows só existe no Windows.");
        }

        var launcher = new WindowsTerminalLauncher(_ => true, _ => true, NullLogger<WindowsTerminalLauncher>.Instance);

        launcher.Validate(new TerminalLaunchOptions(@"C:\Windows\system32\cmd.exe", ["/k"], @"C:\wt", RawArguments: "/k dir"))
            .Should().NotBeNull();
        launcher.Validate(new TerminalLaunchOptions(@"C:\Windows\system32\cmd.exe", [], @"C:\wt", RawArguments: "/k dir"))
            .Should().BeNull();
    }

    /// <summary>
    /// O vigia abre o processo antes de ele sair, e é isso que deixa ler o exit
    /// code de um processo que o app não iniciou.
    /// </summary>
    [Fact]
    public async Task WatchExitCode_ReportsTheExitCode_OfAProcessItDidNotStart()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/q", "/k" } }
            : new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", "read line; exit 7" } };

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;

        using var process = Process.Start(startInfo)!;
        var started = new DateTimeOffset(process.StartTime).ToUniversalTime();
        var exited = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);

        using var watch = new AgentProcessTracker().WatchExitCode(process.Id, started, code => exited.TrySetResult(code));
        watch.Should().NotBeNull();

        await process.StandardInput.WriteLineAsync(OperatingSystem.IsWindows() ? "exit 7" : "x");
        await process.StandardInput.FlushAsync(Ct);

        (await exited.Task.WaitAsync(Patience, Ct)).Should().Be(7);
    }

    private sealed class RecordingTerminalLauncher : ITerminalLauncher
    {
        public List<TerminalLaunchOptions> Launched { get; } = [];

        public Task<TerminalLaunchResult> LaunchAsync(TerminalLaunchOptions options, CancellationToken cancellationToken = default)
        {
            Launched.Add(options);
            return Task.FromResult(new TerminalLaunchResult { Started = true, ProcessId = 1, ProcessStartedAt = DateTimeOffset.UnixEpoch });
        }
    }
}
