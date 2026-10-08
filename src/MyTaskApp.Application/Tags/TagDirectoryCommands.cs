using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Commands;

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

        if (!global.IsGlobal)
        {
            throw new DomainException("Este comando é só de outro diretório. Crie um aqui com \"+ Novo comando\".");
        }

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

/// <summary>
/// Tira o botão do diretório. O comando global continua cadastrado; o comando
/// só deste diretório (ADR-055) não tem outro lugar, e é excluído junto.
/// </summary>
public sealed record RemoveTagDirectoryCommand(Guid TagId, Guid DirectoryId, Guid BindingId);

public sealed class RemoveTagDirectoryCommandHandler(
    ITagRepository tags,
    IDevelopmentCommandRepository commands,
    IUnitOfWork unitOfWork,
    ILogger<RemoveTagDirectoryCommandHandler> logger)
{
    public async Task HandleAsync(RemoveTagDirectoryCommand command, CancellationToken cancellationToken = default)
    {
        var tag = await tags.GetByIdAsync(command.TagId, cancellationToken);
        var binding = tag.GetDirectoryCommand(command.DirectoryId, command.BindingId);

        tag.RemoveDirectoryCommand(command.DirectoryId, command.BindingId);

        if (await commands.FindByIdAsync(binding.DevelopmentCommandId, cancellationToken) is { } own
            && own.TagDirectoryId == command.DirectoryId)
        {
            commands.Remove(own);
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("TagDirectoryCommandRemoved {TagId} {BindingId}", tag.Id, command.BindingId);
    }
}

/// <summary>
/// Cria um comando só deste diretório (ADR-055), sem passar por Comandos
/// globais, e já o oferece como botão: entra no fim da lista, ligado.
/// </summary>
/// <param name="Settings">O nome é obrigatório: é o rótulo do botão.</param>
public sealed record CreateDirectoryOnlyCommand(
    Guid TagId,
    Guid DirectoryId,
    string Command,
    string? Description,
    DevelopmentCommandSettings Settings);

public sealed class CreateDirectoryOnlyCommandHandler(
    ITagRepository tags,
    IDevelopmentCommandRepository commands,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<CreateDirectoryOnlyCommandHandler> logger)
{
    /// <returns>A associação do botão no diretório.</returns>
    public async Task<Guid> HandleAsync(CreateDirectoryOnlyCommand command, CancellationToken cancellationToken = default)
    {
        var tag = await tags.GetByIdAsync(command.TagId, cancellationToken);
        var at = timeProvider.GetUtcNow();

        var created = DevelopmentCommand.CreateForDirectory(
            command.DirectoryId,
            command.Command,
            command.Description,
            command.Settings,
            at);

        // A associação antes do comando entrar no contexto: diretório que não
        // existe, ou já cheio, recusa sem deixar um comando sem botão.
        var binding = tag.AddDirectoryCommand(command.DirectoryId, created.Id, at);

        await commands.AddAsync(created, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "DirectoryOnlyCommandCreated {TagId} {DirectoryId} {CommandId}",
            tag.Id,
            command.DirectoryId,
            created.Id);

        return binding.Id;
    }
}

/// <summary>Edita um comando só do diretório (ADR-055). Atômico, como o global.</summary>
public sealed record UpdateDirectoryOnlyCommand(
    Guid CommandId,
    string Command,
    string? Description,
    DevelopmentCommandSettings Settings);

public sealed class UpdateDirectoryOnlyCommandHandler(
    IDevelopmentCommandRepository commands,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<UpdateDirectoryOnlyCommandHandler> logger)
{
    public async Task<DevelopmentCommandRow> HandleAsync(
        UpdateDirectoryOnlyCommand command,
        CancellationToken cancellationToken = default)
    {
        var existing = await commands.GetByIdAsync(command.CommandId, cancellationToken);

        existing.UpdateForDirectory(command.Command, command.Description, command.Settings, timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("DirectoryOnlyCommandUpdated {CommandId}", existing.Id);

        return DevelopmentCommandRow.From(existing);
    }
}

/// <summary>Um comando inteiro, para o formulário de edição do comando do diretório (ADR-055).</summary>
public sealed record GetDirectoryOnlyCommand(Guid CommandId);

public sealed class GetDirectoryOnlyCommandHandler(IDevelopmentCommandRepository commands)
{
    public async Task<DevelopmentCommandRow> HandleAsync(
        GetDirectoryOnlyCommand query,
        CancellationToken cancellationToken = default) =>
        DevelopmentCommandRow.From(await commands.GetByIdAsync(query.CommandId, cancellationToken));
}

public sealed record GetTagDirectoryCommands(Guid DirectoryId);

public sealed class GetTagDirectoryCommandsHandler(ITagQuery query)
{
    public Task<IReadOnlyList<TagDirectoryCommandRow>> HandleAsync(
        GetTagDirectoryCommands command,
        CancellationToken cancellationToken = default) =>
        query.ListDirectoryCommandsAsync(command.DirectoryId, cancellationToken);
}
