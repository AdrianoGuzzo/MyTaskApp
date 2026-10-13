using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Devices;

namespace MyTaskApp.Application.Devices;

/// <summary>O que este computador ainda precisa pôr no lugar (ADR-062).</summary>
/// <param name="TasksToPlaceHere">Legado criado aqui: passa a ser só daqui.</param>
/// <param name="TagsToPlaceHere">Etiqueta legada criada aqui e usada só em tarefas daqui.</param>
/// <param name="TagsToShare">Etiqueta legada criada aqui e usada em tarefa de outro computador ou compartilhada.</param>
/// <param name="TasksToRehome">Tarefas num id antigo deste computador.</param>
/// <param name="TagsToRehome">Etiquetas num id antigo deste computador.</param>
public sealed record PendingPlacements(
    IReadOnlyList<Guid> TasksToPlaceHere,
    IReadOnlyList<Guid> TagsToPlaceHere,
    IReadOnlyList<Guid> TagsToShare,
    IReadOnlyList<Guid> TasksToRehome,
    IReadOnlyList<Guid> TagsToRehome)
{
    public bool IsEmpty =>
        TasksToPlaceHere.Count == 0 && TagsToPlaceHere.Count == 0 && TagsToShare.Count == 0
        && TasksToRehome.Count == 0 && TagsToRehome.Count == 0;
}

/// <summary>
/// Acha o legado sem lugar e o que está num id antigo daqui. Mora na
/// Infrastructure porque a origem (quem criou) é da sincronização.
/// </summary>
public interface IPendingPlacementQuery
{
    Task<PendingPlacements> FindAsync(Guid here, IReadOnlyCollection<Guid> hereIds, CancellationToken cancellationToken);
}

/// <summary>
/// O assentamento (ADR-062): o que existia antes de haver lugar passa a ser do
/// computador que o criou, e o que estava num id antigo daqui vai para o atual.
/// </summary>
/// <remarks>
/// <para>
/// <b>Só o criador assenta.</b> Cada computador decide só sobre o que ele
/// mesmo criou, então dois computadores nunca gravam valores diferentes no
/// mesmo item. Enquanto o criador não atualiza, o item continua sem lugar, e
/// sem lugar aparece em todos: nada some por esperar.
/// </para>
/// <para>
/// <b>Etiqueta usada nos dois lados vira compartilhada.</b> Uma etiqueta legada
/// que marca alguma tarefa que não é só daqui (de outro computador, ou de
/// todos) aparece em todos; senão, fica só aqui.
/// </para>
/// <para>
/// Passa pela captura como qualquer edição: o lugar sobe e os outros
/// computadores escondem o que não é deles.
/// </para>
/// </remarks>
public sealed class SettleDevicePlacementsHandler(
    IPendingPlacementQuery query,
    ITaskItemRepository tasks,
    ITagRepository tags,
    IUnitOfWork unitOfWork,
    DevicePlacementState state)
{
    public async Task<int> HandleAsync(CancellationToken cancellationToken = default)
    {
        if (state.Here is not { } here)
        {
            return 0;
        }

        var pending = await query.FindAsync(here, state.HereIds, cancellationToken);

        if (pending.IsEmpty)
        {
            return 0;
        }

        var placedHere = DevicePlacement.On(here);
        var changed = 0;

        foreach (var taskId in pending.TasksToPlaceHere.Concat(pending.TasksToRehome).Distinct())
        {
            if (await tasks.FindByIdAsync(taskId, cancellationToken) is { } task)
            {
                task.Place(placedHere);
                changed++;
            }
        }

        foreach (var tagId in pending.TagsToPlaceHere.Concat(pending.TagsToRehome).Distinct())
        {
            if (await tags.FindByIdAsync(tagId, cancellationToken) is { } tag)
            {
                tag.Place(placedHere);
                changed++;
            }
        }

        foreach (var tagId in pending.TagsToShare)
        {
            if (await tags.FindByIdAsync(tagId, cancellationToken) is { } tag)
            {
                tag.Place(DevicePlacement.Everywhere);
                changed++;
            }
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        return changed;
    }
}
