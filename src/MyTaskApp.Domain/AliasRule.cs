using System.Text.RegularExpressions;

namespace MyTaskApp.Domain;

/// <summary>
/// A forma de um <c>@alias</c>, a mesma para diretórios de etiqueta (ADR-026) e
/// comandos globais (ADR-028): o autocomplete reconhece os mesmos caracteres nos
/// dois lugares.
/// </summary>
public static partial class AliasRule
{
    public const int MaxLength = 40;

    /// <summary>
    /// Apara e põe o <c>@</c> quando falta: quem digitou "eco-core" quis dizer
    /// "@eco-core". Depois do <c>@</c>, só letras, dígitos, ponto, hífen e
    /// sublinhado.
    /// </summary>
    /// <param name="missingMessage">A mensagem quando não sobra alias nenhum.</param>
    public static string Normalize(string? alias, string missingMessage)
    {
        var normalized = alias?.Trim() ?? string.Empty;

        if (normalized.Length > 0 && normalized[0] != '@')
        {
            normalized = "@" + normalized;
        }

        if (normalized.Length <= 1)
        {
            throw new DomainException(missingMessage);
        }

        if (normalized.Length > MaxLength)
        {
            throw new DomainException($"O alias não pode passar de {MaxLength} caracteres.");
        }

        if (!Pattern().IsMatch(normalized))
        {
            throw new DomainException(
                "O alias começa com @ e usa só letras, números, ponto, hífen e sublinhado.");
        }

        return normalized;
    }

    /// <summary>Se o texto, como está, é um alias válido (sem normalizar).</summary>
    public static bool IsAlias(string? text) => text is not null && Pattern().IsMatch(text);

    [GeneratedRegex("^@[A-Za-z0-9][A-Za-z0-9._-]*$")]
    private static partial Regex Pattern();
}
