using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Tags;

public sealed record DeleteTag(Guid TagId);

/// <summary>
/// Exclui a etiqueta e, com ela, os vínculos com os checklists. Quem apaga os
/// vínculos é o <c>ON DELETE CASCADE</c> do banco (ADR-025): carregar cada
/// checklist só para tirar uma etiqueta seria uma ida por checklist.
/// </summary>
public sealed class DeleteTagHandler(
    ITagRepository tags,
    IUnitOfWork unitOfWork,
    ILogger<DeleteTagHandler> logger)
{
    public async Task HandleAsync(DeleteTag command, CancellationToken cancellationToken = default)
    {
        var tag = await tags.GetByIdAsync(command.TagId, cancellationToken);

        tags.Remove(tag);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TagDeleted {TagId}", tag.Id);
    }
}
