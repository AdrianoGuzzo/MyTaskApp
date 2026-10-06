using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Application.Commands;

/// <summary>Um comando global como a tela o mostra (ADR-028), com o que o comando rápido usa (ADR-051).</summary>
/// <param name="Parameters">As definições cadastradas; um <c>{nome}</c> sem definição é texto obrigatório.</param>
/// <param name="BindingCount">Em quantos diretórios de etiqueta ele é botão — o aviso antes de excluir.</param>
public sealed record DevelopmentCommandRow(
    Guid Id,
    string Alias,
    string Command,
    string? Description,
    DateTimeOffset UpdatedAt,
    string? Name = null,
    CommandMode Mode = CommandMode.Execute,
    string? WorkingDirectory = null,
    bool KeepTerminalOpen = true,
    bool RequiresConfirmation = false,
    IReadOnlyList<CommandParameterSpec>? Parameters = null,
    int BindingCount = 0)
{
    public string DisplayName => Name ?? Alias;

    public DevelopmentCommandSettings Settings =>
        new(Name, Mode, WorkingDirectory, KeepTerminalOpen, RequiresConfirmation, Parameters);

    public static DevelopmentCommandRow From(DevelopmentCommand command, int bindingCount = 0) =>
        new(
            command.Id,
            command.Alias,
            command.Command,
            command.Description,
            command.UpdatedAt,
            command.Name,
            command.Mode,
            command.WorkingDirectory,
            command.KeepTerminalOpen,
            command.RequiresConfirmation,
            [.. command.Parameters.Select(parameter => parameter.ToSpec())],
            bindingCount);

    /// <summary>A definição de cada <c>{nome}</c> do texto (ver <see cref="DevelopmentCommand.ParametersOf"/>).</summary>
    public IReadOnlyList<CommandParameterSpec> ParametersOf(string text) =>
        [.. CommandParameters.Names(text)
            .Where(name => !CommandVariables.IsContextName(name))
            .Select(name => (Parameters ?? []).FirstOrDefault(
                                spec => string.Equals(spec.Name, name, StringComparison.OrdinalIgnoreCase))
                            ?? CommandParameterSpec.Plain(name))];
}

public sealed record GetDevelopmentCommands;

public sealed class GetDevelopmentCommandsHandler(IDevelopmentCommandRepository commands)
{
    public async Task<IReadOnlyList<DevelopmentCommandRow>> HandleAsync(
        GetDevelopmentCommands query,
        CancellationToken cancellationToken = default)
    {
        var all = await commands.ListAsync(cancellationToken);
        var bindings = await commands.CountBindingsAsync(cancellationToken);

        return all.Select(command => DevelopmentCommandRow.From(command, bindings.GetValueOrDefault(command.Id))).ToList();
    }
}

/// <param name="Settings">O comando rápido (ADR-051). <c>null</c> é o padrão: escondido, na raiz.</param>
public sealed record CreateDevelopmentCommand(
    string Alias,
    string Command,
    string? Description,
    DevelopmentCommandSettings? Settings = null);

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
            command.Settings ?? DevelopmentCommandSettings.Default,
            timeProvider.GetUtcNow());

        await commands.EnsureAliasIsFreeAsync(created.Alias, exceptId: null, cancellationToken);

        await commands.AddAsync(created, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("DevelopmentCommandCreated {CommandId} {Alias}", created.Id, created.Alias);

        return DevelopmentCommandRow.From(created);
    }
}

/// <param name="Settings">O comando rápido (ADR-051). <c>null</c> mantém o que está gravado.</param>
public sealed record UpdateDevelopmentCommand(
    Guid CommandId,
    string Alias,
    string Command,
    string? Description,
    DevelopmentCommandSettings? Settings = null);

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

        existing.Update(
            command.Alias,
            command.Command,
            command.Description,
            command.Settings ?? existing.Settings,
            timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("DevelopmentCommandUpdated {CommandId} {Alias}", existing.Id, existing.Alias);

        var bindings = await commands.CountBindingsAsync(cancellationToken);

        return DevelopmentCommandRow.From(existing, bindings.GetValueOrDefault(existing.Id));
    }
}

public sealed record DeleteDevelopmentCommand(Guid CommandId);

public sealed class DeleteDevelopmentCommandHandler(
    IDevelopmentCommandRepository commands,
    IUnitOfWork unitOfWork,
    ILogger<DeleteDevelopmentCommandHandler> logger)
{
    /// <remarks>
    /// Leva junto os botões dos diretórios que o ofereciam (cascata, ADR-051); o
    /// histórico de execuções fica, sem o vínculo. A tela avisa quantos antes.
    /// </remarks>
    public async Task HandleAsync(DeleteDevelopmentCommand command, CancellationToken cancellationToken = default)
    {
        var existing = await commands.GetByIdAsync(command.CommandId, cancellationToken);

        commands.Remove(existing);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation("DevelopmentCommandDeleted {CommandId} {Alias}", existing.Id, existing.Alias);
    }
}
