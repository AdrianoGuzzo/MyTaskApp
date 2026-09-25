using System.Text;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Application.Commands;

/// <summary>
/// Uma entrada da lista já traduzida: <see cref="Command"/> é o que vai ao shell,
/// ou <c>null</c> com <see cref="Error"/> quando o apelido não existe ou falta
/// preencher um parâmetro (<see cref="MissingParameters"/>).
/// </summary>
public sealed record ResolvedCommand(
    int Index,
    string Entry,
    string? Command,
    string? Error,
    IReadOnlyList<string>? MissingParameters = null)
{
    public bool IsResolved => Command is not null;

    /// <summary>O apelido existe; o que faltou foi valor de parâmetro.</summary>
    public bool LacksParameters => MissingParameters is { Count: > 0 };
}

/// <summary>
/// Um comando global com os argumentos de quem chamou: <see cref="Command"/> é o
/// texto final, ou <c>null</c> quando falta algum dos <see cref="Missing"/>.
/// </summary>
public sealed record AliasExpansion(string? Command, IReadOnlyList<string> Missing)
{
    public bool IsComplete => Missing.Count == 0;
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
/// Parâmetros: <c>eco-sync {nomebanco} -Dev</c> chamado por
/// <c>@eco-sync nomebanco=MeuBanco</c> vira <c>eco-sync MeuBanco -Dev</c>. O valor
/// vai como escrito, aspas inclusas; o que não é <c>nome=valor</c> de um
/// parâmetro segue acrescentado ao final. Parâmetro sem valor é erro: o shell
/// nunca recebe <c>{nomebanco}</c>.
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

        var expansion = Expand(command, entry[alias.Length..]);

        if (!expansion.IsComplete)
        {
            return new ResolvedCommand(index, entry, null, MissingMessage(alias, expansion.Missing), expansion.Missing);
        }

        return new ResolvedCommand(index, entry, expansion.Command, null);
    }

    /// <summary>
    /// Preenche os <c>{nome}</c> de <paramref name="command"/> com os
    /// <c>nome=valor</c> de <paramref name="arguments"/> e acrescenta o resto.
    /// </summary>
    /// <param name="arguments">O que vem depois do apelido na linha.</param>
    public static AliasExpansion Expand(string command, string? arguments)
    {
        var names = CommandParameters.Names(command);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var rest = new List<string>();

        foreach (var token in Tokenize(arguments))
        {
            var equals = token.IndexOf('=');
            var name = equals > 0 ? token[..equals] : null;

            if (name is not null && names.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                values[name] = token[(equals + 1)..];
            }
            else
            {
                rest.Add(token);
            }
        }

        var missing = names
            .Where(name => !values.TryGetValue(name, out var value) || value.Length == 0)
            .ToList();

        if (missing.Count > 0)
        {
            return new AliasExpansion(null, missing);
        }

        var filled = CommandParameters.Fill(command, values);

        return new AliasExpansion(rest.Count == 0 ? filled : $"{filled} {string.Join(' ', rest)}", []);
    }

    /// <summary>Como chamar o apelido com os parâmetros: <c>@eco-sync nomebanco=…</c>.</summary>
    public static string UsageOf(string alias, IEnumerable<string> parameters) =>
        string.Join(' ', parameters.Select(name => $"{name}=…").Prepend(alias));

    private static string MissingMessage(string alias, IReadOnlyList<string> missing) =>
        missing.Count == 1
            ? $"{alias} precisa do parâmetro {missing[0]}. Escreva: {UsageOf(alias, missing)}"
            : $"{alias} precisa dos parâmetros {string.Join(", ", missing)}. Escreva: {UsageOf(alias, missing)}";

    /// <summary>Separa por espaço, sem partir o que está entre aspas duplas. Cada token volta como escrito.</summary>
    private static IEnumerable<string> Tokenize(string? text)
    {
        var token = new StringBuilder();
        var quoted = false;

        foreach (var character in text ?? string.Empty)
        {
            if (character == '"')
            {
                quoted = !quoted;
            }

            if (!quoted && char.IsWhiteSpace(character))
            {
                if (token.Length > 0)
                {
                    yield return token.ToString();
                    token.Clear();
                }

                continue;
            }

            token.Append(character);
        }

        if (token.Length > 0)
        {
            yield return token.ToString();
        }
    }
}
