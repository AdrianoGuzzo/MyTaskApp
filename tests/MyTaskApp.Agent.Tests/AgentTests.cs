using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.LocalPeers;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Agent.Tests;

/// <summary>
/// O agente (ADR-085, T-10): a composição inteira se resolve sem tela, o host
/// sobe e para sem o desktop, e o executável de verdade roda como processo
/// próprio, um por sessão.
/// </summary>
public sealed class AgentTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task EveryRegistration_Resolves_WithoutAnyScreen()
    {
        var folder = Path.Combine(Path.GetTempPath(), $"mytaskapp-agent-{Guid.NewGuid():N}");

        await using var services = AgentTestHost.Compose(folder)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        services.GetRequiredService<AgentHost>().Should().NotBeNull();
        services.GetRequiredService<IProcessLeases>().Should().NotBeNull();
    }

    /// <summary>Sem tela (ADR-085): o agente é a Application e a Infrastructure, e nada das cascas.</summary>
    [Fact]
    public void TheAgent_KnowsNoScreen()
    {
        typeof(AgentHost).Assembly.GetReferencedAssemblies().Select(name => name.Name!)
            .Should().NotContain(name => name.StartsWith("Avalonia", StringComparison.Ordinal)
                || name == "MyTaskApp"
                || name.StartsWith("MyTaskApp.Mobile", StringComparison.Ordinal)
                || name.StartsWith("Microsoft.Maui", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheAgent_RunsAndStops_WithTheDesktopClosed()
    {
        await using var host = AgentTestHost.Create();
        using var stop = new CancellationTokenSource();

        var running = Program.RunAsync(host.Services, stop.Token);
        await WaitUntilAsync(() => host.Services.GetRequiredService<AgentHost>().IsRunning);
        await stop.CancelAsync();

        (await running.WaitAsync(TimeSpan.FromSeconds(30), Ct)).Should().Be(0);
        host.Services.GetRequiredService<AgentHost>().IsRunning.Should().BeFalse();
        File.Exists(Path.Combine(host.Folder, "mytaskapp.db")).Should().BeTrue("o agente cria ou migra o banco, como o desktop");
    }

    [Fact]
    public async Task WithoutAnAccount_TheSyncLoopStaysIdle()
    {
        await using var host = AgentTestHost.Create();
        using var stop = new CancellationTokenSource();
        var scheduler = host.Services.GetRequiredService<SyncScheduler>();

        var running = Program.RunAsync(host.Services, stop.Token);
        await WaitUntilAsync(() => scheduler.LastResult is not null);
        await stop.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);

        scheduler.LastResult!.Status.Should().Be(SyncCycleStatus.Idle);
    }

    /// <summary>T-11: o desktop conecta no canal que o agente abriu, e os avisos atravessam nos dois sentidos.</summary>
    [Fact]
    public async Task TheDesktop_ConnectsToTheAgent_AndNoticesCrossBothWays()
    {
        await using var host = AgentTestHost.Create();
        using var stop = new CancellationTokenSource();
        var agentNotifier = host.Services.GetRequiredService<IDataChangeNotifier>();
        DataArea? heardByAgent = null;
        agentNotifier.Changed += areas => heardByAgent = areas;

        var running = Program.RunAsync(host.Services, stop.Token);
        await WaitUntilAsync(() => host.Services.GetRequiredService<AgentHost>().IsRunning);

        await using var desktop = new PipeLocalPeerChannel(
            PipeLocalPeerChannel.NameFor(Path.Combine(host.Folder, "mytaskapp.db")),
            LocalPeerRole.Connect,
            NullLogger<PipeLocalPeerChannel>.Instance,
            TimeSpan.FromMilliseconds(100));
        LocalPeerMessage? heardByDesktop = null;
        desktop.Received += message => heardByDesktop = message;
        desktop.Start();
        await WaitUntilAsync(() => desktop.IsConnected && host.Services.GetRequiredService<ILocalPeerChannel>().IsConnected);

        await desktop.SendAsync(new LocalPeerMessage(LocalPeerMessageKinds.DataChanged, DataArea.Tasks), Ct);
        await WaitUntilAsync(() => heardByAgent is not null);
        heardByAgent.Should().Be(DataArea.Tasks);
        heardByDesktop.Should().BeNull("o que veio do desktop não volta para ele");

        agentNotifier.NotifyChanged(DataArea.Time);
        await WaitUntilAsync(() => heardByDesktop is not null);
        heardByDesktop.Should().Be(new LocalPeerMessage(LocalPeerMessageKinds.DataChanged, DataArea.Time));

        await stop.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(30), Ct);
    }

    [Fact]
    public async Task TheExecutable_IsItsOwnProcess_AndASecondOneLeavesAtOnce()
    {
        var data = Path.Combine(Path.GetTempPath(), $"mytaskapp-agent-exe-{Guid.NewGuid():N}");
        using var first = Launch(data);

        try
        {
            var log = Path.Combine(data, "logs");
            await WaitUntilAsync(
                () => Directory.Exists(log) && Directory.EnumerateFiles(log, "mytaskapp-agent-*.log").Any(file => ReadShared(file).Contains("AgentStarted")),
                TimeSpan.FromSeconds(60));

            using var second = Launch(data);
            await second.WaitForExitAsync(Ct).WaitAsync(TimeSpan.FromSeconds(30), Ct);

            second.ExitCode.Should().Be(0);
            first.HasExited.Should().BeFalse("o primeiro continua: o segundo só viu o mutex e saiu");
        }
        finally
        {
            first.Kill(entireProcessTree: true);
            await first.WaitForExitAsync(Ct);

            try
            {
                Directory.Delete(data, recursive: true);
            }
            catch (IOException)
            {
                // O antivírus ainda lendo o log.
            }
        }
    }

    private static Process Launch(string data)
    {
        var folder = AppContext.BaseDirectory;
        var apphost = Path.Combine(folder, OperatingSystem.IsWindows() ? "MyTaskApp.Agent.exe" : "MyTaskApp.Agent");
        var start = File.Exists(apphost)
            ? new ProcessStartInfo(apphost)
            : new ProcessStartInfo("dotnet") { ArgumentList = { Path.Combine(folder, "MyTaskApp.Agent.dll") } };

        start.UseShellExecute = false;
        start.CreateNoWindow = true;
        start.Environment["MYTASKAPP_DATA_DIR"] = data;

        return Process.Start(start)!;
    }

    private static string ReadShared(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(30));

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("A condição não aconteceu a tempo.");
            }

            await Task.Delay(50, Ct);
        }
    }
}
