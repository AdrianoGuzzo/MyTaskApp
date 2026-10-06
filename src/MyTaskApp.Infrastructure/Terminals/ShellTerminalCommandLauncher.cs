using MyTaskApp.Application.Agents;
using MyTaskApp.Application.QuickCommands;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Terminals;

/// <summary>
/// Um comando rápido num terminal visível (ADR-051): o shell do sistema
/// (<see cref="ShellCommandPlanner.ForTerminal"/>) aberto pelo mesmo lançador do
/// agente (ADR-030), que devolve o PID e o início do processo.
/// </summary>
/// <remarks>
/// O PID é o do shell, e não o do <c>dotnet run</c>: é ele que vive enquanto a
/// janela está aberta. Fora do Windows o lançador é o "ainda não suportado", e
/// a resposta vem dele.
/// </remarks>
internal sealed class ShellTerminalCommandLauncher(
    ITerminalLauncher terminals,
    bool isWindows,
    Func<string, string?> environment,
    string systemDirectory) : ITerminalCommandLauncher
{
    public ShellTerminalCommandLauncher(ITerminalLauncher terminals)
        : this(terminals, OperatingSystem.IsWindows(), Environment.GetEnvironmentVariable, Environment.SystemDirectory)
    {
    }

    public Task<TerminalLaunchResult> LaunchAsync(
        TerminalCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        var shell = ShellCommandPlanner.ForTerminal(
            isWindows,
            request.Command,
            request.KeepOpen,
            environment,
            systemDirectory);

        return terminals.LaunchAsync(
            new TerminalLaunchOptions(
                shell.FileName,
                shell.Arguments,
                request.WorkingDirectory,
                RawArguments: shell.RawArguments),
            cancellationToken);
    }
}
