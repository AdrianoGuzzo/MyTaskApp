namespace MyTaskApp.Domain.Planning;

/// <summary>Seções da tela "Hoje" (§9). Mutuamente exclusivas.</summary>
public enum TodaySection
{
    Overdue = 0,
    Now = 1,
    Today = 2,
    Unscheduled = 3,
    Completed = 4,

    /// <summary>
    /// PRAZOS: tarefas com prazo que não estão marcadas para uma hora de hoje —
    /// responsabilidades que continuam correndo enquanto o usuário faz outras
    /// coisas (ADR-050).
    /// </summary>
    Deadlines = 5,
}
