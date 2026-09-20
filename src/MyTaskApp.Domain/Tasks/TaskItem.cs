using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Domain.Tasks;

/// <summary>
/// Raiz do agregado: descreve o QUE é a tarefa. O QUANDO e o estado de execução
/// ficam nas ocorrências (ADR-001). Não existe tarefa sem ocorrência — é isso
/// que elimina o caminho duplo entre tarefa simples e recorrente.
/// </summary>
public sealed class TaskItem
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 4000;

    private readonly List<TaskOccurrence> _occurrences = [];

    private TaskItem(
        Guid id,
        string title,
        string? description,
        TaskPriority priority,
        DateTimeOffset createdAt)
    {
        Id = id;
        Title = title;
        Description = description;
        Priority = priority;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public string Title { get; private set; }

    public string? Description { get; private set; }

    public TaskPriority Priority { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public IReadOnlyList<TaskOccurrence> Occurrences => _occurrences.AsReadOnly();

    /// <summary>
    /// A política de lembrete da série. O domínio não conhece o padrão global do
    /// usuário — ele mora no banco e é aplicado pela Application na criação.
    /// </summary>
    public ReminderPolicy Reminder { get; private set; } = ReminderPolicy.None;

    public static TaskItem Create(
        string title,
        DateTimeOffset createdAt,
        string? description = null,
        TaskPriority priority = TaskPriority.Normal,
        TaskSchedule? schedule = null,
        ReminderPolicy? reminder = null)
    {
        var task = new TaskItem(
            Guid.CreateVersion7(createdAt),
            NormalizeTitle(title),
            NormalizeDescription(description),
            priority,
            createdAt);

        task.ChangeReminder(reminder ?? ReminderPolicy.None);
        task.AddOccurrence(schedule ?? TaskSchedule.Unscheduled, createdAt);

        return task;
    }

    /// <summary>
    /// Edição atômica: normaliza e valida tudo antes de alterar qualquer campo,
    /// para que uma recusa não deixe a entidade num estado intermediário.
    /// </summary>
    public void Update(string title, string? description, TaskPriority priority)
    {
        var normalizedTitle = NormalizeTitle(title);
        var normalizedDescription = NormalizeDescription(description);

        Title = normalizedTitle;
        Description = normalizedDescription;
        Priority = priority;
    }

    public void Rename(string title) => Title = NormalizeTitle(title);

    public void ChangeDescription(string? description) =>
        Description = NormalizeDescription(description);

    public void ChangePriority(TaskPriority priority) => Priority = priority;

    /// <summary>
    /// Troca a política de lembrete. Campo único, como <see cref="ChangePriority"/>:
    /// não há meia-alteração a proteger. Quem rearma as ocorrências pendentes é a
    /// Application, que tem o relógio do usuário.
    /// </summary>
    /// <remarks>
    /// A cópia (<c>with { }</c>) não é adorno. <see cref="ReminderPolicy"/> tem
    /// instâncias compartilhadas — <c>Default</c>, <c>Urgent</c>, <c>None</c> —
    /// e a captura rápida aplica a mesma a várias tarefas de uma vez. O EF trata
    /// um tipo owned como parte do dono e <b>não aceita a mesma instância em dois
    /// donos</b>: ele grava uma e deixa a outra com os valores default, em
    /// silêncio. O resultado era a primeira tarefa de cada captura nascer com o
    /// lembrete desligado. Copiar aqui protege todos os chamadores de uma vez.
    /// </remarks>
    public void ChangeReminder(ReminderPolicy policy)
    {
        if (policy is null)
        {
            throw new DomainException("A tarefa precisa de uma política de lembrete.");
        }

        Reminder = policy with { };
    }

    public TaskOccurrence GetOccurrence(Guid occurrenceId) =>
        _occurrences.SingleOrDefault(occurrence => occurrence.Id == occurrenceId)
        ?? throw new DomainException("Ocorrência não encontrada nesta tarefa.");

    private void AddOccurrence(TaskSchedule schedule, DateTimeOffset createdAt) =>
        _occurrences.Add(new TaskOccurrence(Guid.CreateVersion7(createdAt), Id, schedule));

    private static string NormalizeTitle(string? title) =>
        NormalizeOptionalText(title, MaxTitleLength, "O título")
        ?? throw new DomainException("A tarefa precisa de um título.");

    private static string? NormalizeDescription(string? description) =>
        NormalizeOptionalText(description, MaxDescriptionLength, "A descrição");

    /// <summary>Texto em branco vira nulo; texto acima do limite é recusado.</summary>
    private static string? NormalizeOptionalText(string? value, int maxLength, string fieldLabel)
    {
        var normalized = value?.Trim();

        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Length > maxLength)
        {
            throw new DomainException($"{fieldLabel} não pode passar de {maxLength} caracteres.");
        }

        return normalized;
    }
}
