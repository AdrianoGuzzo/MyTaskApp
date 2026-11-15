using System.Text;
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

        masked = UriCredentialsPattern.Replace(masked, "${scheme}${user}:" + Redacted + "@");
        masked = MaskKeyValues(masked);
        masked = Authorization().Replace(masked, "${scheme} " + Redacted);

        return masked;
    }

    /// <summary>Mantém as aspas do valor, para o JSON mascarado continuar JSON.</summary>
    private static string Quoted(string value) =>
        value.Length >= 2 && (value[0] == '"' || value[0] == '\'') ? $"{value[0]}{Redacted}{value[0]}" : Redacted;

    // O motor sem retrocesso mantém a busca linear mesmo em textos longos sem URI.
    private static readonly Regex UriCredentialsPattern = new(
        @"(?<scheme>\b[a-z][a-z0-9+.\-]*://)(?<user>[^:/@\s]+):(?<password>[^@\s]+)@",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));

    // Percorre as chaves uma vez e só lê o valor quando o sufixo indica segredo.
    private static readonly Regex KeyValuePattern = new(
        """(?<key>[A-Za-z0-9_\-]+)(?<sep>["']?\s*[:=]\s*)""",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromSeconds(1));

    [GeneratedRegex(
        """\G("[^"]*"|'[^']*'|[^\s;,&"'}\]]+)""",
        RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex SecretValue();

    private static string MaskKeyValues(string text)
    {
        var result = new StringBuilder(text.Length);
        var copiedUntil = 0;
        for (var pair = KeyValuePattern.Match(text); pair.Success;)
        {
            var next = pair.Index + pair.Length;
            if (IsSecretKey(pair.Groups["key"].Value))
            {
                var value = SecretValue().Match(text, next);
                if (value.Success)
                {
                    result.Append(text, copiedUntil, next - copiedUntil);
                    result.Append(Quoted(value.Value));
                    next += value.Length;
                    copiedUntil = next;
                }
            }

            pair = KeyValuePattern.Match(text, next);
        }

        return result.Append(text, copiedUntil, text.Length - copiedUntil).ToString();
    }

    private static bool IsSecretKey(string key) =>
        key.EndsWith("password", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("passwd", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("pwd", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("secret", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("token", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("apikey", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("api_key", StringComparison.OrdinalIgnoreCase)
        || key.EndsWith("api-key", StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(
        @"\b(?<scheme>Bearer|Basic)\s+[A-Za-z0-9\-._~+/]+=*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        matchTimeoutMilliseconds: 1000)]
    private static partial Regex Authorization();
}
