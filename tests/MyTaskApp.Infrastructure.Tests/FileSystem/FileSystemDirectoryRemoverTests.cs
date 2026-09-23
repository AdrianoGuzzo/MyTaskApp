using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Infrastructure.FileSystem;

namespace MyTaskApp.Infrastructure.Tests.FileSystem;

/// <summary>
/// Apagar a pasta com processos de verdade segurando ela (ADR-029). Só no
/// Windows: fora dele, pasta aberta não impede apagar.
/// </summary>
public sealed class FileSystemDirectoryRemoverTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"mytaskapp-rm-{Guid.NewGuid():N}");

    private readonly List<Process> _started = [];

    public FileSystemDirectoryRemoverTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "sub", "a.txt"), "a");
    }

    private static FileSystemDirectoryRemover Remover() =>
        new(
            OperatingSystem.IsWindows()
                ? new WindowsDirectoryLockFinder(NullLogger<WindowsDirectoryLockFinder>.Instance)
                : new NoDirectoryLockFinder(),
            NullLogger<FileSystemDirectoryRemover>.Instance);

    /// <summary>Um terminal parado com a pasta de trabalho dentro da pasta — o caso mais comum.</summary>
    private Process TerminalInside()
    {
        var process = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c ping -n 120 127.0.0.1 >nul")
        {
            WorkingDirectory = Path.Combine(_root, "sub"),
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        _started.Add(process);
        return process;
    }

    [Fact]
    public async Task AFreeFolder_IsRemoved_EvenWithReadOnlyFiles()
    {
        var file = Path.Combine(_root, "sub", "a.txt");
        File.SetAttributes(file, FileAttributes.ReadOnly);

        var removal = await Remover().RemoveAsync(_root, [], Ct);

        removal.Removed.Should().BeTrue();
        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public async Task AFolderHeldByATerminal_ShowsWhoHoldsIt_AndKillsNothing()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Só o Windows prende pasta aberta.");

        var terminal = TerminalInside();
        await Task.Delay(500, Ct);

        var removal = await Remover().RemoveAsync(_root, [], Ct);

        removal.Removed.Should().BeFalse();
        removal.Lockers.Should().Contain(locker => locker.ProcessId == terminal.Id && locker.CanTerminate);
        removal.Lockers.Single(locker => locker.ProcessId == terminal.Id).ExecutablePath
            .Should().EndWithEquivalentOf("cmd.exe");
        removal.Terminated.Should().BeEmpty();
        terminal.HasExited.Should().BeFalse("sem o usuário mandar, ninguém é encerrado");
        Directory.Exists(_root).Should().BeTrue();
    }

    [Fact]
    public async Task Forcing_KillsTheApprovedLockers_AndRemovesTheFolder()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Só o Windows prende pasta aberta.");

        var terminal = TerminalInside();
        await Task.Delay(500, Ct);

        var remover = Remover();
        var first = await remover.RemoveAsync(_root, [], Ct);
        var forced = await remover.RemoveAsync(_root, first.Lockers, Ct);

        forced.Removed.Should().BeTrue(forced.Error);
        forced.Terminated.Should().Contain(locker => locker.ProcessId == terminal.Id);
        terminal.HasExited.Should().BeTrue();
        Directory.Exists(_root).Should().BeFalse();
    }

    [Fact]
    public async Task AnUnapprovedLocker_IsNotKilled()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Só o Windows prende pasta aberta.");

        var terminal = TerminalInside();
        await Task.Delay(500, Ct);

        // O usuário viu outro processo — com o mesmo PID, mas outro nome.
        var removal = await Remover().RemoveAsync(
            _root,
            [new(terminal.Id, "outro", null, true)],
            Ct);

        removal.Removed.Should().BeFalse();
        removal.Terminated.Should().BeEmpty();
        terminal.HasExited.Should().BeFalse();
    }

    [Fact]
    public async Task TheAppItself_IsListed_ButNeverTerminated()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Só o Windows prende pasta aberta.");

        await using (new FileStream(Path.Combine(_root, "sub", "a.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var locker = new[] { new DirectoryLocker(Environment.ProcessId, Process.GetCurrentProcess().ProcessName, null, true) };

            var removal = await Remover().RemoveAsync(_root, locker, Ct);

            removal.Removed.Should().BeFalse();
            removal.Lockers.Should().ContainSingle(found => found.ProcessId == Environment.ProcessId)
                .Which.CanTerminate.Should().BeFalse();
            removal.Terminated.Should().BeEmpty();
        }
    }

    public void Dispose()
    {
        foreach (var process in _started)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
        }

        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(_root, recursive: true);
            }
        }
        catch (IOException)
        {
        }
    }
}
