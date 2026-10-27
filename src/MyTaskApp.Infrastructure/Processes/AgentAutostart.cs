using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using MyTaskApp.Application.Remote;

namespace MyTaskApp.Infrastructure.Processes;

/// <summary>O executável do agente e o mutex dele: contrato com o instalador e com o próprio agente.</summary>
public static class AgentProcess
{
    /// <summary>
    /// Sem <c>Global\</c>: um agente por sessão. O instalador procura por este
    /// nome antes de trocar os arquivos, como faz com o do app (ADR-049).
    /// </summary>
    public const string MutexName = "MyTaskApp.Agent";

    /// <summary>O mesmo nome de valor na chave <c>Run</c> que o <c>.iss</c> apaga ao desinstalar.</summary>
    public const string RunValueName = "MyTaskApp.Agent";

    /// <summary>A unit do <c>systemd --user</c> que o <c>uninstall.sh</c> remove.</summary>
    public const string SystemdUnitName = "mytaskapp-agent.service";

    public static string ExecutableName => OperatingSystem.IsWindows() ? "MyTaskApp.Agent.exe" : "MyTaskApp.Agent";

    /// <summary>Ao lado do app: o instalador publica os dois na mesma pasta.</summary>
    public static string ExecutablePath(string folder) => Path.Combine(folder, ExecutableName);

    /// <summary>Um agente desta sessão segura o mutex.</summary>
    public static bool IsRunning()
    {
        if (Mutex.TryOpenExisting(MutexName, out var mutex))
        {
            mutex.Dispose();
            return true;
        }

        return false;
    }
}

/// <summary>Onde o sistema guarda "abra isto no logon".</summary>
internal interface IAgentLogonEntry
{
    Task<bool> IsRegisteredAsync(CancellationToken cancellationToken);

    Task RegisterAsync(string executable, CancellationToken cancellationToken);

    Task UnregisterAsync(CancellationToken cancellationToken);

    /// <summary>Sobe agora, sem esperar o próximo logon.</summary>
    Task StartAsync(string executable, CancellationToken cancellationToken);
}

/// <inheritdoc cref="IAgentAutostart"/>
internal sealed class AgentAutostart(
    string executable,
    IAgentLogonEntry entry,
    Func<bool> isRunning,
    ILogger<AgentAutostart> logger) : IAgentAutostart
{
    public async Task ApplyAsync(bool wanted, CancellationToken cancellationToken = default)
    {
        try
        {
            if (!wanted)
            {
                if (await entry.IsRegisteredAsync(cancellationToken))
                {
                    await entry.UnregisterAsync(cancellationToken);
                    logger.LogInformation("AgentAutostartRemoved");
                }

                return;
            }

            if (!File.Exists(executable))
            {
                // Uma build de desenvolvimento só do desktop: não há agente ao lado.
                logger.LogWarning("AgentExecutableMissing");
                return;
            }

            // Reescreve sempre: uma instalação em outra pasta deixaria o caminho velho.
            await entry.RegisterAsync(executable, cancellationToken);

            if (!isRunning())
            {
                await entry.StartAsync(executable, cancellationToken);
                logger.LogInformation("AgentLaunched");
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "AgentAutostartFailed {Wanted}", wanted);
        }
    }
}

/// <summary>A chave <c>Run</c> do usuário (HKCU), como o app faz para si (ADR-023).</summary>
[SupportedOSPlatform("windows")]
internal sealed class RunKeyLogonEntry(string subKey, string valueName) : IAgentLogonEntry
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static RunKeyLogonEntry ForCurrentUser() => new(RunKey, AgentProcess.RunValueName);

    public Task<bool> IsRegisteredAsync(CancellationToken cancellationToken)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey);
        return Task.FromResult(key?.GetValue(valueName) is string { Length: > 0 });
    }

    public Task RegisterAsync(string executable, CancellationToken cancellationToken)
    {
        using var key = Registry.CurrentUser.CreateSubKey(subKey, writable: true)
            ?? throw new SecurityException("A chave Run do usuário não abriu para escrita.");

        // Entre aspas: o caminho de instalação tem espaços.
        key.SetValue(valueName, $"\"{executable}\"", RegistryValueKind.String);
        return Task.CompletedTask;
    }

    public Task UnregisterAsync(CancellationToken cancellationToken)
    {
        using var key = Registry.CurrentUser.OpenSubKey(subKey, writable: true);
        key?.DeleteValue(valueName, throwOnMissingValue: false);
        return Task.CompletedTask;
    }

    /// <summary>Solto: o agente não é filho do desktop e continua quando ele fecha.</summary>
    public Task StartAsync(string executable, CancellationToken cancellationToken)
    {
        using var process = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(executable)!,
        });

        return Task.CompletedTask;
    }
}

/// <summary>
/// Uma unit do <c>systemd --user</c>: sobe no login da conta e volta se cair.
/// Ficar no ar com a sessão fechada é o <c>loginctl enable-linger</c>, que é
/// escolha de quem administra a máquina e só aparece na documentação.
/// </summary>
internal sealed class SystemdUserLogonEntry(string unitFolder, IProcessRunner processes) : IAgentLogonEntry
{
    private static readonly TimeSpan SystemctlTimeout = TimeSpan.FromSeconds(15);

    public static SystemdUserLogonEntry ForCurrentUser(IProcessRunner processes)
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
            ? xdg
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");

        return new SystemdUserLogonEntry(Path.Combine(config, "systemd", "user"), processes);
    }

    public string UnitPath => Path.Combine(unitFolder, AgentProcess.SystemdUnitName);

    public static string UnitFor(string executable) =>
        $"""
         [Unit]
         Description=MyTaskApp — agente da orquestração remota

         [Service]
         ExecStart="{executable}"
         WorkingDirectory={Path.GetDirectoryName(executable)}
         Restart=on-failure
         RestartSec=10

         [Install]
         WantedBy=default.target

         """;

    public Task<bool> IsRegisteredAsync(CancellationToken cancellationToken) => Task.FromResult(File.Exists(UnitPath));

    public async Task RegisterAsync(string executable, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(unitFolder);
        await File.WriteAllTextAsync(UnitPath, UnitFor(executable), cancellationToken);

        await SystemctlAsync(["daemon-reload"], cancellationToken);
        await SystemctlAsync(["enable", AgentProcess.SystemdUnitName], cancellationToken);
    }

    public async Task UnregisterAsync(CancellationToken cancellationToken)
    {
        await SystemctlAsync(["disable", AgentProcess.SystemdUnitName], cancellationToken);
        File.Delete(UnitPath);
        await SystemctlAsync(["daemon-reload"], cancellationToken);
    }

    /// <summary>Pelo systemd, e não solto: é ele quem reergue o agente se cair.</summary>
    public Task StartAsync(string executable, CancellationToken cancellationToken) =>
        SystemctlAsync(["start", AgentProcess.SystemdUnitName], cancellationToken);

    private async Task SystemctlAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var result = await processes.RunAsync(new ProcessRequest("systemctl", ["--user", .. arguments], SystemctlTimeout), cancellationToken);

        if (result.ExitCode != 0 || result.TimedOut)
        {
            throw new InvalidOperationException($"systemctl --user {string.Join(' ', arguments)} terminou com {result.ExitCode}.");
        }
    }
}
