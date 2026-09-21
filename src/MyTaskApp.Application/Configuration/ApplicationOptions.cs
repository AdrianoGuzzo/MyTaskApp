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

    /// <summary>Limites defensivos: um tique de 0 s fritaria o disco.</summary>
    public TimeSpan ToReminderTickPeriod() =>
        TimeSpan.FromSeconds(Math.Clamp(ReminderTickSeconds, 5, 3600));

    /// <summary>Entre um minuto e uma semana; fora disso e engano de digitacao.</summary>
    public TimeSpan ToLifecycleSweepPeriod() =>
        TimeSpan.FromMinutes(Math.Clamp(LifecycleSweepMinutes, 1, 10080));

    public NowWindow ToNowWindow() =>
        new(
            TimeSpan.FromMinutes(NowWindowBeforeMinutes),
            TimeSpan.FromMinutes(NowWindowAfterMinutes));
}
