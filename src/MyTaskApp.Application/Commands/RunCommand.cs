using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Sync;
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

        // Só os globais, que sempre têm apelido: o comando do diretório não é chamado por @ (ADR-055).
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
/// <param name="DatabaseScheduled">
/// Um banco será copiado para o ambiente antes dos comandos (ADR-060): só então
/// <c>{database}</c> e as irmãs podem aparecer na lista.
/// </param>
/// <param name="DatabaseAlias">
/// O apelido de onde a cópia agendada vem: <c>{apelido}</c> fica para a
/// execução; os outros apelidos são recusados com o motivo.
/// </param>
public sealed record ValidateCommandEntries(
    IReadOnlyList<string> Entries,
    bool DatabaseScheduled = false,
    string? DatabaseAlias = null);

public sealed class ValidateCommandEntriesHandler(
    IDevelopmentCommandRepository commands,
    ISavedDatabaseRepository savedDatabases)
{
    private const string NoCopyReason =
        "nenhum banco será copiado para este ambiente. Marque \"Copiar banco para este ambiente\" ou tire a variável.";

    /// <summary>
    /// O worktree ainda não existe: as variáveis dele não podem faltar, e ficam
    /// para a execução (ADR-051). <c>{tag}</c> não: a lista pós-Worktree não vem
    /// de um diretório de etiqueta, então ninguém a preencheria.
    /// </summary>
    private static readonly IReadOnlyList<string> WorktreeVariables = [.. CommandVariables.All
        .Select(variable => variable.Name)
        .Where(name => name != CommandVariables.Tag && !CommandVariables.DatabaseNames.Contains(name))];

    public async Task<IReadOnlyList<ResolvedCommand>> HandleAsync(
        ValidateCommandEntries query,
        CancellationToken cancellationToken = default)
    {
        var globals = await GlobalCommandLookup.LoadAsync(commands, cancellationToken);
        var aliases = await DatabaseAliasNames.ListAsync(savedDatabases, cancellationToken);
        var resolved = CommandAliasResolver.Resolve(query.Entries, globals, ContextFor(query, aliases));

        GlobalCommandLookup.EnsureResolved(resolved);

        return resolved;
    }

    /// <summary>Com a cópia marcada, o banco — e o apelido dela — também ficam para a execução.</summary>
    private static CommandContext ContextFor(ValidateCommandEntries query, IReadOnlyList<string> aliases)
    {
        var scheduledAlias = query.DatabaseScheduled ? query.DatabaseAlias : null;

        var otherReason = !query.DatabaseScheduled
            ? NoCopyReason
            : scheduledAlias is null
                ? "a cópia marcada não vem de um apelido. Use {database}, ou escolha a cópia por este apelido."
                : $"a cópia marcada vem do apelido {scheduledAlias}. Use {{{scheduledAlias}}} ou {{database}}.";

        var aliasVariables = aliases
            .Where(alias => !string.Equals(alias, scheduledAlias, StringComparison.OrdinalIgnoreCase))
            .Select(alias => new DatabaseAliasVariable(alias, UnavailableReason: otherReason))
            .ToList();

        if (scheduledAlias is not null)
        {
            aliasVariables.Add(new DatabaseAliasVariable(scheduledAlias));
        }

        return query.DatabaseScheduled
            ? CommandContext.Deferred(
                WorktreeVariables.Concat(CommandVariables.DatabaseNames),
                new Dictionary<string, string>(),
                aliasVariables)
            : CommandContext.Deferred(WorktreeVariables, CommandContext.DatabaseUnavailable(NoCopyReason), aliasVariables);
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
    ILogger<RunCommandHandler> logger,
    ICommandReviewGate? reviewGate = null)
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

        // Global que veio de outro computador não roda sem revisão (ADR-061).
        if (reviewGate is not null)
        {
            await reviewGate.EnsureReviewedAsync(
                [.. CommandReviewSources.GlobalsUsedBy([command.Entry], await commands.ListAsync(cancellationToken))],
                cancellationToken);
        }

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
