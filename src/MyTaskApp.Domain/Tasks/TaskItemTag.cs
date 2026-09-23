namespace MyTaskApp.Domain.Tasks;

/// <summary>
/// O vínculo entre um checklist e uma etiqueta. Só as duas chaves: nome e cor
/// ficam na etiqueta, para que alterá-la reflita em todo checklist (ADR-025).
/// </summary>
public sealed class TaskItemTag
{
    internal TaskItemTag(Guid taskItemId, Guid tagId)
    {
        TaskItemId = taskItemId;
        TagId = tagId;
    }

    public Guid TaskItemId { get; }

    public Guid TagId { get; }
}
