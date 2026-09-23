using System.Diagnostics;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Tests.Processes;

/// <summary>
/// O shell de verdade (ADR-028), só com o que todo sistema tem: <c>echo</c> e
/// <c>exit</c> existem no cmd e no sh, com a mesma sintaxe. Nada de npm, dotnet
/// ou docker — o teste não pode depender do que está instalado.
/// </summary>
public class ShellCommandExecutorTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _folder = Directory.CreateTempSubdirectory("mytaskapp-shell-").FullName;

    private readonly ShellCommandExecutor _executor = new(TimeProvider.System);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
        }

        GC.SuppressFinalize(this);
    }

    private Task<CommandExecutionResult> RunAsync(
        string command,
        IProgress<CommandOutputLine>? output = null,
        CancellationToken? cancellationToken = null,
        TimeSpan? timeout = null) =>
        _executor.ExecuteAsync(new CommandExecutionRequest(command, _folder, timeout), output, cancellationToken ?? Ct);

    [Fact]
    public async Task CapturesStandardOutput_AndSucceeds()
    {
        var result = await RunAsync("echo ola-mundo");

        result.ExitCode.Should().Be(0);
        result.Success.Should().BeTrue();
        result.StandardOutput.Should().Contain("ola-mundo");
        result.FinishedAt.Should().BeOnOrAfter(result.StartedAt);
    }

    [Fact]
    public async Task CapturesStandardError_Separately()
    {
        var result = await RunAsync("echo deu-ruim 1>&2");

        result.StandardError.Should().Contain("deu-ruim");
        result.StandardOutput.Should().NotContain("deu-ruim");
    }

    [Fact]
    public async Task ReturnsTheExitCode_AndANonZeroOneIsAFailure()
    {
        var result = await RunAsync("exit 3");

        result.ExitCode.Should().Be(3);
        result.Success.Should().BeFalse();
        result.Canceled.Should().BeFalse();
    }

    [Fact]
    public async Task ChainedCommands_RunInTheShell_AndTheLastExitCodeWins()
    {
        var result = await RunAsync("echo primeiro && echo segundo && exit 4");

        result.StandardOutput.Should().Contain("primeiro").And.Contain("segundo");
        result.ExitCode.Should().Be(4);
    }

    [Fact]
    public async Task RunsInTheWorkingDirectory()
    {
        File.WriteAllText(Path.Combine(_folder, "marcador.txt"), "x");

        var result = await RunAsync(OperatingSystem.IsWindows() ? "dir /b" : "ls");

        result.StandardOutput.Should().Contain("marcador.txt");
    }

    [Fact]
    public async Task EachLine_IsReportedAsItComes_WithItsStream()
    {
        var lines = new List<CommandOutputLine>();
        var output = new CollectingProgress(lines);

        await RunAsync("echo linha-1 && echo linha-2 && echo linha-erro 1>&2", output);

        lock (lines)
        {
            lines.Select(line => (line.Text.Trim(), line.IsError)).Should().Contain(
                [("linha-1", false), ("linha-2", false), ("linha-erro", true)]);
        }
    }

    [Fact]
    public async Task Cancelling_KillsTheProcess_KeepsTheOutput_AndSaysCanceled()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromSeconds(1));
        var watch = Stopwatch.StartNew();

        var result = await RunAsync($"echo antes && {Sleep(30)}", cancellationToken: cancel.Token);

        result.Canceled.Should().BeTrue();
        result.Success.Should().BeFalse();
        result.StandardOutput.Should().Contain("antes");
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task ATimeout_KillsTheProcess_AndSaysSo()
    {
        var result = await RunAsync(Sleep(30), timeout: TimeSpan.FromMilliseconds(700));

        result.TimedOut.Should().BeTrue();
        result.Canceled.Should().BeFalse();
        result.Success.Should().BeFalse();
    }

    [Fact]
    public async Task AFolderThatDoesNotExist_FailsToStart()
    {
        var run = () => _executor.ExecuteAsync(
            new CommandExecutionRequest("echo x", Path.Combine(_folder, "nao-existe")),
            null,
            Ct);

        await run.Should().ThrowAsync<CommandStartException>();
    }

    [Fact]
    public void Windows_UsesCmdWithTheLineVerbatim()
    {
        var launch = ShellCommandPlanner.For(
            isWindows: true,
            "npm install && npm run \"build:prod\"",
            name => name == "ComSpec" ? @"C:\Windows\system32\cmd.exe" : null);

        launch.FileName.Should().Be(@"C:\Windows\system32\cmd.exe");
        launch.RawArguments.Should().Be("/d /s /c \"chcp 65001>nul & npm install && npm run \"build:prod\"\"");
        launch.Arguments.Should().BeEmpty();
    }

    [Fact]
    public void Windows_WithoutComSpec_FallsBackToCmd()
    {
        ShellCommandPlanner.For(isWindows: true, "dir", _ => null).FileName.Should().Be("cmd.exe");
    }

    [Fact]
    public void Linux_PassesTheLineAsASingleArgumentToSh()
    {
        var launch = ShellCommandPlanner.For(isWindows: false, "npm install && echo 'a b' | cat", _ => null);

        launch.FileName.Should().Be("/bin/sh");
        launch.RawArguments.Should().BeNull();
        launch.Arguments.Should().Equal("-c", "npm install && echo 'a b' | cat");
    }

    [Fact]
    public async Task TheClockComesFromTheTimeProvider()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 9, 23, 10, 0, 0, TimeSpan.Zero));
        var executor = new ShellCommandExecutor(time);

        var result = await executor.ExecuteAsync(new CommandExecutionRequest("echo x", _folder), null, Ct);

        result.StartedAt.Should().Be(time.GetUtcNow());
    }

    /// <summary>Esperar sem depender de nada instalado: ping no Windows, sleep no POSIX.</summary>
    private static string Sleep(int seconds) =>
        OperatingSystem.IsWindows() ? $"ping -n {seconds} 127.0.0.1 >nul" : $"sleep {seconds}";

    private sealed class CollectingProgress(List<CommandOutputLine> lines) : IProgress<CommandOutputLine>
    {
        public void Report(CommandOutputLine value)
        {
            lock (lines)
            {
                lines.Add(value);
            }
        }
    }
}
