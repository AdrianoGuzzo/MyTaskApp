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
    string? FailureReason)
{
    public bool IsActive => Status is AgentSessionStatus.Starting or AgentSessionStatus.Running;

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
            session.FailureReason);
}

/// <summary>
/// O agente, se está instalado, como instalar quando não está, e os parâmetros
/// com que ele abre agora (o salvo, ou o padrão do agente).
/// </summary>
public sealed record AgentCliStatus(
    string ProviderId,
    string Name,
    string Command,
    CliDetectionResult Detection,
    AgentCliInstallGuide? InstallGuide,
    string Arguments = "");

/// <summary>O que "Abrir terminal" conseguiu.</summary>
public sealed record AgentFocusResult(AgentSessionView Session, bool Focused);
