using System.Globalization;

namespace MyTaskApp.Domain.Deadlines;

/// <summary>
/// O texto do prazo, num lugar só (§16). A linha, o HUD, o editor e os avisos
/// leem daqui: um "2 dias e 4 horas" escrito em dois lugares acaba virando
/// "52 horas" num deles.
/// </summary>
/// <remarks>
/// Em pt-BR, como as mensagens de <see cref="DomainException"/>. Os nomes dos
/// dias são fixos em vez de vir da cultura da máquina: a interface é pt-BR
/// mesmo num Windows em inglês.
/// </remarks>
public static class DeadlineFormatter
{
    /// <summary>Abaixo disto o tempo restante ganha minutos ("1h 42min").</summary>
    public static readonly TimeSpan MinutePrecisionBelow = TimeSpan.FromHours(2);

    private static readonly TimeSpan HoursDropAt = TimeSpan.FromDays(7);

    private static readonly string[] WeekdayNames =
        ["domingo", "segunda", "terça", "quarta", "quinta", "sexta", "sábado"];

    private static readonly string[] WeekdayAbbreviations =
        ["dom", "seg", "ter", "qua", "qui", "sex", "sáb"];

    /// <summary>"ter", "sáb": a mesma abreviação do prazo, para o histórico (ADR-053) não ter outra.</summary>
    public static string WeekdayAbbreviation(DateOnly date) => WeekdayAbbreviations[(int)date.DayOfWeek];

    /// <summary>
    /// Quanto falta, na unidade que se lê de relance: "8 dias", "2 dias e 4
    /// horas", "6 horas", "1h 42min", "48 minutos". Nunca "137 horas".
    /// </summary>
    public static string Remaining(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = span.Negate();
        }

        if (span < TimeSpan.FromMinutes(1))
        {
            return "menos de 1 minuto";
        }

        if (span < TimeSpan.FromHours(1))
        {
            return Count(span.Minutes, "minuto", "minutos");
        }

        if (span < MinutePrecisionBelow)
        {
            return span.Minutes == 0
                ? "1 hora"
                : string.Create(CultureInfo.InvariantCulture, $"{span.Hours}h {span.Minutes:00}min");
        }

        if (span < TimeSpan.FromDays(1))
        {
            return Count(span.Hours, "hora", "horas");
        }

        var days = Count(span.Days, "dia", "dias");

        return span >= HoursDropAt || span.Hours == 0
            ? days
            : $"{days} e {Count(span.Hours, "hora", "horas")}";
    }

    /// <summary>
    /// Há quanto tempo o prazo passou: "há 25 minutos", "há 3 horas", "há 2
    /// dias". Mais grosso que <see cref="Remaining"/> de propósito — atrasada há
    /// "2 dias e 3 horas" não diz nada que "há 2 dias" não diga.
    /// </summary>
    public static string Ago(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = span.Negate();
        }

        if (span < TimeSpan.FromMinutes(1))
        {
            return "agora mesmo";
        }

        return "há " + (span < TimeSpan.FromHours(1) ? Count(span.Minutes, "minuto", "minutos")
            : span < TimeSpan.FromDays(1) ? Count(span.Hours, "hora", "horas")
            : Count(span.Days, "dia", "dias"));
    }

    /// <summary>
    /// O momento do prazo, relativo a hoje: "hoje às 18:00", "amanhã às 18:00",
    /// "sexta às 18:00" (até seis dias), "09/10 às 18:00" (além disso, ou no
    /// passado).
    /// </summary>
    public static string Moment(TaskDeadline deadline, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(deadline);

        var days = deadline.Date.DayNumber - today.DayNumber;
        var time = Time(deadline.Time);

        return days switch
        {
            0 => $"hoje às {time}",
            1 => $"amanhã às {time}",
            > 1 and < 7 => $"{WeekdayNames[(int)deadline.Date.DayOfWeek]} às {time}",
            _ => $"{ShortDate(deadline.Date)} às {time}",
        };
    }

    /// <summary>A data por extenso curto, para o editor: "sex 09/10 18:00".</summary>
    public static string Date(TaskDeadline deadline)
    {
        ArgumentNullException.ThrowIfNull(deadline);

        return $"{WeekdayAbbreviations[(int)deadline.Date.DayOfWeek]} {ShortDate(deadline.Date)} {Time(deadline.Time)}";
    }

    /// <summary>
    /// A linha da tarefa (§5): curta, e com a severidade escrita além de
    /// pintada. "5 dias restantes", "ATENÇÃO · vence amanhã às 18:00",
    /// "URGENTE · vence em 1h 42min", "ATRASADA · há 3 horas".
    /// </summary>
    public static string RowLabel(DeadlineSnapshot snapshot, TaskDeadline deadline, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(deadline);

        return snapshot.Status switch
        {
            DeadlineStatus.Met => "concluída no prazo",
            DeadlineStatus.Missed => $"concluída {Remaining(snapshot.Remaining)} após o prazo",
            DeadlineStatus.Overdue => $"ATRASADA · {Ago(snapshot.Remaining)}",
            _ => snapshot.Severity switch
            {
                DeadlineSeverity.Urgent => $"URGENTE · {Due(snapshot, deadline, today)}",
                DeadlineSeverity.Attention => $"ATENÇÃO · {Due(snapshot, deadline, today)}",
                _ => $"{Remaining(snapshot.Remaining)} restantes",
            },
        };
    }

    /// <summary>
    /// O editor (§34): o tempo restante sem o prefixo de severidade, porque a
    /// data já está ao lado. "2 dias e 6 horas restantes", "atrasada há 3 horas".
    /// </summary>
    public static string Countdown(DeadlineSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return snapshot.Status switch
        {
            DeadlineStatus.Met => "concluída no prazo",
            DeadlineStatus.Missed => $"concluída {Remaining(snapshot.Remaining)} após o prazo",
            DeadlineStatus.Overdue => $"atrasada {Ago(snapshot.Remaining)}",
            _ => $"{Remaining(snapshot.Remaining)} restantes",
        };
    }

    /// <summary>O título do aviso (§27) — diz o que aconteceu, não "você tem pendências".</summary>
    public static string AlertHeading(DeadlineSnapshot snapshot, TaskDeadline deadline, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(deadline);

        if (snapshot.Status is DeadlineStatus.Overdue)
        {
            return "Tarefa atrasada";
        }

        if (snapshot.Remaining < MinutePrecisionBelow)
        {
            return "Prazo urgente";
        }

        return (deadline.Date.DayNumber - today.DayNumber) switch
        {
            0 => "Prazo hoje",
            1 => "Prazo amanhã",
            _ => "Prazo se aproximando",
        };
    }

    /// <summary>O corpo do aviso, que vai abaixo do título da tarefa.</summary>
    public static string AlertMessage(DeadlineSnapshot snapshot, TaskDeadline deadline, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(deadline);

        if (snapshot.Status is DeadlineStatus.Overdue)
        {
            return $"Está atrasada {Ago(snapshot.Remaining)}.";
        }

        return snapshot.Remaining < MinutePrecisionBelow
            ? $"Vence em {Remaining(snapshot.Remaining)}."
            : $"Vence {Moment(deadline, today)}.";
    }

    /// <summary>
    /// "vence hoje às 18:00", ou "vence em 1h 42min" quando falta pouco — aí
    /// a contagem diz mais que o horário.
    /// </summary>
    private static string Due(DeadlineSnapshot snapshot, TaskDeadline deadline, DateOnly today) =>
        snapshot.Remaining < MinutePrecisionBelow
            ? $"vence em {Remaining(snapshot.Remaining)}"
            : (deadline.Date.DayNumber - today.DayNumber) < 2
                ? $"vence {Moment(deadline, today)}"
                : $"{Remaining(snapshot.Remaining)} restantes";

    private static string Count(int value, string singular, string plural) =>
        string.Create(CultureInfo.InvariantCulture, $"{value} {(value == 1 ? singular : plural)}");

    private static string ShortDate(DateOnly date) =>
        date.ToString("dd/MM", CultureInfo.InvariantCulture);

    private static string Time(TimeOnly time) =>
        time.ToString("HH:mm", CultureInfo.InvariantCulture);
}
