namespace MyTaskApp.Domain.Deadlines;

/// <summary>
/// O que já foi avisado sobre o prazo de <b>uma ocorrência</b>. Guarda o
/// passado, não o futuro: o próximo aviso é função pura da política, do prazo
/// e de agora (<see cref="DeadlineAlerting.Decide"/>), e por isso não é coluna
/// — mudar a configuração vale na hora, sem rearmar nada (ADR-050).
/// </summary>
public sealed class DeadlineAlertState
{
    // Interno, e não privado, porque a ocorrência precisa criar o seu. O EF
    // materializa por este mesmo construtor.
    internal DeadlineAlertState()
    {
    }

    /// <summary>
    /// O degrau mais grave já avisado. Também é o ponto de partida de um prazo
    /// recém-definido: o que já estava cruzado ao definir não vira aviso.
    /// </summary>
    public DeadlineAlertStage LastStage { get; private set; }

    /// <summary>Quando saiu o último aviso. Base da repetição do atraso.</summary>
    public DateTimeOffset? LastAlertAtUtc { get; private set; }

    /// <summary>Até quando o usuário pediu silêncio. Não mexe no prazo (§21).</summary>
    public DateTimeOffset? SnoozedUntilUtc { get; private set; }

    internal void MarkAlerted(DeadlineAlertStage stage, DateTimeOffset atUtc)
    {
        LastStage = stage;
        LastAlertAtUtc = atUtc;
        SnoozedUntilUtc = null;
    }

    internal void Snooze(DateTimeOffset untilUtc) => SnoozedUntilUtc = untilUtc;

    /// <summary>Prazo novo, contagem nova, a partir do degrau em que ele já nasce.</summary>
    internal void Reset(DeadlineAlertStage initialStage)
    {
        LastStage = initialStage;
        LastAlertAtUtc = null;
        SnoozedUntilUtc = null;
    }
}
