using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.TimeTracking;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Devices;

namespace MyTaskApp.Application.Devices;

/// <summary>
/// Leva uma tarefa para outro computador, ou para todos, ou de volta para este
/// (ADR-062). Nunca automático: só quando alguém pede.
/// </summary>
public sealed record PlaceTask(Guid TaskId, DevicePlacement Placement);

/// <summary>Leva uma etiqueta para outro computador, para todos, ou de volta para este.</summary>
public sealed record PlaceTag(Guid TagId, DevicePlacement Placement);

/// <param name="LeftThisComputer">Saiu da lista daqui: a tela avisa e oferece desfazer.</param>
/// <param name="Previous">Onde estava, para o "Desfazer".</param>
public sealed record PlaceResult(bool LeftThisComputer, string Destination, DevicePlacement? Previous);

public sealed class PlaceTaskHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    DevicePlacementState placements,
    ILogger<PlaceTaskHandler> logger,
    IActiveTimerQuery? timers = null)
{
    public async Task<PlaceResult> HandleAsync(PlaceTask command, CancellationToken cancellationToken = default)
    {
        var placement = placements.EnsureCanPlace(command.Placement);
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);
        var previous = task.Placement;
        var leaves = placements.IsVisibleHere(previous) && !placements.IsVisibleHere(placement);

        // O cronômetro corre aqui: mandar a tarefa embora deixaria um relógio
        // contando para uma tarefa que some da lista.
        if (leaves
            && timers is not null
            && await timers.FindAsync(cancellationToken) is { } timer
            && timer.TaskId == task.Id)
        {
            throw new DomainException("Pare o cronômetro desta tarefa antes de mandá-la para outro computador.");
        }

        task.Place(placement);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TaskPlaced {TaskId} {Kind}", task.Id, placement.Kind);

        return new PlaceResult(leaves, placements.Describe(placement), previous);
    }
}

public sealed class PlaceTagHandler(
    ITagRepository tags,
    IUnitOfWork unitOfWork,
    DevicePlacementState placements,
    ILogger<PlaceTagHandler> logger)
{
    public async Task<PlaceResult> HandleAsync(PlaceTag command, CancellationToken cancellationToken = default)
    {
        var placement = placements.EnsureCanPlace(command.Placement);
        var tag = await tags.FindByIdAsync(command.TagId, cancellationToken)
            ?? throw new DomainException("Etiqueta não encontrada.");
        var previous = tag.Placement;

        tag.Place(placement);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TagPlaced {TagId} {Kind}", tag.Id, placement.Kind);

        return new PlaceResult(
            placements.IsVisibleHere(previous) && !placements.IsVisibleHere(placement),
            placements.Describe(placement),
            previous);
    }
}

/// <summary>Marca como vista a tarefa que chegou de outro computador (ADR-062).</summary>
public sealed record AcknowledgeArrival(Guid TaskId);

public sealed class AcknowledgeArrivalHandler(ITaskItemRepository tasks, IUnitOfWork unitOfWork)
{
    public async Task HandleAsync(AcknowledgeArrival command, CancellationToken cancellationToken = default)
    {
        if (await tasks.FindByIdAsync(command.TaskId, cancellationToken) is not { ReceivedAt: not null } task)
        {
            return;
        }

        task.AcknowledgeArrival();
        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}
