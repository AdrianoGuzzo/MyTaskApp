using System.Globalization;

namespace MyTaskApp.Domain.External;

/// <summary>
/// Uma chave de issue no formato do Jira: <c>PROJETO-NÚMERO</c>
/// (<c>GAECO-1234</c>). Função pura, no molde do <c>AliasRule</c>: quem digita a
/// chave em vez do título precisa ser reconhecido sem ida à rede (ADR-045).
/// </summary>
/// <remarks>
/// O projeto começa com letra e segue com letras, dígitos e <c>_</c> — a regra
/// do Jira. Minúsculas são aceitas e viram maiúsculas: o Jira também ignora a
/// caixa na chave, e quem digita rápido não aperta Shift. Número sem zero à
/// esquerda, até nove dígitos.
/// </remarks>
public readonly record struct IssueKey
{
    public const int MaxProjectLength = 255;

    private const int MaxNumberDigits = 9;

    private static readonly char[] TitleSeparators = [' ', '-', ':', '–', '—'];

    private IssueKey(string project, long number)
    {
        Project = project;
        Number = number;
    }

    public string Project { get; }

    public long Number { get; }

    public override string ToString() => $"{Project}-{Number.ToString(CultureInfo.InvariantCulture)}";

    /// <summary>O texto inteiro (sem os espaços das pontas) é uma chave.</summary>
    public static bool TryParse(string? text, out IssueKey key)
    {
        key = default;

        var trimmed = text.AsSpan().Trim();
        var dash = trimmed.LastIndexOf('-');

        if (dash <= 0 || dash > MaxProjectLength)
        {
            return false;
        }

        var project = trimmed[..dash];
        var digits = trimmed[(dash + 1)..];

        if (!IsProject(project) || !IsNumber(digits))
        {
            return false;
        }

        key = new IssueKey(
            project.ToString().ToUpperInvariant(),
            long.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture));

        return true;
    }

    /// <summary>
    /// A linha começa com uma chave: <c>GAECO-1234 Corrigir erro</c>. Devolve o
    /// resto sem os separadores comuns (<c>-</c>, <c>:</c>, travessão).
    /// </summary>
    public static bool TryParsePrefix(string? line, out IssueKey key, out string rest)
    {
        key = default;
        rest = string.Empty;

        var trimmed = (line ?? string.Empty).Trim();
        var end = trimmed.IndexOfAny([' ', ':', '\t']);
        var candidate = end < 0 ? trimmed : trimmed[..end];

        if (!TryParse(candidate, out key))
        {
            return false;
        }

        rest = end < 0 ? string.Empty : trimmed[end..].TrimStart(TitleSeparators).Trim();
        return true;
    }

    private static bool IsProject(ReadOnlySpan<char> project)
    {
        if (!char.IsAsciiLetter(project[0]))
        {
            return false;
        }

        foreach (var c in project)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '_')
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsNumber(ReadOnlySpan<char> digits) =>
        digits.Length is > 0 and <= MaxNumberDigits
        && digits[0] != '0'
        && !digits.ContainsAnyExcept("0123456789");
}
