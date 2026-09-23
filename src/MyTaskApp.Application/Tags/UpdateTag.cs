using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Application.Tags;

public sealed record UpdateTag(Guid TagId, string Name, string ColorHex);

public sealed class UpdateTagHandler(
    ITagRepository tags,
    IUnitOfWork unitOfWork,
    ILogger<UpdateTagHandler> logger)
{
    public async Task HandleAsync(UpdateTag command, CancellationToken cancellationToken = default)
    {
        var tag = await tags.GetByIdAsync(command.TagId, cancellationToken);

        // Normaliza antes de consultar: "  Urgente " e "urgente" são o mesmo nome.
        await tags.EnsureNameIsFreeAsync(Tag.NormalizeName(command.Name), tag.Id, cancellationToken);

        tag.Update(command.Name, command.ColorHex);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TagUpdated {TagId}", tag.Id);
    }
}
