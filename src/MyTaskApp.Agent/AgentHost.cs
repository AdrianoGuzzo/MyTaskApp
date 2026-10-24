using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;

namespace MyTaskApp.Agent;

/// <summary>
/// Os laços do agente (ADR-085): a sincronização, que só cicla no dono do
/// lease, e o sinal de vida da máquina. Lembretes, bandeja e post-its são só do
/// desktop, e não sobem aqui.
/// </summary>
internal sealed class AgentHost(
    SyncScheduler sync,
    RemotePresenceScheduler presence,
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

        await presence.DisposeAsync();
        await sync.DisposeAsync();

        logger.LogInformation("AgentStopped");
    }
}
