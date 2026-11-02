using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.LocalPeers;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Infrastructure.Terminals;

namespace MyTaskApp.Agent;

/// <summary>
/// Os laços do agente (ADR-085): a sincronização, que só cicla no dono do
/// lease, o sinal de vida da máquina, o canal local até o desktop e a fila de
/// operações remotas, que só o agente atende (T-15). Lembretes, bandeja e
/// post-its são só do desktop, e não sobem aqui. E o terminal do agente
/// (ADR-098): a porta dos hooks das sessões dele e o canal local por onde a
/// janela nativa e o app seguem cada sessão.
/// </summary>
internal sealed class AgentHost(
    SyncScheduler sync,
    RemotePresenceScheduler presence,
    LocalPeerBridge peer,
    RemoteOperationScheduler operations,
    IAgentEventEndpoint events,
    TerminalPipeListener terminalPipe,
    TerminalSessionRegistry terminals,
    LocalPeerAttentionForwarder attention,
    ILogger<AgentHost> logger)
{
    /// <summary>A mesma folga do desktop para o que está em andamento terminar.</summary>
    public static readonly TimeSpan ShutdownGrace = TimeSpan.FromSeconds(10);

    public bool IsRunning { get; private set; }

    public void Start()
    {
        if (IsRunning)
        {
            return;
        }

        IsRunning = true;
        sync.Start();
        presence.Start();
        peer.Start();
        attention.Start();
        events.Start();
        terminalPipe.Start();
        operations.Start();

        logger.LogInformation("AgentStarted");
    }

    /// <summary>Para de aceitar, deixa o que roda terminar dentro da folga, e solta tudo.</summary>
    public async Task StopAsync()
    {
        if (!IsRunning)
        {
            return;
        }

        IsRunning = false;

        // A fila primeiro: o que roda ainda conta o fim ao servidor, pelo sync e pela rede que seguem de pé.
        await operations.DisposeAsync();

        // Os terminais antes do canal: cada Claude recebe o fim do terminal e sai salvando a conversa.
        await terminalPipe.DisposeAsync();
        await terminals.DisposeAsync();
        attention.Dispose();

        if (events is IAsyncDisposable listener)
        {
            await listener.DisposeAsync();
        }

        await peer.DisposeAsync();
        await presence.DisposeAsync();
        await sync.DisposeAsync();

        logger.LogInformation("AgentStopped");
    }
}
