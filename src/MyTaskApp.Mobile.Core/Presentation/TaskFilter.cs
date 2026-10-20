using MyTaskApp.Application.Devices;
using MyTaskApp.Application.Tasks;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Mobile.Core.Presentation;

/// <summary>Os atalhos do topo da lista: um de cada vez.</summary>
public enum TaskListPreset
{
    All = 0,
    Pending = 1,
    Completed = 2,
    Cancelled = 3,
}

/// <summary>
/// Os filtros da lista do celular, imutáveis: cada mudança é um filtro novo, e
/// a busca sai sempre de <see cref="ToSearch"/>. "Todas as tarefas" é de todos
/// os dispositivos (<see cref="DeviceView.All"/>) — a origem é que estreita.
/// </summary>
public sealed record TaskFilter
{
    public static TaskFilter Default { get; } = new();

    /// <summary>O ponto de partida de uma aba: Pendentes na aba Tarefas, Concluídas no Histórico.</summary>
    public static TaskFilter For(TaskListPreset preset) => new() { Preset = preset, BasePreset = preset };

    public string? Text { get; init; }

    public TaskListPreset Preset { get; init; } = TaskListPreset.All;

    /// <summary>O atalho de quando nada foi mexido; escolher outro estreita.</summary>
    public TaskListPreset BasePreset { get; init; } = TaskListPreset.All;

    public IReadOnlyCollection<TaskPriority> Priorities { get; init; } = [];

    public IReadOnlyCollection<Guid> TagIds { get; init; } = [];

    /// <summary>Só as que passaram da data ou do prazo — o mesmo critério do quadro Hoje. Fica na folha (ADR-079).</summary>
    public bool OverdueOnly { get; init; }

    public PlacementMatch? Origin { get; init; }

    public TaskSort Sort { get; init; } = TaskSort.Created;

    /// <summary>Há algo além do padrão — é o que distingue "nenhuma tarefa" de "nada com estes filtros".</summary>
    public bool IsNarrowed =>
        !string.IsNullOrWhiteSpace(Text) || Preset != BasePreset || Priorities.Count > 0 || TagIds.Count > 0 || OverdueOnly || Origin is not null;

    /// <summary>Quantos filtros da folha estão ligados, para o selo do botão; o dispositivo fica no título.</summary>
    public int SheetCount => (OverdueOnly ? 1 : 0) + (Priorities.Count > 0 ? 1 : 0) + (TagIds.Count > 0 ? 1 : 0);

    public SearchTasks ToSearch(int offset, int limit) => new(
        Text: string.IsNullOrWhiteSpace(Text) ? null : Text.Trim(),
        Statuses: Preset switch
        {
            TaskListPreset.Pending => [TaskItemStatus.Pending],
            TaskListPreset.Completed => [TaskItemStatus.Completed],
            TaskListPreset.Cancelled => [TaskItemStatus.Cancelled],
            _ => null,
        },
        TagIds: TagIds.Count > 0 ? TagIds : null,
        Priorities: Priorities.Count > 0 ? Priorities : null,
        OverdueOnly: OverdueOnly,
        Sort: Sort,
        Limit: limit,
        Offset: offset,
        Device: DeviceView.All,
        Origin: Origin);
}
