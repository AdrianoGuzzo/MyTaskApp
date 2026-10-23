namespace MyTaskApp.Remote.Contracts;

/// <summary>
/// Os tetos do protocolo, os mesmos no servidor e no agente
/// (docs/architecture/remote-orchestration.md, seção 3.10).
/// </summary>
public static class RemoteLimits
{
    /// <summary>A versão deste contrato, anunciada em <see cref="DeviceCapabilities.ProtocolVersion"/>.</summary>
    public const int ProtocolVersion = 1;

    /// <summary>Quanto uma operação espera ser aceita, se o controlador não disser.</summary>
    public static readonly TimeSpan DefaultAcceptanceWindow = TimeSpan.FromMinutes(10);

    /// <summary>
    /// O máximo entre criar e expirar: máquina desligada não roda amanhã o que
    /// foi pedido hoje.
    /// </summary>
    public static readonly TimeSpan MaxAcceptanceWindow = TimeSpan.FromHours(24);

    /// <summary>Quanto o agente lembra os ids já vistos: mais que qualquer <see cref="MaxAcceptanceWindow"/>.</summary>
    public static readonly TimeSpan ReplayWindow = TimeSpan.FromDays(30);

    /// <summary>Sem resposta até aqui, a aprovação vale "negar".</summary>
    public static readonly TimeSpan DefaultApprovalLifetime = TimeSpan.FromMinutes(10);

    /// <summary>O mesmo teto do prompt guardado no ambiente (<c>TaskDevelopment.AgentPrompt</c>).</summary>
    public const int MaxPromptLength = 8000;

    /// <summary>Texto de um evento, já mascarado. Uma linha maior é cortada e marcada.</summary>
    public const int MaxEventTextLength = 8 * 1024;

    public const int MaxEventsPerBatch = 200;
}
