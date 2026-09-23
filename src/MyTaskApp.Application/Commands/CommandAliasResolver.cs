using MyTaskApp.Domain;

namespace MyTaskApp.Application.Commands;

/// <summary>
/// Uma entrada da lista já traduzida: <see cref="Command"/> é o que vai ao shell,
/// ou <c>null</c> com <see cref="Error"/> quando o apelido não existe.
/// </summary>
public sealed record ResolvedCommand(int Index, string Entry, string? Command, string? Error)
{
    public bool IsResolved => Command is not null;
}

/// <summary>
/// Troca <c>@alias</c> pelo comando global (ADR-028). Puro: recebe os comandos
/// cadastrados, não pergunta ao banco.
/// </summary>
/// <remarks>
/// <para>
/// Só o <b>primeiro</b> termo é olhado. <c>@build -c Release</c> vira
/// <c>dotnet build -c Release</c>; um <c>@</c> no meio do texto é do usuário e
/// segue como está. Comando global não chama outro — sem recursão, sem ciclo.
/// </para>
/// <para>
/// Uma entrada com forma de alias que não existe é erro, e não literal: mandar
/// <c>@restor</c> ao shell daria outro erro, mais confuso. O que começa com
/// <c>@</c> sem forma de alias (<c>@ x</c>) segue literal.
/// </para>
/// </remarks>
public static class CommandAliasResolver
{
    /// <param name="entries">A lista como o usuário digitou. Em branco é ignorada.</param>
    /// <param name="globals">Apelido (com <c>@</c>) → comando.</param>
    public static IReadOnlyList<ResolvedCommand> Resolve(
        IReadOnlyList<string> entries,
        IReadOnlyDictionary<string, string> globals)
    {
        var lookup = new Dictionary<string, string>(globals, StringComparer.OrdinalIgnoreCase);
        var resolved = new List<ResolvedCommand>();

        foreach (var raw in entries)
        {
            var entry = raw?.Trim() ?? string.Empty;

            if (entry.Length == 0)
            {
                continue;
            }

            resolved.Add(ResolveOne(resolved.Count, entry, lookup));
        }

        return resolved;
    }

    /// <summary>O <c>@alias</c> que abre a entrada, ou <c>null</c> se ela é literal.</summary>
    public static string? AliasOf(string? entry)
    {
        var text = entry?.Trim() ?? string.Empty;

        if (text.Length == 0 || text[0] != '@')
        {
            return null;
        }

        var end = text.IndexOfAny([' ', '\t']);
        var head = end < 0 ? text : text[..end];

        return AliasRule.IsAlias(head) ? head : null;
    }

    private static ResolvedCommand ResolveOne(int index, string entry, Dictionary<string, string> lookup)
    {
        if (AliasOf(entry) is not { } alias)
        {
            return new ResolvedCommand(index, entry, entry, null);
        }

        if (!lookup.TryGetValue(alias, out var command))
        {
            return new ResolvedCommand(
                index,
                entry,
                null,
                $"O comando {alias} não existe. Cadastre-o em Comandos globais ou corrija o apelido.");
        }

        var arguments = entry[alias.Length..].Trim();

        return new ResolvedCommand(
            index,
            entry,
            arguments.Length == 0 ? command : $"{command} {arguments}",
            null);
    }
}
