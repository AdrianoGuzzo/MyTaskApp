using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Tags;

/// <summary>Uma pasta nova na etiqueta, com o alias que a chama na anotação (ADR-026).</summary>
public sealed record AddTagDirectory(
    Guid TagId,
    string Alias,
    string Path,
    string? Name,
    string? Description);

public sealed class AddTagDirectoryHandler(
    ITagRepository tags,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<AddTagDirectoryHandler> logger)
{
    public async Task<Guid> HandleAsync(AddTagDirectory command, CancellationToken cancellationToken = default)
    {
        var tag = await tags.GetByIdAsync(command.TagId, cancellationToken);

        var directory = tag.AddDirectory(
            command.Alias,
            command.Path,
            command.Name,
            command.Description,
            timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TagDirectoryAdded {TagId} {DirectoryId}", tag.Id, directory.Id);

        return directory.Id;
    }
}

/// <summary>
/// Troca alias, path, nome e descrição de uma vez. Anotações que já receberam o
/// path antigo ficam como estão: o texto nunca guardou referência ao diretório.
/// </summary>
public sealed record UpdateTagDirectory(
    Guid TagId,
    Guid DirectoryId,
    string Alias,
    string Path,
    string? Name,
    string? Description);

public sealed class UpdateTagDirectoryHandler(
    ITagRepository tags,
    IUnitOfWork unitOfWork,
    ILogger<UpdateTagDirectoryHandler> logger)
{
    public async Task HandleAsync(UpdateTagDirectory command, CancellationToken cancellationToken = default)
    {
        var tag = await tags.GetByIdAsync(command.TagId, cancellationToken);

        tag.UpdateDirectory(
            command.DirectoryId,
            command.Alias,
            command.Path,
            command.Name,
            command.Description);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TagDirectoryUpdated {TagId} {DirectoryId}", tag.Id, command.DirectoryId);
    }
}

public sealed record RemoveTagDirectory(Guid TagId, Guid DirectoryId);

public sealed class RemoveTagDirectoryHandler(
    ITagRepository tags,
    IUnitOfWork unitOfWork,
    ILogger<RemoveTagDirectoryHandler> logger)
{
    public async Task HandleAsync(RemoveTagDirectory command, CancellationToken cancellationToken = default)
    {
        var tag = await tags.GetByIdAsync(command.TagId, cancellationToken);

        tag.RemoveDirectory(command.DirectoryId);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TagDirectoryRemoved {TagId} {DirectoryId}", tag.Id, command.DirectoryId);
    }
}

public sealed record GetTagDirectories(Guid TagId);

public sealed class GetTagDirectoriesHandler(ITagQuery query)
{
    public Task<IReadOnlyList<TagDirectoryRow>> HandleAsync(
        GetTagDirectories command,
        CancellationToken cancellationToken = default) =>
        query.ListDirectoriesAsync(command.TagId, cancellationToken);
}

/// <summary>Os atalhos que a anotação de uma tarefa oferece: os das etiquetas dela.</summary>
public sealed record GetTaskDirectories(Guid TaskId);

public sealed class GetTaskDirectoriesHandler(ITagQuery query)
{
    public Task<IReadOnlyList<TagDirectoryRow>> HandleAsync(
        GetTaskDirectories command,
        CancellationToken cancellationToken = default) =>
        query.ListDirectoriesForTaskAsync(command.TaskId, cancellationToken);
}
