namespace MyTaskApp.Domain.Tasks;

/// <summary>
/// Agendamento em hora de parede (ADR-002): o que o usuário lê no relógio dele.
/// A conversão para instante absoluto acontece na borda, ao disparar lembretes.
/// </summary>
public sealed record TaskSchedule
{
    public TaskSchedule(DateOnly? Date, TimeOnly? Time)
    {
        if (Date is null && Time is not null)
        {
            throw new DomainException("Não é possível agendar um horário sem data.");
        }

        this.Date = Date;
        this.Time = Time;
    }

    public static TaskSchedule Unscheduled { get; } = new(null, null);

    public DateOnly? Date { get; }

    public TimeOnly? Time { get; }

    public bool IsScheduled => Date is not null;

    public bool HasTime => Time is not null;

    public static TaskSchedule On(DateOnly date) => new(date, null);

    public static TaskSchedule At(DateOnly date, TimeOnly time) => new(date, time);
}
