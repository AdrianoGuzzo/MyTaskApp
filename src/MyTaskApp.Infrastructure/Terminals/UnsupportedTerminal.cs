using MyTaskApp.Application.Agents;

namespace MyTaskApp.Infrastructure.Terminals;

/// <summary>
/// O terminal em sistemas que ainda não têm implementação (ADR-030). Existe
/// para o app subir e dizer "ainda não", em vez de falhar ao montar o contêiner.
/// O Linux entra trocando esta classe, sem mexer no resto.
/// </summary>
internal sealed class UnsupportedTerminalLauncher : ITerminalLauncher
{
    public Task<TerminalLaunchResult> LaunchAsync(
        TerminalLaunchOptions options,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(TerminalLaunchResult.Failed(
            "Abrir o agente num terminal ainda não é suportado neste sistema."));
}

internal sealed class UnsupportedTerminalWindowManager : ITerminalWindowManager
{
    public Task<bool> FocusAsync(int processId) => Task.FromResult(false);
}
