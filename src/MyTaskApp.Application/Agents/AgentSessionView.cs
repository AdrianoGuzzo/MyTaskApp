using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Application.Agents;

/// <summary>A sessão como a tela a mostra (ADR-030).</summary>
public sealed record AgentSessionView(
    Guid SessionId,
    Guid TaskId,
    string ProviderId,
    string ProviderName,
    string Command,
    string WorkingDirectory,
    int? ProcessId,
    DateTimeOffset StartedAt,
    DateTimeOffset? EndedAt,
    AgentSessionStatus Status,
    string? FailureReason,
    bool IsMonitored = false,
    AgentActivity Activity = AgentActivity.Unknown,
    string? ActivityMessage = null,
    DateTimeOffset? ActivityChangedAt = null)
{
    public bool IsActive => Status is AgentSessionStatus.Starting or AgentSessionStatus.Running;

    /// <summary>
    /// Por que a sessão que acabou de abrir ficou sem acompanhamento (ADR-037).
    /// Só na resposta do "Iniciar": não é gravado.
    /// </summary>
    public string? MonitoringNote { get; init; }

    public static AgentSessionView From(AgentSession session, IAgentCliProviders providers) =>
        new(
            session.Id,
            session.TaskItemId,
            session.ProviderId,
            providers.Find(session.ProviderId)?.Name ?? session.ProviderId,
            session.Command,
            session.WorkingDirectory,
            session.ProcessId,
            session.StartedAt,
            session.EndedAt,
            session.Status,
            session.FailureReason,
            session.IsMonitored,
            session.Activity,
            session.ActivityMessage,
            session.ActivityChangedAt);
}

/// <summary>
/// O agente, se está instalado, como instalar quando não está, e os parâmetros
/// com que ele abre agora (o salvo, ou o padrão do agente) — e se abre
/// acompanhado pelos hooks (ADR-037).
/// </summary>
public sealed record AgentCliStatus(
    string ProviderId,
    string Name,
    string Command,
    CliDetectionResult Detection,
    AgentCliInstallGuide? InstallGuide,
    string Arguments = "",
    bool Monitor = true);

/// <summary>O que "Abrir terminal" conseguiu.</summary>
public sealed record AgentFocusResult(AgentSessionView Session, bool Focused);
