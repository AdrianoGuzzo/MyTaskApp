using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Tags;

namespace MyTaskApp.Application.Tags;

public sealed record CreateTag(string Name, string ColorHex);

public sealed class CreateTagHandler(
    ITagRepository tags,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<CreateTagHandler> logger)
{
    public async Task<Guid> HandleAsync(CreateTag command, CancellationToken cancellationToken = default)
    {
        var tag = Tag.Create(command.Name, command.ColorHex, timeProvider.GetUtcNow());

        await tags.EnsureNameIsFreeAsync(tag.Name, exceptId: null, cancellationToken);

        await tags.AddAsync(tag, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TagCreated {TagId}", tag.Id);

        return tag.Id;
    }
}
