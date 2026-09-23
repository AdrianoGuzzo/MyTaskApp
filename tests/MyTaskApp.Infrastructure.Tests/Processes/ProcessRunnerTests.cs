using System.Diagnostics;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Tests.Processes;

/// <summary>
/// A execução de processo de verdade (ADR-027), com o <c>dotnet</c> que roda os
/// próprios testes — o único executável que certamente existe aqui.
/// </summary>
public class ProcessRunnerTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Dotnet => Environment.ProcessPath is { } path
        && Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
            ? path
            : "dotnet";

    private readonly ProcessRunner _runner = new();

    [Fact]
    public async Task CapturesOutputAndExitCode()
    {
        var result = await _runner.RunAsync(new ProcessRequest(Dotnet, ["--version"], TimeSpan.FromMinutes(1)), Ct);

        result.ExitCode.Should().Be(0);
        result.TimedOut.Should().BeFalse();
        result.StandardOutput.Trim().Should().MatchRegex(@"^\d+\.\d+");
    }

    [Fact]
    public async Task CapturesTheErrorOfAFailingCommand()
    {
        var result = await _runner.RunAsync(
            new ProcessRequest(Dotnet, ["comando-que-nao-existe-xyz"], TimeSpan.FromMinutes(1)),
            Ct);

        result.ExitCode.Should().NotBe(0);
        (result.StandardError + result.StandardOutput).Should().NotBeEmpty();
    }

    [Fact]
    public async Task AMissingExecutable_FailsToStart()
    {
        await FluentActions.Awaiting(() => _runner.RunAsync(
                new ProcessRequest("executavel-que-nao-existe-xyz", [], TimeSpan.FromSeconds(5)),
                Ct))
            .Should().ThrowAsync<ProcessStartException>();
    }

    [Fact]
    public async Task ATimeout_KillsTheProcess_AndSaysSo()
    {
        var watch = Stopwatch.StartNew();

        var result = await _runner.RunAsync(SleepFor(TimeSpan.FromSeconds(30), TimeSpan.FromMilliseconds(500)), Ct);

        result.TimedOut.Should().BeTrue();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task Cancelling_KillsTheProcess_AndThrows()
    {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        cancel.CancelAfter(TimeSpan.FromMilliseconds(500));
        var watch = Stopwatch.StartNew();

        await FluentActions.Awaiting(() => _runner.RunAsync(SleepFor(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(1)), cancel.Token))
            .Should().ThrowAsync<OperationCanceledException>();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public async Task ArgumentsWithSpacesAndQuotes_ArriveIntact()
    {
        var file = Path.Combine(Path.GetTempPath(), $"mytaskapp proc {Guid.NewGuid():N}.txt");

        try
        {
            // "dotnet <arquivo>" falha, mas a mensagem cita o caminho recebido — inteiro.
            var result = await _runner.RunAsync(new ProcessRequest(Dotnet, [file], TimeSpan.FromMinutes(1)), Ct);

            (result.StandardError + result.StandardOutput).Should().Contain(Path.GetFileName(file));
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static ProcessRequest SleepFor(TimeSpan duration, TimeSpan timeout) =>
        OperatingSystem.IsWindows()
            ? new ProcessRequest("ping", ["-n", ((int)duration.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture), "127.0.0.1"], timeout)
            : new ProcessRequest("sleep", [((int)duration.TotalSeconds).ToString(System.Globalization.CultureInfo.InvariantCulture)], timeout);
}
