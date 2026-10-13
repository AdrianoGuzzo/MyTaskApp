using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Devices;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Application.Tags;

public sealed record CreateTag(string Name, string ColorHex);

public sealed class CreateTagHandler(
    ITagRepository tags,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<CreateTagHandler> logger,
    DevicePlacementState? placements = null)
{
    public async Task<Guid> HandleAsync(CreateTag command, CancellationToken cancellationToken = default)
    {
        var tag = Tag.Create(command.Name, command.ColorHex, timeProvider.GetUtcNow(), placements.ForNew(null));

        // O nome é único na conta, e uma etiqueta de outro computador está
        // escondida aqui (ADR-062). Criar o mesmo nome é querer a mesma
        // etiqueta nos dois: ela passa a aparecer em todos, e não vira erro.
        if (placements is { IsActive: true }
            && await tags.FindByNameAsync(tag.Name, cancellationToken) is { } hidden
            && !placements.IsVisibleHere(hidden.Placement))
        {
            hidden.Place(DevicePlacement.Everywhere);
            await unitOfWork.SaveChangesAsync(cancellationToken);

            logger.LogInformation("TagSharedByName {TagId}", hidden.Id);

            return hidden.Id;
        }

        await tags.EnsureNameIsFreeAsync(tag.Name, exceptId: null, cancellationToken);

        await tags.AddAsync(tag, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TagCreated {TagId}", tag.Id);

        return tag.Id;
    }
}
