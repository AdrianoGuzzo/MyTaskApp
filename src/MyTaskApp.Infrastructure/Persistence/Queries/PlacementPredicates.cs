using MyTaskApp.Application.Devices;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Infrastructure.Persistence.Queries;

/// <summary>
/// O "onde aparece" nas consultas (ADR-062), num lugar só: sem lugar (legado),
/// em todos, ou num dos computadores do filtro. Composto dentro dos
/// <c>Any</c> e <c>join</c> que já existem, o EF traduz como subconsulta.
/// </summary>
internal static class PlacementPredicates
{
    public static IQueryable<TaskItem> VisibleIn(this IQueryable<TaskItem> tasks, PlacementFilter placement)
    {
        if (placement.DeviceIds is not { } ids)
        {
            return tasks;
        }

        var devices = ids.ToArray();

        return tasks.Where(task => task.PlacementKind == null
            || task.PlacementKind == PlacementKind.Everywhere
            || task.PlacementDeviceId == null
            || devices.Contains(task.PlacementDeviceId.Value));
    }

    /// <summary>
    /// A origem exata (ADR-067): só as compartilhadas — legado e "só no
    /// computador" sem computador inclusos, como em <see cref="DevicePlacement.From"/> —
    /// ou só as que moram nestes dispositivos.
    /// </summary>
    public static IQueryable<TaskItem> PlacedAt(this IQueryable<TaskItem> tasks, PlacementMatch? origin)
    {
        if (origin is null)
        {
            return tasks;
        }

        if (origin.SharedOnly)
        {
            return tasks.Where(task => task.PlacementKind == null
                || task.PlacementKind == PlacementKind.Everywhere
                || task.PlacementDeviceId == null);
        }

        var devices = origin.DeviceIds.ToArray();

        return tasks.Where(task => task.PlacementKind == PlacementKind.Device
            && task.PlacementDeviceId != null
            && devices.Contains(task.PlacementDeviceId.Value));
    }

    public static IQueryable<Tag> VisibleIn(this IQueryable<Tag> tags, PlacementFilter placement)
    {
        if (placement.DeviceIds is not { } ids)
        {
            return tags;
        }

        var devices = ids.ToArray();

        return tags.Where(tag => tag.PlacementKind == null
            || tag.PlacementKind == PlacementKind.Everywhere
            || tag.PlacementDeviceId == null
            || devices.Contains(tag.PlacementDeviceId.Value));
    }
}
