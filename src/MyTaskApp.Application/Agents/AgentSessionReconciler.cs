using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Application.Agents;

/// <summary>
/// Confere uma sessão gravada contra o sistema (ADR-029): o estado do banco é
/// só o que era verdade da última vez. Antes de mostrar "Em execução", o
/// processo precisa existir — e ser o mesmo.
/// </summary>
public static class AgentSessionReconciler
{
    /// <summary>
    /// Quanto tempo uma sessão pode ficar em <see cref="AgentSessionStatus.Starting"/>
    /// sem PID antes de ser dada como perdida. Abrir o terminal leva milissegundos;
    /// a folga existe para a reconciliação periódica não encerrar uma sessão que
    /// outra operação está, neste instante, terminando de abrir.
    /// </summary>
    public static readonly TimeSpan StartingGrace = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Encerra a sessão se o processo dela não existe mais. Devolve <c>true</c>
    /// quando mudou algo — quem chamou grava.
    /// </summary>
    public static bool EndIfGone(AgentSession session, IAgentProcessTracker processes, DateTimeOffset now)
    {
        if (!session.IsActive)
        {
            return false;
        }

        if (session is { ProcessId: { } processId, ProcessStartedAt: { } processStartedAt })
        {
            if (processes.IsAlive(processId, processStartedAt))
            {
                return false;
            }
        }
        else if (now - session.StartedAt < StartingGrace)
        {
            return false;
        }

        session.MarkExited(now);
        return true;
    }
}
