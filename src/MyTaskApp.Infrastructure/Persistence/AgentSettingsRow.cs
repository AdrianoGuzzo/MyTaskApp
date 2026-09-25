namespace MyTaskApp.Infrastructure.Persistence;

/// <summary>
/// Os parâmetros de um agente, uma linha por provider (ADR-030: o catálogo já
/// prevê outros agentes além do Claude Code). Sem linha, vale o padrão do agente.
/// </summary>
internal sealed class AgentSettingsRow
{
    public string ProviderId { get; set; } = string.Empty;

    public string Arguments { get; set; } = string.Empty;
}
