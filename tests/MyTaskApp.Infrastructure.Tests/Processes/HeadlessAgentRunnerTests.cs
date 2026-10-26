using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Tests.Processes;

/// <summary>
/// O processo do agente sem terminal (ADR-089) com um processo de verdade no
/// lugar do Claude: o prompt pelo stdin, as linhas em ordem, o ambiente limpo,
/// o cancelamento e os arquivos apagados no fim.
/// </summary>
public sealed class HeadlessAgentRunnerTests : IDisposable
{
    private const string Probe = "PGMYTASKAPP_RUNNER_PROBE";

    private readonly string _folder = Directory.CreateTempSubdirectory("mytaskapp-headless-").FullName;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(Probe, null);
        Directory.Delete(_folder, recursive: true);
    }

    private static HeadlessAgentRunner Runner() => new(NullLogger<HeadlessAgentRunner>.Instance);

    /// <summary>Repete o stdin no stdout: o papel do Claude que lê o prompt.</summary>
    private HeadlessAgentLaunch Echo(IReadOnlyList<string>? files = null) =>
        OperatingSystem.IsWindows()
            ? new(Path.Combine(Environment.SystemDirectory, "findstr.exe"), ["^"], _folder, new Dictionary<string, string>(), files ?? [])
            : new("/bin/cat", [], _folder, new Dictionary<string, string>(), files ?? []);

    [Fact]
    public async Task ThePromptGoesThroughStdin_Whole_AndTheLinesComeBackInOrder()
    {
        var lines = new List<string>();
        int? processId = null;

        var result = await Runner().RunAsync(
            Echo(),
            "primeira \"com aspas\" e 100%\nsegunda linha\n",
            id => processId = id,
            line =>
            {
                lines.Add(line);
                return Task.CompletedTask;
            },
            Ct);

        result.ExitCode.Should().Be(0, result.StandardError);
        result.Canceled.Should().BeFalse();
        processId.Should().BePositive();
        lines.Should().Equal("primeira \"com aspas\" e 100%", "segunda linha");
    }

    [Fact]
    public async Task TheForeignVariables_DoNotReachTheProcess_AndTheSessionOnesDo()
    {
        Environment.SetEnvironmentVariable(Probe, "segredo-do-postgres");
        var launch = OperatingSystem.IsWindows()
            ? new HeadlessAgentLaunch(Path.Combine(Environment.SystemDirectory, "cmd.exe"), ["/d", "/c", "set"], _folder, Session(), [])
            : new HeadlessAgentLaunch("/usr/bin/env", [], _folder, Session(), []);
        var lines = new List<string>();

        await Runner().RunAsync(launch, string.Empty, _ => { }, line => { lines.Add(line); return Task.CompletedTask; }, Ct);

        lines.Should().NotContain(line => line.StartsWith(Probe, StringComparison.OrdinalIgnoreCase));
        lines.Should().Contain("MYTASKAPP_AGENT_APPROVAL_TOKEN=da-sessao");

        static Dictionary<string, string> Session() => new() { ["MYTASKAPP_AGENT_APPROVAL_TOKEN"] = "da-sessao" };
    }

    [Fact]
    public async Task TheFilesOfTheLine_AreDeleted_EvenWhenWhoReadsTheLinesFails()
    {
        var file = Path.Combine(_folder, "x.settings.json");
        await File.WriteAllTextAsync(file, "{}", Ct);

        var act = () => Runner().RunAsync(Echo([file]), "uma\nduas\n", _ => { }, _ => throw new InvalidOperationException("o diário caiu"), Ct);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("o diário caiu");
        File.Exists(file).Should().BeFalse();
    }

    [Fact]
    public async Task Canceling_EndsTheProcess()
    {
        var launch = OperatingSystem.IsWindows()
            ? new HeadlessAgentLaunch(Path.Combine(Environment.SystemDirectory, "PING.EXE"), ["-n", "60", "127.0.0.1"], _folder, new Dictionary<string, string>(), [])
            : new HeadlessAgentLaunch("/bin/sleep", ["60"], _folder, new Dictionary<string, string>(), []);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        int? processId = null;

        var run = Runner().RunAsync(launch, string.Empty, id => { processId = id; cancel.CancelAfter(TimeSpan.FromMilliseconds(300)); }, _ => Task.CompletedTask, cancel.Token);
        var result = await run.WaitAsync(TimeSpan.FromSeconds(20), Ct);

        result.Canceled.Should().BeTrue();
        IsAlive(processId!.Value).Should().BeFalse();
    }

    private static bool IsAlive(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
