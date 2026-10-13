using MyTaskApp.Application.Devices;

namespace MyTaskApp.Application.Tags;

/// <param name="Device">De qual computador (ADR-062); <c>null</c> = este e as compartilhadas.</param>
/// <param name="Include">
/// Etiquetas que entram mesmo escondidas aqui: as que já marcam a tarefa que
/// chegou de outro computador, para dar para tirá-las.
/// </param>
public sealed record GetTags(DeviceView? Device = null, IReadOnlyCollection<Guid>? Include = null);

public sealed class GetTagsHandler(ITagQuery query, DevicePlacementState? placements = null)
{
    public Task<IReadOnlyList<TagRow>> HandleAsync(
        GetTags command,
        CancellationToken cancellationToken = default) =>
        query.ListAsync(
            placements?.Filter(command.Device ?? DeviceView.Here) ?? PlacementFilter.All,
            command.Include ?? [],
            cancellationToken);
}
