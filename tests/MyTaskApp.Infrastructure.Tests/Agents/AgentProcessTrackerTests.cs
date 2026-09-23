using System.Diagnostics;
using MyTaskApp.Infrastructure.Agents;

namespace MyTaskApp.Infrastructure.Tests.Agents;

/// <summary>
/// O tracker contra processos de verdade (ADR-030): vivo, morto, PID
/// reaproveitado, e o aviso de fim sem polling. O processo usado é um shell
/// esperando entrada — existe em qualquer máquina que rode os testes.
/// </summary>
public class AgentProcessTrackerTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly AgentProcessTracker _tracker = new();

    /// <summary>Um processo que fica de pé até ser encerrado: espera a entrada padrão.</summary>
    private static Process StartSleeper()
    {
        var startInfo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe") { ArgumentList = { "/d", "/q", "/k" } }
            : new ProcessStartInfo("/bin/cat");

        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;
        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;

        return Process.Start(startInfo)!;
    }

    private static DateTimeOffset StartOf(Process process) =>
        new DateTimeOffset(process.StartTime).ToUniversalTime();

    [Fact]
    public void ARunningProcess_IsAlive()
    {
        using var process = StartSleeper();

        try
        {
            _tracker.IsAlive(process.Id, StartOf(process)).Should().BeTrue();
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void AnExitedProcess_IsNotAlive()
    {
        using var process = StartSleeper();
        var start = StartOf(process);
        process.Kill(entireProcessTree: true);
        process.WaitForExit();

        _tracker.IsAlive(process.Id, start).Should().BeFalse();
    }

    /// <summary>O PID existe, mas começou em outro instante: é outro programa.</summary>
    [Fact]
    public void AProcessWithTheSamePidButAnotherStart_IsNotTheSession()
    {
        using var process = StartSleeper();

        try
        {
            _tracker.IsAlive(process.Id, StartOf(process).AddMinutes(-10)).Should().BeFalse();
        }
        finally
        {
            process.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public void ANonexistentPid_IsNotAlive()
    {
        _tracker.IsAlive(0, DateTimeOffset.UtcNow).Should().BeFalse();
        _tracker.IsAlive(int.MaxValue, DateTimeOffset.UtcNow).Should().BeFalse();
    }

    [Fact]
    public void TheEndOfTheProcess_IsReported_ByEvent()
    {
        using var process = StartSleeper();
        using var exited = new ManualResetEventSlim();

        using var watch = _tracker.WatchExit(process.Id, StartOf(process), exited.Set);
        watch.Should().NotBeNull();

        process.Kill(entireProcessTree: true);

        exited.Wait(Patience, TestContext.Current.CancellationToken).Should().BeTrue();
    }

    [Fact]
    public void WatchingAGoneProcess_ReturnsNothing()
    {
        using var process = StartSleeper();
        var start = StartOf(process);
        process.Kill(entireProcessTree: true);
        process.WaitForExit();

        _tracker.WatchExit(process.Id, start, () => { }).Should().BeNull();
    }

    /// <summary>Parar de vigiar não encerra: é o que acontece quando o app fecha.</summary>
    [Fact]
    public void DisposingTheWatch_DoesNotEndTheProcess_NorReportIt()
    {
        using var process = StartSleeper();
        var reported = false;

        try
        {
            var watch = _tracker.WatchExit(process.Id, StartOf(process), () => reported = true);
            watch!.Dispose();

            process.HasExited.Should().BeFalse();
            _tracker.IsAlive(process.Id, StartOf(process)).Should().BeTrue();
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit();
        }

        reported.Should().BeFalse();
    }
}
