namespace MyTaskApp.Domain.Deadlines;

/// <summary>
/// A política global dos alertas de prazo: quais degraus avisam e de quanto em
/// quanto o atraso volta a avisar. Não guarda estado — o que já foi avisado é
/// da ocorrência (<see cref="DeadlineAlertState"/>).
/// </summary>
/// <remarks>
/// Ao contrário do <c>ReminderPolicy</c>, que é copiado para a tarefa na
/// criação (ADR-014), esta é lida <b>viva</b> a cada tique: a tarefa em
/// "Padrão" segue o que a configuração disser agora. Por isso desligar não
/// apaga os degraus escolhidos — ligar de novo devolve o que estava.
/// </remarks>
public sealed record DeadlineAlertPolicy
{
    public static readonly TimeSpan MinOverdueRepeat = TimeSpan.FromHours(1);
    public static readonly TimeSpan MaxOverdueRepeat = TimeSpan.FromDays(7);

    public DeadlineAlertPolicy(
        bool IsEnabled,
        DeadlineAlertStage Stages,
        TimeSpan? OverdueRepeatEvery)
    {
        if (!DeadlineAlertStages.IsValidSet(Stages))
        {
            throw new DomainException("Esse aviso de prazo não existe.");
        }

        if (IsEnabled && Stages is DeadlineAlertStage.None)
        {
            throw new DomainException("Escolha pelo menos um aviso de prazo, ou desative os alertas.");
        }

        if (OverdueRepeatEvery is { } every && (every < MinOverdueRepeat || every > MaxOverdueRepeat))
        {
            throw new DomainException(
                "O alerta de atraso pode repetir de 1 hora em 1 hora até de 7 em 7 dias.");
        }

        this.IsEnabled = IsEnabled;
        this.Stages = Stages;

        // Sem o degrau de atraso não há o que repetir; zerar mantém a igualdade honesta.
        this.OverdueRepeatEvery = Stages.HasFlag(DeadlineAlertStage.Overdue) ? OverdueRepeatEvery : null;
    }

    /// <summary>
    /// O padrão de fábrica (§4): nada antes da véspera, e daí 24 h, 8 h, 2 h e
    /// o atraso, que volta uma vez por dia enquanto a tarefa estiver aberta.
    /// </summary>
    public static DeadlineAlertPolicy Default { get; } = new(
        IsEnabled: true,
        DeadlineAlertStage.OneDay | DeadlineAlertStage.EightHours
            | DeadlineAlertStage.TwoHours | DeadlineAlertStage.Overdue,
        TimeSpan.FromDays(1));

    public bool IsEnabled { get; }

    public DeadlineAlertStage Stages { get; }

    /// <summary><c>null</c> = o atraso avisa uma vez só.</summary>
    public TimeSpan? OverdueRepeatEvery { get; }

    /// <summary>
    /// Os degraus que valem para uma tarefa. A escolha da tarefa vence a global
    /// — inclusive a global desligada: quem marcou uma tarefa crítica como
    /// "personalizado" pediu para ser avisado dela (§20).
    /// </summary>
    /// <param name="taskOverride"><c>null</c> = a tarefa segue o padrão.</param>
    public DeadlineAlertStage StagesFor(DeadlineAlertStage? taskOverride) =>
        taskOverride ?? (IsEnabled ? Stages : DeadlineAlertStage.None);
}
