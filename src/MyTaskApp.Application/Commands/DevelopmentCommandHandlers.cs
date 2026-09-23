using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Application.Commands;

/// <summary>Um comando global como a tela o mostra (ADR-028).</summary>
public sealed record DevelopmentCommandRow(
    Guid Id,
    string Alias,
    string Command,
    string? Description,
    DateTimeOffset UpdatedAt)
{
    public static DevelopmentCommandRow From(DevelopmentCommand command) =>
        new(command.Id, command.Alias, command.Command, command.Description, command.UpdatedAt);
}

public sealed record GetDevelopmentCommands;

public sealed class GetDevelopmentCommandsHandler(IDevelopmentCommandRepository commands)
{
    public async Task<IReadOnlyList<DevelopmentCommandRow>> HandleAsync(
        GetDevelopmentCommands query,
        CancellationToken cancellationToken = default)
    {
        var all = await commands.ListAsync(cancellationToken);

        return all.Select(DevelopmentCommandRow.From).ToList();
    }
}

public sealed record CreateDevelopmentCommand(string Alias, string Command, string? Description);

public sealed class CreateDevelopmentCommandHandler(
    IDevelopmentCommandRepository commands,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<CreateDevelopmentCommandHandler> logger)
{
    public async Task<DevelopmentCommandRow> HandleAsync(
        CreateDevelopmentCommand command,
        CancellationToken cancellationToken = default)
    {
        var created = DevelopmentCommand.Create(
            command.Alias,
            command.Command,
            command.Description,
            timeProvider.GetUtcNow());

        await commands.EnsureAliasIsFreeAsync(created.Alias, exceptId: null, cancellationToken);

        await commands.AddAsync(created, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("DevelopmentCommandCreated {CommandId} {Alias}", created.Id, created.Alias);

        return DevelopmentCommandRow.From(created);
    }
}

public sealed record UpdateDevelopmentCommand(Guid CommandId, string Alias, string Command, string? Description);

public sealed class UpdateDevelopmentCommandHandler(
    IDevelopmentCommandRepository commands,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<UpdateDevelopmentCommandHandler> logger)
{
    /// <remarks>
    /// Renomear o apelido não reescreve as tarefas que chamam o antigo: elas
    /// guardam o texto digitado. A execução avisa "@antigo não existe" — melhor
    /// que rodar outra coisa em silêncio.
    /// </remarks>
    public async Task<DevelopmentCommandRow> HandleAsync(
        UpdateDevelopmentCommand command,
        CancellationToken cancellationToken = default)
    {
        var existing = await commands.GetByIdAsync(command.CommandId, cancellationToken);

        // Normaliza antes de consultar: "restore" e "@Restore" são o mesmo apelido.
        await commands.EnsureAliasIsFreeAsync(
            DevelopmentCommand.NormalizeAlias(command.Alias),
            existing.Id,
            cancellationToken);

        existing.Update(command.Alias, command.Command, command.Description, timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("DevelopmentCommandUpdated {CommandId} {Alias}", existing.Id, existing.Alias);

        return DevelopmentCommandRow.From(existing);
    }
}

public sealed record DeleteDevelopmentCommand(Guid CommandId);

public sealed class DeleteDevelopmentCommandHandler(
    IDevelopmentCommandRepository commands,
    IUnitOfWork unitOfWork,
    ILogger<DeleteDevelopmentCommandHandler> logger)
{
    public async Task HandleAsync(DeleteDevelopmentCommand command, CancellationToken cancellationToken = default)
    {
        var existing = await commands.GetByIdAsync(command.CommandId, cancellationToken);

        commands.Remove(existing);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("DevelopmentCommandDeleted {CommandId} {Alias}", existing.Id, existing.Alias);
    }
}
