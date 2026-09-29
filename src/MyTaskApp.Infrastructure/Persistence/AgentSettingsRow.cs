namespace MyTaskApp.Infrastructure.Persistence;

/// <summary>
/// Os parâmetros de um agente, uma linha por provider (ADR-030: o catálogo já
/// prevê outros agentes além do Claude Code). Sem linha, vale o padrão do agente.
/// </summary>
internal sealed class AgentSettingsRow
{
    public string ProviderId { get; set; } = string.Empty;

    /// <summary><c>null</c> = nunca salvo: vale o padrão do agente (ADR-033).</summary>
    public string? Arguments { get; set; }

    /// <summary><c>null</c> = nunca escolhido: o acompanhamento nasce ligado (ADR-037).</summary>
    public bool? MonitorActivity { get; set; }

    /// <summary><c>null</c> ou vazio = o modelo padrão do agente (ADR-040).</summary>
    public string? Model { get; set; }

    /// <summary><c>null</c> ou vazio = o esforço padrão do agente (ADR-040).</summary>
    public string? Effort { get; set; }
}
