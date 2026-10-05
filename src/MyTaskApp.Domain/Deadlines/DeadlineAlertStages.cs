namespace MyTaskApp.Domain.Deadlines;

/// <summary>Quanto antes do prazo cada degrau acende.</summary>
public static class DeadlineAlertStages
{
    /// <summary>Do mais brando ao mais grave — a ordem dos valores do enum.</summary>
    public static IReadOnlyList<DeadlineAlertStage> Ordered { get; } =
    [
        DeadlineAlertStage.SevenDays,
        DeadlineAlertStage.ThreeDays,
        DeadlineAlertStage.OneDay,
        DeadlineAlertStage.EightHours,
        DeadlineAlertStage.TwoHours,
        DeadlineAlertStage.Overdue,
    ];

    /// <summary>
    /// "Só quando faltar 24 horas": o aviso da véspera e o do atraso. O preset
    /// do §20 para quem quer ser lembrado de uma tarefa sem ser cobrado por ela.
    /// </summary>
    public const DeadlineAlertStage OnlyTheDayBefore =
        DeadlineAlertStage.OneDay | DeadlineAlertStage.Overdue;

    /// <summary>Quanto antes do prazo o degrau acende. Atrasada acende no próprio prazo.</summary>
    public static TimeSpan LeadTimeOf(DeadlineAlertStage stage) => stage switch
    {
        DeadlineAlertStage.SevenDays => TimeSpan.FromDays(7),
        DeadlineAlertStage.ThreeDays => TimeSpan.FromDays(3),
        DeadlineAlertStage.OneDay => TimeSpan.FromDays(1),
        DeadlineAlertStage.EightHours => TimeSpan.FromHours(8),
        DeadlineAlertStage.TwoHours => TimeSpan.FromHours(2),
        DeadlineAlertStage.Overdue => TimeSpan.Zero,
        _ => throw new ArgumentOutOfRangeException(nameof(stage), stage, "Degrau de prazo desconhecido."),
    };

    /// <summary>Um conjunto só com degraus que existem.</summary>
    public static bool IsValidSet(DeadlineAlertStage stages) =>
        (stages & ~DeadlineAlertStage.All) == 0;
}
