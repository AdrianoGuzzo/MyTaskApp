using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Application.Commands;

internal static class GlobalCommandLookup
{
    /// <summary>Apelido → comando, lido agora: editar um global vale na próxima execução.</summary>
    public static async Task<IReadOnlyDictionary<string, string>> LoadAsync(
        IDevelopmentCommandRepository commands,
        CancellationToken cancellationToken)
    {
        var all = await commands.ListAsync(cancellationToken);

        // Só os globais, que sempre têm apelido: o comando do diretório não é chamado por @ (ADR-054).
        return all
            .Where(command => command.Alias is not null)
            .ToDictionary(command => command.Alias!, command => command.Command, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Recusa, numa mensagem só, com todos os apelidos que faltam e todos os
    /// parâmetros sem valor.
    /// </summary>
    public static void EnsureResolved(IReadOnlyList<ResolvedCommand> resolved)
    {
        var problems = new List<string>();

        var missing = resolved
            .Where(step => step.IsUnknownAlias)
            .Select(step => CommandAliasResolver.AliasOf(step.Entry))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (missing.Count == 1)
        {
            problems.Add($"O comando {missing[0]} não existe. Cadastre-o em Comandos globais ou corrija o apelido.");
        }

        if (missing.Count > 1)
        {
            problems.Add(
                $"Os comandos {string.Join(", ", missing)} não existem. "
                + "Cadastre-os em Comandos globais ou corrija os apelidos.");
        }

        problems.AddRange(resolved
            .Where(step => step.LacksParameters || step.HasUnsafeValue)
            .Select(step => $"Comando {step.Index + 1}: {step.Error}"));

        if (problems.Count > 0)
        {
            throw new DomainException(string.Join(Environment.NewLine, problems));
        }
    }
}

/// <summary>
/// Confere, sem rodar nada, que todo <c>@alias</c> da lista existe. A tela
/// pergunta antes de criar o worktree: descobrir o erro depois é ter um
/// ambiente pela metade.
/// </summary>
public sealed record ValidateCommandEntries(IReadOnlyList<string> Entries);

public sealed class ValidateCommandEntriesHandler(IDevelopmentCommandRepository commands)
{
    /// <summary>
    /// O worktree ainda não existe: as variáveis dele não podem faltar, e ficam
    /// para a execução (ADR-051). <c>{tag}</c> não: a lista pós-Worktree não vem
    /// de um diretório de etiqueta, então ninguém a preencheria.
    /// </summary>
    private static readonly CommandContext BeforeTheWorktree = CommandContext.Deferred(
        CommandVariables.All
            .Select(variable => variable.Name)
            .Where(name => name != CommandVariables.Tag));

    public async Task<IReadOnlyList<ResolvedCommand>> HandleAsync(
        ValidateCommandEntries query,
        CancellationToken cancellationToken = default)
    {
        var globals = await GlobalCommandLookup.LoadAsync(commands, cancellationToken);
        var resolved = CommandAliasResolver.Resolve(query.Entries, globals, BeforeTheWorktree);

        GlobalCommandLookup.EnsureResolved(resolved);

        return resolved;
    }
}

/// <summary>
/// "Testar" na tela de comandos globais: roda uma linha numa pasta escolhida.
/// Só por clique explícito — nada aqui roda sozinho.
/// </summary>
public sealed record RunCommand(string Entry, string WorkingDirectory);

public sealed class RunCommandHandler(
    IDevelopmentCommandRepository commands,
    ICommandExecutor executor,
    IDirectoryProbe directories,
    ILogger<RunCommandHandler> logger)
{
    public async Task<CommandRunSummary> HandleAsync(
        RunCommand command,
        IProgress<CommandStepProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command.Entry))
        {
            throw new DomainException("Informe o comando.");
        }

        var directory = await CommandDirectory.RequireAsync(directories, command.WorkingDirectory, cancellationToken);

        var globals = await GlobalCommandLookup.LoadAsync(commands, cancellationToken);
        var resolved = CommandAliasResolver.Resolve([command.Entry], globals, CommandContext.ForFolder(directory));

        var summary = await CommandSequence.RunAsync(resolved, directory, executor, progress, cancellationToken);

        foreach (var step in summary.Steps)
        {
            logger.LogInformation(
                "CommandTested {State} {ExitCode}",
                step.State,
                step.Result?.ExitCode);
        }

        return summary;
    }
}

internal static class CommandDirectory
{
    /// <summary>A pasta onde o comando vai rodar, conferida agora.</summary>
    public static async Task<string> RequireAsync(
        IDirectoryProbe directories,
        string? path,
        CancellationToken cancellationToken)
    {
        var directory = path?.Trim().Trim('"').Trim() ?? string.Empty;

        if (directory.Length == 0 || !Path.IsPathFullyQualified(directory))
        {
            throw new DomainException("Informe o caminho completo da pasta onde o comando vai rodar.");
        }

        if (!await directories.ExistsAsync(directory, cancellationToken))
        {
            throw new DomainException($"A pasta {directory} não existe.");
        }

        return directory;
    }
}
