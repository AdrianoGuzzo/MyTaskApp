using System.Text.RegularExpressions;

namespace MyTaskApp.Domain.DatabaseOperations;

/// <summary>
/// Tira segredo de um texto que vai para log, tela ou auditoria (ADR-056):
/// senhas conhecidas, credencial em URI, pares <c>password=</c>/<c>token:</c>,
/// JSON e cabeçalho <c>Authorization</c>.
/// </summary>
/// <remarks>
/// <para>
/// É rede de segurança, não o mecanismo: a senha já não vai em argumento, em
/// connection string de log nem em mensagem montada pelo app. O que passa por
/// aqui é texto de <b>fora</b> — a saída do <c>pg_dump</c>, a mensagem de um
/// erro do servidor — onde não se controla o que vem.
/// </para>
/// <para>
/// Mora no Domain porque "a auditoria nunca guarda segredo" é uma invariante
/// da entidade de auditoria, e não um cuidado de quem a preenche.
/// </para>
/// </remarks>
public static partial class SensitiveText
{
    public const string Redacted = "***";

    /// <summary>Abaixo disto, trocar toda ocorrência destruiria o texto sem proteger nada.</summary>
    private const int MinKnownSecretLength = 3;

    public static string Mask(string? text, IEnumerable<string?>? knownSecrets = null)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        var masked = text;

        foreach (var secret in knownSecrets ?? [])
        {
            if (secret is { Length: >= MinKnownSecretLength })
            {
                masked = masked.Replace(secret, Redacted, StringComparison.Ordinal);
            }
        }

        masked = UriCredentials().Replace(masked, "${scheme}${user}:" + Redacted + "@");
        masked = KeyValue().Replace(masked, match => match.Groups["key"].Value + match.Groups["sep"].Value + Quoted(match.Groups["value"].Value));
        masked = Authorization().Replace(masked, "${scheme} " + Redacted);

        return masked;
    }

    /// <summary>Mantém as aspas do valor, para o JSON mascarado continuar JSON.</summary>
    private static string Quoted(string value) =>
        value.Length >= 2 && (value[0] == '"' || value[0] == '\'') ? $"{value[0]}{Redacted}{value[0]}" : Redacted;

    [GeneratedRegex(
        @"(?<scheme>\b[a-z][a-z0-9+.\-]*://)(?<user>[^:/@\s]+):(?<password>[^@\s]+)@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex UriCredentials();

    [GeneratedRegex(
        """(?<![A-Za-z0-9])(?<key>[A-Za-z0-9_\-]*(?:password|passwd|pwd|secret|token|api[_\-]?key))(?<sep>["']?\s*[:=]\s*)(?<value>"[^"]*"|'[^']*'|[^\s;,&"'}\]]+)""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex KeyValue();

    [GeneratedRegex(
        @"\b(?<scheme>Bearer|Basic)\s+[A-Za-z0-9\-._~+/]+=*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Authorization();
}
