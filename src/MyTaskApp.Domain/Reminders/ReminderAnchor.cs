namespace MyTaskApp.Domain.Reminders;

/// <summary>A partir de quando o atraso do lembrete é contado.</summary>
public enum ReminderAnchor
{
    /// <summary>"Lembrar 1 hora depois de criar" — o padrão.</summary>
    AfterCreation = 0,

    /// <summary>
    /// "Lembrar 10 minutos antes das 09:00". Com folga zero significa
    /// exatamente no horário agendado. Uma ocorrência sem hora não tem
    /// "antes de quê" e simplesmente não arma.
    /// </summary>
    BeforeScheduledTime = 1,
}
