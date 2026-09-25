using System.Text.RegularExpressions;

namespace MyTaskApp.Domain.Commands;

/// <summary>
/// Os parâmetros de um comando global: <c>{nome}</c> no texto, preenchido por
/// quem chama com <c>@alias nome=valor</c> (ADR-028).
/// </summary>
/// <remarks>
/// O nome começa com letra ou sublinhado e não tem espaço: <c>{ $_ }</c>,
/// <c>${env:X}</c> e <c>HEAD@{1}</c> são do shell e seguem literais.
/// </remarks>
public static partial class CommandParameters
{
    /// <summary>Os nomes, na ordem em que aparecem, sem repetir.</summary>
    public static IReadOnlyList<string> Names(string? command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return [];
        }

        return [.. Placeholder().Matches(command)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>O nome é de um parâmetro (a forma que <c>{nome}</c> aceita).</summary>
    public static bool IsName(string? name) => name is not null && Name().IsMatch(name);

    /// <summary>Troca cada <c>{nome}</c> pelo valor, como escrito. Sem valor, fica como está.</summary>
    public static string Fill(string command, IReadOnlyDictionary<string, string> values) =>
        Placeholder().Replace(
            command,
            match => values.TryGetValue(match.Groups["name"].Value, out var value) ? value : match.Value);

    [GeneratedRegex(@"\{(?<name>[A-Za-z_][A-Za-z0-9_-]*)\}")]
    private static partial Regex Placeholder();

    [GeneratedRegex(@"^[A-Za-z_][A-Za-z0-9_-]*$")]
    private static partial Regex Name();
}
