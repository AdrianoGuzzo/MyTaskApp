using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Application.Commands;

/// <summary>Um <c>{nome}</c> que o app preenche, para o autocomplete do <c>{</c> (ADR-060).</summary>
/// <param name="Value">O valor neste ambiente, quando já se sabe; <c>null</c> antes do worktree ou da cópia.</param>
public sealed record CommandPlaceholder(string Name, string Description, string? Value = null, bool IsDatabaseAlias = false)
{
    /// <summary>Como entra no comando: <c>{lock_eco_core_1010}</c>.</summary>
    public string Placeholder => $"{{{Name}}}";
}

/// <summary>A lista do <c>{</c>: os apelidos de banco primeiro, depois as variáveis do ambiente.</summary>
public static class CommandPlaceholders
{
    public const string AliasDescription = "Banco copiado deste apelido";

    /// <summary>As variáveis fixas, com o valor de <paramref name="context"/> quando há.</summary>
    public static IReadOnlyList<CommandPlaceholder> Variables(CommandContext? context = null) =>
        [.. CommandVariables.All.Select(variable =>
            new CommandPlaceholder(variable.Name, variable.Description, context?.ValueOf(variable.Name)))];

    /// <summary>
    /// Tudo o que <paramref name="context"/> preenche. O apelido do banco
    /// copiado vem antes dos outros, com o nome do banco como valor.
    /// </summary>
    public static IReadOnlyList<CommandPlaceholder> For(CommandContext context) =>
    [
        .. context.DatabaseAliases
            .Select(alias => new CommandPlaceholder(alias, AliasDescription, context.ValueOf(alias), IsDatabaseAlias: true))
            .OrderBy(item => item.Value is null ? 1 : 0)
            .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase),
        .. Variables(context),
    ];
}
