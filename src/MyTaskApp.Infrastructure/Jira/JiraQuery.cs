using System.Text;
using MyTaskApp.Domain.External;

namespace MyTaskApp.Infrastructure.Jira;

/// <summary>
/// O JQL da busca do autocomplete (ADR-045). Função pura, para cada regra
/// virar um teste — inclusive a que importa: o texto do usuário nunca vira JQL.
/// </summary>
internal static class JiraQuery
{
    public const string Fields = "summary,issuetype,status,project";

    /// <summary>
    /// <c>summary ~ "corrigir erro*"</c>, do mais recente para o mais antigo.
    /// Só letras e dígitos sobrevivem: aspas, barras e os operadores do Lucene
    /// (<c>+ - &amp;&amp; || ! ( ) { } [ ] ^ ~ * ? :</c>) viram espaço, então não
    /// há como fechar a string e emendar outra cláusula. O <c>*</c> no fim deixa
    /// a última palavra, ainda sendo digitada, valer pelo começo.
    /// </summary>
    /// <returns><c>null</c> quando não sobra palavra nenhuma para procurar.</returns>
    public static string? ForText(string text, string? defaultProject)
    {
        var words = Words(text);

        if (words.Count == 0)
        {
            return null;
        }

        var clause = $"summary ~ \"{string.Join(' ', words)}*\"";

        if (ProjectKey(defaultProject) is { } project)
        {
            clause = $"project = \"{project}\" AND {clause}";
        }

        return clause + " ORDER BY updated DESC";
    }

    /// <summary>
    /// "1234" com projeto padrão <c>GAECO</c> é <c>GAECO-1234</c>: quem trabalha
    /// num projeto só digita o número.
    /// </summary>
    public static string? KeyFromNumber(string text, string? defaultProject) =>
        ProjectKey(defaultProject) is { } project
        && IssueKey.TryParse($"{project}-{text.Trim()}", out var key)
            ? key.ToString()
            : null;

    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new StringBuilder();

        foreach (var c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                current.Append(c);
            }
            else if (current.Length > 0)
            {
                words.Add(current.ToString());
                current.Clear();
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }

    private static string? ProjectKey(string? project) =>
        project is { Length: > 0 } && IssueKey.TryParse($"{project}-1", out var key) ? key.Project : null;
}
