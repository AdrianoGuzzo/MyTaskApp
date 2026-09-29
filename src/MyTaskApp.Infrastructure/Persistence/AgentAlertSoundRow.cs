using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Infrastructure.Persistence;

/// <summary>
/// O som de um estado do agente, uma linha por estado (ADR-042). Sem linha,
/// vale o de fábrica.
/// </summary>
internal sealed class AgentAlertSoundRow
{
    public AgentActivity Activity { get; set; }

    public bool IsEnabled { get; set; }

    /// <summary><c>builtin:…</c> ou <c>custom:…</c> — o id da biblioteca de sons.</summary>
    public string SoundId { get; set; } = string.Empty;
}
