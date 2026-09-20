namespace MyTaskApp.Domain.Planning;

/// <summary>Seções da tela "Hoje" (§9). Mutuamente exclusivas.</summary>
public enum TodaySection
{
    Overdue = 0,
    Now = 1,
    Today = 2,
    Unscheduled = 3,
    Completed = 4,
}
