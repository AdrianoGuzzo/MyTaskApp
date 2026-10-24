using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Win32;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Tests.Processes;

/// <summary>
/// O agente no logon (ADR-085, T-16): ligar registra e sobe o que não está
/// rodando; desligar tira do logon; nada disso derruba quem chamou.
/// </summary>
public sealed class AgentAutostartTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), $"mytaskapp-autostart-{Guid.NewGuid():N}");

    private readonly FakeEntry _entry = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public AgentAutostartTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (IOException)
        {
            // Fica na pasta temporária.
        }
    }

    [Fact]
    public async Task Wanted_RegistersAndStartsTheAgent_WhenItIsNotRunning()
    {
        var executable = Executable();

        await Autostart(executable, running: false).ApplyAsync(wanted: true, Ct);

        _entry.Registered.Should().Be(executable);
        _entry.Started.Should().Equal(executable);
    }

    [Fact]
    public async Task Wanted_DoesNotStartASecondAgent()
    {
        await Autostart(Executable(), running: true).ApplyAsync(wanted: true, Ct);

        _entry.Registered.Should().NotBeNull();
        _entry.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task WithoutTheAgentBesideTheApp_NothingIsRegistered()
    {
        await Autostart(Path.Combine(_folder, "nao-existe.exe"), running: false).ApplyAsync(wanted: true, Ct);

        _entry.Registered.Should().BeNull();
        _entry.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task NotWanted_TakesItOutOfTheLogon()
    {
        _entry.Registered = Executable();

        await Autostart(Executable(), running: true).ApplyAsync(wanted: false, Ct);

        _entry.Registered.Should().BeNull();
        _entry.Started.Should().BeEmpty();
    }

    [Fact]
    public async Task AFailure_IsLogged_NotThrown()
    {
        _entry.Failure = new UnauthorizedAccessException("política de grupo");

        await Autostart(Executable(), running: false).Invoking(autostart => autostart.ApplyAsync(wanted: true, Ct))
            .Should().NotThrowAsync();
    }

    [Fact]
    public async Task TheSystemdUnit_RunsTheAgentAndComesBackWhenItFalls()
    {
        var processes = new RecordingRunner();
        var entry = new SystemdUserLogonEntry(Path.Combine(_folder, "systemd", "user"), processes);
        var executable = "/home/pessoa/.local/share/MyTaskApp/MyTaskApp.Agent";

        await entry.RegisterAsync(executable, Ct);
        await entry.StartAsync(executable, Ct);

        var unit = await File.ReadAllTextAsync(entry.UnitPath, Ct);
        unit.Should().Contain($"ExecStart=\"{executable}\"").And.Contain("Restart=on-failure").And.Contain("WantedBy=default.target");
        (await entry.IsRegisteredAsync(Ct)).Should().BeTrue();
        processes.Calls.Should().Equal(
            "systemctl --user daemon-reload",
            "systemctl --user enable mytaskapp-agent.service",
            "systemctl --user start mytaskapp-agent.service");

        await entry.UnregisterAsync(Ct);

        File.Exists(entry.UnitPath).Should().BeFalse();
        processes.Calls.Skip(3).Should().Equal(
            "systemctl --user disable mytaskapp-agent.service",
            "systemctl --user daemon-reload");
    }

    [Fact]
    public async Task ASystemctlThatFails_IsAFailure()
    {
        var processes = new RecordingRunner { ExitCode = 1 };
        var entry = new SystemdUserLogonEntry(Path.Combine(_folder, "systemd", "user"), processes);

        await entry.Invoking(unit => unit.RegisterAsync("/opt/agent", Ct)).Should().ThrowAsync<InvalidOperationException>();
    }

    /// <summary>A chave de verdade, numa subchave descartável: nunca a <c>Run</c> do usuário.</summary>
    [Fact]
    public async Task TheRunKey_KeepsTheQuotedPath()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Só o Windows tem a chave Run.");
            return;
        }

        await RunKeyRoundTripAsync();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static async Task RunKeyRoundTripAsync()
    {
        var subKey = $@"Software\MyTaskApp.Tests\{Guid.NewGuid():N}";
        var entry = new RunKeyLogonEntry(subKey, AgentProcess.RunValueName);

        try
        {
            (await entry.IsRegisteredAsync(Ct)).Should().BeFalse();

            await entry.RegisterAsync(@"C:\Program Files\MyTaskApp\MyTaskApp.Agent.exe", Ct);

            using (var key = Registry.CurrentUser.OpenSubKey(subKey))
            {
                key!.GetValue(AgentProcess.RunValueName).Should().Be("\"C:\\Program Files\\MyTaskApp\\MyTaskApp.Agent.exe\"");
            }

            (await entry.IsRegisteredAsync(Ct)).Should().BeTrue();
            await entry.UnregisterAsync(Ct);
            (await entry.IsRegisteredAsync(Ct)).Should().BeFalse();
            await entry.UnregisterAsync(Ct);
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree($@"Software\MyTaskApp.Tests", throwOnMissingSubKey: false);
        }
    }

    private AgentAutostart Autostart(string executable, bool running) =>
        new(executable, _entry, () => running, NullLogger<AgentAutostart>.Instance);

    private string Executable()
    {
        var path = Path.Combine(_folder, AgentProcess.ExecutableName);
        File.WriteAllText(path, string.Empty);
        return path;
    }

    private sealed class FakeEntry : IAgentLogonEntry
    {
        public string? Registered { get; set; }

        public List<string> Started { get; } = [];

        public Exception? Failure { get; set; }

        public Task<bool> IsRegisteredAsync(CancellationToken cancellationToken) => Task.FromResult(Registered is not null);

        public Task RegisterAsync(string executable, CancellationToken cancellationToken)
        {
            if (Failure is not null)
            {
                throw Failure;
            }

            Registered = executable;
            return Task.CompletedTask;
        }

        public Task UnregisterAsync(CancellationToken cancellationToken)
        {
            Registered = null;
            return Task.CompletedTask;
        }

        public Task StartAsync(string executable, CancellationToken cancellationToken)
        {
            Started.Add(executable);
            return Task.CompletedTask;
        }
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        public List<string> Calls { get; } = [];

        public int ExitCode { get; init; }

        public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
        {
            Calls.Add(string.Join(' ', request.Arguments.Prepend(request.FileName)));
            return Task.FromResult(new ProcessResult(ExitCode, string.Empty, string.Empty, TimedOut: false));
        }
    }
}
