using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Application.Agents;

/// <summary>
/// Confere uma sessão gravada contra o sistema (ADR-030): o estado do banco é
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
    /// <param name="startingGrace">
    /// A folga da sessão ainda sem PID; nula é <see cref="StartingGrace"/>. Zero
    /// só para quem sabe que ninguém está abrindo o processo agora (o agente,
    /// com a conversa nas mãos).
    /// </param>
    public static bool EndIfGone(AgentSession session, IAgentProcessTracker processes, DateTimeOffset now, TimeSpan? startingGrace = null)
    {
        if (!IsGone(session, processes, now, startingGrace))
        {
            return false;
        }

        session.MarkExited(now);
        return true;
    }

    /// <summary>A sessão diz estar ativa, mas o processo dela não existe mais (ou nunca chegou a abrir, passada a folga).</summary>
    public static bool IsGone(AgentSession session, IAgentProcessTracker processes, DateTimeOffset now, TimeSpan? startingGrace = null)
    {
        if (!session.IsActive)
        {
            return false;
        }

        if (session is { ProcessId: { } processId, ProcessStartedAt: { } processStartedAt })
        {
            return !processes.IsAlive(processId, processStartedAt);
        }

        return now - (session.PromptStartedAt ?? session.StartedAt) >= (startingGrace ?? StartingGrace);
    }
}
