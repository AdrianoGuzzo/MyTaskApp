using MyTaskApp.Domain.Planning;

namespace MyTaskApp.Application.Configuration;

public sealed class ApplicationOptions
{
    public const string SectionName = "Application";

    /// <summary>Id IANA (ex.: "America/Sao_Paulo"). Vazio usa o fuso da máquina.</summary>
    public string? TimeZoneId { get; set; }

    public int NowWindowBeforeMinutes { get; set; } = 15;

    public int NowWindowAfterMinutes { get; set; } = 60;

    /// <summary>
    /// De quanto em quanto tempo o agendador procura lembretes vencidos. Botao
    /// de implantacao, e nao preferencia de usuario: por isso fica aqui e nao
    /// no banco.
    /// </summary>
    public int ReminderTickSeconds { get; set; } = 30;

    /// <summary>
    /// De quanto em quanto tempo a manutencao do ciclo de vida roda. Horas, e
    /// nao segundos: arquivar e esvaziar lixeira sao prazos medidos em dias, e
    /// varrer o banco a cada meio minuto para nao achar nada seria desperdicio.
    /// Como o primeiro tique e imediato, quem abre o app ja ve tudo em dia.
    /// </summary>
    public int LifecycleSweepMinutes { get; set; } = 360;

    /// <summary>
    /// De quanto em quanto tempo as sessões de agente são conferidas contra os
    /// processos do sistema (ADR-030). É só rede de segurança: o fim de cada
    /// processo já chega por evento, então não há por que ser curto.
    /// </summary>
    public int AgentSessionReconcileSeconds { get; set; } = 60;

    /// <summary>
    /// A porta local (só 127.0.0.1) em que os hooks do agente avisam o app
    /// (ADR-037). Fixa de propósito: o Claude que ficou aberto com o app
    /// fechado guarda esta URL e volta a ser ouvido quando o app reabre. Ocupada,
    /// vale qualquer livre; <c>0</c> = sempre qualquer livre.
    /// </summary>
    public int AgentEventsPort { get; set; } = 47831;

    /// <summary>Limites defensivos: um tique de 0 s fritaria o disco.</summary>
    public TimeSpan ToReminderTickPeriod() =>
        TimeSpan.FromSeconds(Math.Clamp(ReminderTickSeconds, 5, 3600));

    /// <summary>Entre um minuto e uma semana; fora disso e engano de digitacao.</summary>
    public TimeSpan ToLifecycleSweepPeriod() =>
        TimeSpan.FromMinutes(Math.Clamp(LifecycleSweepMinutes, 1, 10080));

    /// <summary>Entre 10 s e uma hora: menos que isso seria o polling agressivo que o evento evita.</summary>
    public TimeSpan ToAgentSessionReconcilePeriod() =>
        TimeSpan.FromSeconds(Math.Clamp(AgentSessionReconcileSeconds, 10, 3600));

    public NowWindow ToNowWindow() =>
        new(
            TimeSpan.FromMinutes(NowWindowBeforeMinutes),
            TimeSpan.FromMinutes(NowWindowAfterMinutes));
}
