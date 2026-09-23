namespace MyTaskApp.Application.Tags;

public sealed record GetTags;

public sealed class GetTagsHandler(ITagQuery query)
{
    public Task<IReadOnlyList<TagRow>> HandleAsync(
        GetTags command,
        CancellationToken cancellationToken = default) =>
        query.ListAsync(cancellationToken);
}
