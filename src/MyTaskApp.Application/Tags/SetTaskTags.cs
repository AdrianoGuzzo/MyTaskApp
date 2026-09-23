using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Tags;

/// <summary>
/// O conjunto final de etiquetas do checklist. Serve tanto para associar quanto
/// para remover, e repetir a mesma chamada não muda nada (ADR-025).
/// </summary>
public sealed record SetTaskTags(Guid TaskId, IReadOnlyCollection<Guid> TagIds);

public sealed class SetTaskTagsHandler(
    ITaskItemRepository tasks,
    ITagRepository tags,
    IUnitOfWork unitOfWork,
    ILogger<SetTaskTagsHandler> logger)
{
    public async Task HandleAsync(SetTaskTags command, CancellationToken cancellationToken = default)
    {
        var wanted = await tags.GetExistingAsync(command.TagIds, cancellationToken);

        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);

        task.SetTags(wanted);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TaskTagsChanged {TaskId} {TagCount}", task.Id, wanted.Length);
    }
}
