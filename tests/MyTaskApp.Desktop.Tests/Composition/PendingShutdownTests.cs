using System.Diagnostics;
using MyTaskApp.Desktop.Composition;

namespace MyTaskApp.Desktop.Tests.Composition;

/// <summary>
/// O encerramento roda fora da thread da UI, e o contêiner só é descartado
/// depois dele: sem a espera, o <c>Dispose</c> do contêiner corria contra os
/// <c>GetRequiredService</c> do encerramento, e a falha sumia sem log.
/// </summary>
public class PendingShutdownTests
{
    [Fact]
    public void TheWait_LastsUntilTheShutdownEnds()
    {
        var finished = false;
        var shutdown = new PendingShutdown();
        shutdown.Start(() =>
        {
            Thread.Sleep(300);
            finished = true;
        });

        shutdown.Wait(TimeSpan.FromSeconds(10));

        finished.Should().BeTrue();
    }

    [Fact]
    public void AFailingShutdown_DoesNotThrowOnTheWayOut()
    {
        var shutdown = new PendingShutdown();
        shutdown.Start(() => throw new ObjectDisposedException("IServiceProvider"));

        var wait = () => shutdown.Wait(TimeSpan.FromSeconds(10));

        wait.Should().NotThrow();
    }

    [Fact]
    public void AShutdownThatHangs_IsLeftBehind_AfterThePatience()
    {
        using var release = new ManualResetEventSlim();
        var shutdown = new PendingShutdown();
        shutdown.Start(() => release.Wait(TimeSpan.FromSeconds(30)));
        var watch = Stopwatch.StartNew();

        shutdown.Wait(TimeSpan.FromMilliseconds(200));

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(10));
        release.Set();
    }

    [Fact]
    public void WithoutAShutdown_ThereIsNothingToWaitFor()
    {
        var wait = () => new PendingShutdown().Wait(TimeSpan.FromSeconds(10));

        wait.Should().NotThrow();
    }
}
