using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Terminals;

/// <summary>
/// O desktop falando com o agente desta máquina pelo canal local do terminal
/// (ADR-098). Se o agente não está rodando, sobe ele: abrir o Claude no
/// terminal do agente não depende de "Aceitar ser controlada".
/// </summary>
public sealed partial class LocalAgentTerminalClient(
    string pipeName,
    string appFolder,
    IPseudoTerminalFactory terminals,
    ILogger<LocalAgentTerminalClient> logger) : IAgentTerminalClient
{
    /// <summary>Quanto esperar o agente recém-aberto atender: ele migra o banco antes.</summary>
    public static readonly TimeSpan AgentStartPatience = TimeSpan.FromSeconds(20);

    public static string WindowExecutableName => OperatingSystem.IsWindows() ? "MyTaskApp.TerminalAttach.exe" : "MyTaskApp.TerminalAttach";

    private string WindowExecutable => Path.Combine(appFolder, WindowExecutableName);

    private string AgentExecutable => AgentProcess.ExecutablePath(appFolder);

    public PseudoTerminalSupport Support
    {
        get
        {
            if (!terminals.Support.IsSupported)
            {
                return terminals.Support;
            }

            // Uma build de desenvolvimento só do desktop não tem o agente nem a janela ao lado.
            return File.Exists(AgentExecutable) && File.Exists(WindowExecutable)
                ? terminals.Support
                : PseudoTerminalSupport.Unsupported(
                    terminals.Support.Platform,
                    "O MyTaskApp Agent ou a janela do terminal não estão instalados ao lado do app.");
        }
    }

    public async Task<TerminalStartResult> StartAsync(TerminalStartRequest request, CancellationToken cancellationToken = default)
    {
        var stream = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            return await LocalTerminalChannel.StartAsync(stream, request, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<ITerminalChannel> AttachAsync(Guid sessionId, TerminalClientKind kind, string label, TerminalSize? size, CancellationToken cancellationToken = default)
    {
        var stream = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        return await LocalTerminalChannel.AttachAsync(stream, sessionId, kind, label, size, cancellationToken).ConfigureAwait(false);
    }

    public TerminalLaunchOptions WindowFor(Guid sessionId, string title) =>
        new(WindowExecutable, ["--pipe", pipeName, "--session", sessionId.ToString(), "--title", title], appFolder);

    private async Task<Stream> ConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await TerminalPipe.ConnectAsync(pipeName, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException) when (!AgentProcess.IsRunning() && File.Exists(AgentExecutable))
        {
            StartAgent();
        }

        var deadline = DateTimeOffset.UtcNow + AgentStartPatience;

        while (true)
        {
            try
            {
                return await TerminalPipe.ConnectAsync(pipeName, cancellationToken).ConfigureAwait(false);
            }
            catch (DomainException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Solto, como o autostart faz: o agente não é filho do desktop e continua quando ele fecha.</summary>
    private void StartAgent()
    {
        using var process = Process.Start(new ProcessStartInfo(AgentExecutable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = appFolder,
        });

        LogAgentStarted(process?.Id);
    }

    [LoggerMessage(EventId = 9851, Level = LogLevel.Information, Message = "AgentStartedForTerminal {ProcessId}")]
    private partial void LogAgentStarted(int? processId);
}
