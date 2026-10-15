namespace MyTaskApp.Domain.Tags;

/// <summary>
/// O endereço de um repositório Git, como o <c>git clone</c> e o
/// <c>git remote get-url</c> o escrevem (ADR-064): <c>https://…</c>,
/// <c>ssh://…</c>, <c>git://…</c>, <c>file://…</c> ou o curto do SSH,
/// <c>git@github.com:empresa/projeto.git</c>.
/// </summary>
public static class GitRemoteUrl
{
    public const int MaxLength = 2048;

    private static readonly string[] Schemes = ["https", "http", "ssh", "git", "file"];

    /// <summary>
    /// Apara e confere a forma. Tira usuário e senha de <c>http(s)://</c>: um
    /// <c>https://token@github.com/…</c> copiado do remoto iria para o banco e
    /// para os outros computadores (ADR-061), e o Git pergunta a credencial
    /// pelo gerenciador dele de qualquer jeito. Em branco é <c>null</c>.
    /// </summary>
    public static string? Normalize(string? url)
    {
        var normalized = url?.Trim().Trim('"').Trim();

        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Length > MaxLength)
        {
            throw new DomainException($"A URL do repositório não pode passar de {MaxLength} caracteres.");
        }

        if (normalized.Any(char.IsWhiteSpace))
        {
            throw new DomainException("A URL do repositório não pode ter espaços.");
        }

        // "-" no começo o Git leria como opção; nenhum endereço começa assim.
        if (normalized[0] == '-' || !(HasScheme(normalized) || IsScpLike(normalized)))
        {
            throw new DomainException(
                "Informe a URL do repositório (ex.: https://github.com/empresa/projeto.git ou git@github.com:empresa/projeto.git).");
        }

        return WithoutHttpCredentials(normalized);
    }

    /// <summary>
    /// A pasta que o <c>git clone</c> criaria: o último trecho do caminho, sem
    /// <c>.git</c>. <c>null</c> quando a URL não tem caminho (só o servidor).
    /// </summary>
    public static string? FolderName(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        var path = url.Trim().TrimEnd('/', '\\');
        var schemeEnd = path.IndexOf("://", StringComparison.Ordinal);

        if (schemeEnd >= 0)
        {
            var afterAuthority = path.IndexOf('/', schemeEnd + 3);

            if (afterAuthority < 0)
            {
                return null;
            }

            path = path[(afterAuthority + 1)..];
        }
        else if (IsScpLike(path))
        {
            path = path[(path.IndexOf(':', StringComparison.Ordinal) + 1)..];
        }

        var name = path[(path.LastIndexOfAny(['/', '\\', ':']) + 1)..];

        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        name = new string([.. name.Where(c => !Path.GetInvalidFileNameChars().Contains(c))]).Trim('.', ' ');

        return name.Length == 0 ? null : name;
    }

    /// <summary>
    /// Mesma URL? Sem diferença de maiúsculas, de <c>.git</c> no fim nem de
    /// barra final: <c>https://github.com/a/b</c> e <c>…/a/b.git</c> são o mesmo
    /// repositório, e trocar uma pela outra só mexeria no banco à toa.
    /// </summary>
    public static bool AreSame(string? left, string? right) =>
        string.Equals(Canonical(left), Canonical(right), StringComparison.OrdinalIgnoreCase);

    private static string? Canonical(string? url)
    {
        var value = url?.Trim().TrimEnd('/');

        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        return value.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;
    }

    private static bool HasScheme(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);

        return schemeEnd > 0
               && url.Length > schemeEnd + 3
               && Schemes.Contains(url[..schemeEnd], StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// <c>[usuário@]servidor:caminho</c>, o curto do SSH. Como no Git, com
    /// <c>://</c> é URL, e não o curto: <c>ftp://…</c> não vira servidor
    /// "ftp". Uma letra antes do <c>:</c> é unidade do Windows (<c>C:\…</c>).
    /// </summary>
    private static bool IsScpLike(string url)
    {
        var colon = url.IndexOf(':', StringComparison.Ordinal);
        var slash = url.IndexOfAny(['/', '\\']);

        if (colon <= 0
            || colon == url.Length - 1
            || (slash >= 0 && slash < colon)
            || url.Contains("://", StringComparison.Ordinal))
        {
            return false;
        }

        var host = url[(url.IndexOf('@', StringComparison.Ordinal) + 1)..colon];

        return host.Length > 1;
    }

    private static string WithoutHttpCredentials(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);

        if (schemeEnd < 0 || !url[..schemeEnd].StartsWith("http", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        var authorityStart = schemeEnd + 3;
        var authorityEnd = url.IndexOf('/', authorityStart);
        var authority = authorityEnd < 0 ? url[authorityStart..] : url[authorityStart..authorityEnd];
        var at = authority.LastIndexOf('@');

        return at < 0
            ? url
            : url[..authorityStart] + authority[(at + 1)..] + (authorityEnd < 0 ? string.Empty : url[authorityEnd..]);
    }
}
