using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;

namespace MyTaskApp.Infrastructure.Terminals;

/// <summary>
/// O terminal em sistemas que ainda não têm implementação (ADR-030): hoje, o
/// macOS. Existe para o app subir e dizer "ainda não", em vez de falhar ao
/// montar o contêiner. O Linux já tem o dele (<c>LinuxTerminalLauncher</c>); o
/// gerenciador de janelas daqui segue valendo lá.
/// </summary>
internal sealed class UnsupportedTerminalLauncher : ITerminalLauncher
{
    public Task<TerminalLaunchResult> LaunchAsync(
        TerminalLaunchOptions options,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(TerminalLaunchResult.Failed(
            "Abrir um terminal pelo app ainda não é suportado neste sistema."));
}

internal sealed class UnsupportedTerminalWindowManager : ITerminalWindowManager
{
    public Task<bool> FocusAsync(int processId) => Task.FromResult(false);

    public Task<bool> IsInForegroundAsync(int processId) => Task.FromResult(false);

    public Task<bool> FlashAsync(int processId) => Task.FromResult(false);

    public Task StopFlashingAsync(int processId) => Task.CompletedTask;
}

/// <summary>Sem pseudoterminal (hoje, o macOS): o terminal do agente diz "ainda não" (ADR-098).</summary>
internal sealed class UnsupportedPseudoTerminalFactory : IPseudoTerminalFactory
{
    public PseudoTerminalSupport Support { get; } = PseudoTerminalSupport.Unsupported(
        "este sistema",
        "O terminal do agente ainda não é suportado neste sistema. Use o terminal direto.");

    public IPseudoTerminal Start(PseudoTerminalStart start) => throw new DomainException(Support.Reason!);
}
