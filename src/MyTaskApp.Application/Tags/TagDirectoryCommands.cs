using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Tags;

/// <summary>
/// Oferece um comando global como botão nos worktrees do diretório (ADR-051).
/// Entra no fim da lista, ligado e sem personalização.
/// </summary>
public sealed record AddTagDirectoryCommand(Guid TagId, Guid DirectoryId, Guid CommandId);

public sealed class AddTagDirectoryCommandHandler(
    ITagRepository tags,
    IDevelopmentCommandRepository commands,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<AddTagDirectoryCommandHandler> logger)
{
    public async Task<Guid> HandleAsync(AddTagDirectoryCommand command, CancellationToken cancellationToken = default)
    {
        // O comando é de outro agregado: a etiqueta não tem como conferir.
        var global = await commands.GetByIdAsync(command.CommandId, cancellationToken);
        var tag = await tags.GetByIdAsync(command.TagId, cancellationToken);

        var binding = tag.AddDirectoryCommand(command.DirectoryId, global.Id, timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "TagDirectoryCommandAdded {TagId} {DirectoryId} {CommandId}",
            tag.Id,
            command.DirectoryId,
            global.Id);

        return binding.Id;
    }
}

/// <summary>
/// Personaliza o comando só neste diretório (ADR-051): outro texto, outra pasta,
/// ou os dois. Em branco volta à configuração global.
/// </summary>
public sealed record CustomizeTagDirectoryCommand(
    Guid TagId,
    Guid DirectoryId,
    Guid BindingId,
    string? CommandOverride,
    string? WorkingDirectoryOverride);

public sealed class CustomizeTagDirectoryCommandHandler(
    ITagRepository tags,
    IUnitOfWork unitOfWork,
    ILogger<CustomizeTagDirectoryCommandHandler> logger)
{
    public async Task HandleAsync(CustomizeTagDirectoryCommand command, CancellationToken cancellationToken = default)
    {
        var tag = await tags.GetByIdAsync(command.TagId, cancellationToken);

        tag.CustomizeDirectoryCommand(
            command.DirectoryId,
            command.BindingId,
            command.CommandOverride,
            command.WorkingDirectoryOverride);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TagDirectoryCommandCustomized {TagId} {BindingId}", tag.Id, command.BindingId);
    }
}

/// <summary>Liga ou desliga o botão sem apagar a associação (ADR-051).</summary>
public sealed record SetTagDirectoryCommandEnabled(Guid TagId, Guid DirectoryId, Guid BindingId, bool Enabled);

public sealed class SetTagDirectoryCommandEnabledHandler(
    ITagRepository tags,
    IUnitOfWork unitOfWork,
    ILogger<SetTagDirectoryCommandEnabledHandler> logger)
{
    public async Task HandleAsync(SetTagDirectoryCommandEnabled command, CancellationToken cancellationToken = default)
    {
        var tag = await tags.GetByIdAsync(command.TagId, cancellationToken);

        tag.SetDirectoryCommandEnabled(command.DirectoryId, command.BindingId, command.Enabled);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "TagDirectoryCommandEnabledChanged {TagId} {BindingId} {Enabled}",
            tag.Id,
            command.BindingId,
            command.Enabled);
    }
}

/// <summary>Sobe (<c>-1</c>) ou desce (<c>+1</c>) o botão na lista.</summary>
public sealed record MoveTagDirectoryCommand(Guid TagId, Guid DirectoryId, Guid BindingId, int Offset);

public sealed class MoveTagDirectoryCommandHandler(ITagRepository tags, IUnitOfWork unitOfWork)
{
    public async Task HandleAsync(MoveTagDirectoryCommand command, CancellationToken cancellationToken = default)
    {
        var tag = await tags.GetByIdAsync(command.TagId, cancellationToken);

        tag.MoveDirectoryCommand(command.DirectoryId, command.BindingId, command.Offset);

        await unitOfWork.SaveChangesAsync(cancellationToken);
    }
}

/// <summary>Tira o botão do diretório. O comando global continua cadastrado.</summary>
public sealed record RemoveTagDirectoryCommand(Guid TagId, Guid DirectoryId, Guid BindingId);

public sealed class RemoveTagDirectoryCommandHandler(
    ITagRepository tags,
    IUnitOfWork unitOfWork,
    ILogger<RemoveTagDirectoryCommandHandler> logger)
{
    public async Task HandleAsync(RemoveTagDirectoryCommand command, CancellationToken cancellationToken = default)
    {
        var tag = await tags.GetByIdAsync(command.TagId, cancellationToken);

        tag.RemoveDirectoryCommand(command.DirectoryId, command.BindingId);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TagDirectoryCommandRemoved {TagId} {BindingId}", tag.Id, command.BindingId);
    }
}

public sealed record GetTagDirectoryCommands(Guid DirectoryId);

public sealed class GetTagDirectoryCommandsHandler(ITagQuery query)
{
    public Task<IReadOnlyList<TagDirectoryCommandRow>> HandleAsync(
        GetTagDirectoryCommands command,
        CancellationToken cancellationToken = default) =>
        query.ListDirectoryCommandsAsync(command.DirectoryId, cancellationToken);
}
