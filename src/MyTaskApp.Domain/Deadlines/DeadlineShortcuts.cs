namespace MyTaskApp.Domain.Deadlines;

/// <summary>Os atalhos do prazo, no menu da linha e no editor (§8, §9, §23).</summary>
public enum DeadlineShortcut
{
    Today = 0,
    Tomorrow = 1,
    EndOfWeek = 2,
    NextWeek = 3,
    InThreeDays = 4,
    InOneWeek = 5,
}

/// <summary>Traduz um atalho num prazo. Função pura, para os testes fixarem as bordas.</summary>
public static class DeadlineShortcuts
{
    /// <summary>
    /// O último dia da semana de trabalho. Fixo de propósito: o app não tem
    /// configuração de semana, e a cultura pt-BR começa a semana no domingo, o
    /// que faria "final da semana" cair no sábado.
    /// </summary>
    public const DayOfWeek LastWorkday = DayOfWeek.Friday;

    /// <summary>O fim do dia, para "Hoje" pedido depois do horário padrão.</summary>
    public static readonly TimeOnly EndOfDay = new(23, 59);

    /// <param name="time">
    /// O horário do prazo: o do prazo atual, se houver, para "+1 dia" não mexer
    /// na hora que o usuário escolheu; senão o padrão da configuração.
    /// </param>
    public static TaskDeadline Resolve(DeadlineShortcut shortcut, DateOnly today, TimeOnly now, TimeOnly time) =>
        shortcut switch
        {
            // "Hoje" às 19:00 com padrão 18:00 não pode virar um prazo vencido.
            DeadlineShortcut.Today => new TaskDeadline(today, time > now ? time : EndOfDay),
            DeadlineShortcut.Tomorrow => new TaskDeadline(today.AddDays(1), time),
            DeadlineShortcut.InThreeDays => new TaskDeadline(today.AddDays(3), time),
            DeadlineShortcut.InOneWeek => new TaskDeadline(today.AddDays(7), time),
            DeadlineShortcut.EndOfWeek => new TaskDeadline(EndOfWeek(today, now, time), time),
            DeadlineShortcut.NextWeek => new TaskDeadline(LastWorkdayOfWeek(today).AddDays(7), time),
            _ => throw new DomainException("Esse atalho de prazo não existe."),
        };

    /// <summary>
    /// A sexta desta semana; se ela já passou (sábado, domingo, ou sexta depois
    /// do horário), a da semana seguinte.
    /// </summary>
    private static DateOnly EndOfWeek(DateOnly today, TimeOnly now, TimeOnly time)
    {
        var friday = LastWorkdayOfWeek(today);

        return friday > today || (friday == today && time > now) ? friday : friday.AddDays(7);
    }

    /// <summary>A sexta da semana (segunda a domingo) em que <paramref name="day"/> está.</summary>
    private static DateOnly LastWorkdayOfWeek(DateOnly day)
    {
        var sinceMonday = ((int)day.DayOfWeek + 6) % 7;
        var monday = day.AddDays(-sinceMonday);

        return monday.AddDays(((int)LastWorkday + 6) % 7);
    }
}
