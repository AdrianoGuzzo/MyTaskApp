using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Mobile.Core.Presentation;

/// <summary>Uma etiqueta como a linha desenha: nome e cor.</summary>
public sealed record TagChip(Guid Id, string Name, string ColorHex);

/// <summary>
/// Uma linha da lista do celular: só o que cabe numa linha e decide um toque.
/// Montada de <see cref="TaskListItem"/>, a mesma linha da busca do desktop e do MCP.
/// </summary>
public sealed record TaskRow(
    Guid TaskId,
    Guid OccurrenceId,
    string Title,
    string? Summary,
    TaskItemStatus Status,
    TaskLifecycle Lifecycle,
    string StatusLabel,
    TaskPriority Priority,
    string PriorityLabel,
    string Origin,
    IReadOnlyList<TagChip> Tags,
    string? DeadlineLabel,
    DeadlineSeverity? DeadlineSeverity,
    bool IsOverdue)
{
    public bool IsCompleted => Status == TaskItemStatus.Completed;

    public bool IsHighPriority => Priority is TaskPriority.High or TaskPriority.Urgent;

    /// <summary>Concluir e reabrir pelo gesto só na lista principal; arquivada e lixeira pedem o detalhe.</summary>
    public bool CanToggle => Lifecycle is TaskLifecycle.Active or TaskLifecycle.Completed && Status != TaskItemStatus.Cancelled;

    public string ToggleLabel => IsCompleted ? "Reabrir" : "Concluir";

    /// <summary>O que o leitor de tela fala ao passar pela linha.</summary>
    public string AccessibleDescription =>
        string.Join(", ", new[]
        {
            Title,
            StatusLabel,
            "prioridade " + PriorityLabel.ToLower(Labels.Culture),
            "origem " + Origin,
            DeadlineLabel is null ? null : "prazo " + DeadlineLabel,
            IsOverdue ? "atrasada" : null,
        }.Where(part => part is not null));

    public static TaskRow From(TaskListItem item, OriginCatalog origins)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(origins);

        var task = item.Task;

        return new TaskRow(
            task.TaskId,
            task.OccurrenceId,
            task.Title,
            Labels.Summary(task.Notes),
            item.Status,
            item.Lifecycle,
            Labels.Status(item.Status, item.Lifecycle),
            task.Priority,
            Labels.Priority(task.Priority),
            origins.Label(task.Placement),
            task.Tags?.Select(tag => new TagChip(tag.Id, tag.Name, tag.ColorHex)).ToList() ?? [],
            task.Deadline?.Label,
            task.Deadline?.Severity,
            item.IsOverdue);
    }
}
