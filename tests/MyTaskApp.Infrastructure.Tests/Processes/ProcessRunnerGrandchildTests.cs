using System.Diagnostics;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Tests.Processes;

[CollectionDefinition(DisableParallelization = true)]
public sealed class ProcessRunnerGrandchildCollection;

/// <summary>
/// O processo terminou, mas deixou um neto vivo que herdou a saída (um daemon,
/// um helper de credencial). O resultado não espera por ele além do prazo: o que
/// chegou até ali é o resultado.
/// </summary>
/// <remarks>
/// Fora do paralelismo: com a suíte inteira disputando o pool de threads, a
/// leitura do pipe pode atrasar mais que o prazo curto deste teste, e o "pronto"
/// ficaria de fora sem que o código tivesse culpa. Os outros assemblies de
/// teste rodam ao mesmo tempo, e no CI, com cobertura, uma leitura chegou a
/// atrasar 6 s: o prazo tem folga para isso, e o neto vive o bastante para
/// a folga não esconder a espera.
/// </remarks>
[Collection(nameof(ProcessRunnerGrandchildCollection))]
public class ProcessRunnerGrandchildTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AGrandchildHoldingTheOutput_DoesNotHoldTheResult(bool live)
    {
        var runner = new ProcessRunner(drainTimeout: TimeSpan.FromSeconds(10));
        var request = OperatingSystem.IsWindows()
            ? new ProcessRequest("cmd.exe", ["/d", "/c", "echo pronto& start /b ping -n 90 127.0.0.1 >nul"], TimeSpan.FromMinutes(2))
            : new ProcessRequest("/bin/sh", ["-c", "echo pronto; sleep 90 &"], TimeSpan.FromMinutes(2));

        var watch = Stopwatch.StartNew();

        var result = live
            ? await runner.RunAsync(request, new Progress<CommandOutputLine>(), Ct)
            : await runner.RunAsync(request, Ct);

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(45), "o neto vive 90 s");
        result.ExitCode.Should().Be(0);
        result.TimedOut.Should().BeFalse();
        result.StandardOutput.Should().Contain("pronto");
    }

    /// <summary>O prazo de produção é folgado: cortar a saída de um Git ocupado seria pior que esperar.</summary>
    [Fact]
    public void TheProductionDrain_IsGenerous()
    {
        ProcessRunner.DefaultDrainTimeout.Should().Be(TimeSpan.FromSeconds(30));
    }
}
