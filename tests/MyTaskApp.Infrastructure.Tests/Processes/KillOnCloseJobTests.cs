using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Tests.Processes;

/// <summary>
/// A queda do agente leva o comando junto (E3): o neto que o comando abriu
/// depois de entrar no job também morre quando o job fecha.
/// </summary>
public sealed class KillOnCloseJobTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    [SupportedOSPlatform("windows")]
    public async Task ClosingTheJobKillsTheChildAndTheGrandchildItOpenedLater()
    {
        Assert.SkipUnless(OperatingSystem.IsWindows(), "Job Object é do Windows; no Linux, o systemd encerra o grupo.");

        using var job = KillOnCloseJob.Create();
        using var child = Process.Start(new ProcessStartInfo("cmd.exe", ["/d", "/c", "ping -n 2 127.0.0.1 >nul & ping -n 120 127.0.0.1 >nul"])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        job.Assign(child);

        // Só o cmd foi posto no job: um ping que esteja nele nasceu depois e entrou sozinho, por herança.
        // (O primeiro pode ter nascido antes do Assign e ficado fora; o segundo vem um segundo depois.)
        var deadline = DateTime.UtcNow.AddSeconds(20);
        Process? grandchild = null;

        while ((grandchild = InJob(job)) is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "O neto não abriu.");
            await Task.Delay(100, Ct);
        }

        using (grandchild)
        {
            job.Dispose();

            await child.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
            await grandchild.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(10), Ct);
        }
    }

    /// <summary>Um ping que está no job; os da máquina, de fora dele, não contam.</summary>
    [SupportedOSPlatform("windows")]
    private static Process? InJob(KillOnCloseJob job)
    {
        Process? found = null;

        foreach (var process in Process.GetProcessesByName("PING"))
        {
            try
            {
                if (found is null && job.Contains(process))
                {
                    found = process;
                    continue;
                }
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
                // Saiu enquanto olhávamos, ou é de outro usuário.
            }

            process.Dispose();
        }

        return found;
    }
}
