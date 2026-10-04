namespace MyTaskApp.Application.Development;

/// <summary>
/// Um repositório do github.com, como o <c>gh</c> o chama: <c>github.com/dono/nome</c>
/// (ADR-047). Sai da URL do remoto — <c>https</c>, <c>ssh://</c> ou o atalho
/// <c>git@github.com:dono/nome.git</c>.
/// </summary>
/// <remarks>
/// Só o github.com: GitHub Enterprise tem host próprio e login próprio no
/// <c>gh</c>, e fica para quando alguém precisar.
/// </remarks>
public sealed record GitHubRepository(string Owner, string Name)
{
    public const string Host = "github.com";

    /// <summary>O <c>-R</c> do <c>gh</c>: <c>github.com/dono/nome</c>.</summary>
    public string Slug => $"{Host}/{Owner}/{Name}";

    public static GitHubRepository? TryParse(string? remoteUrl)
    {
        var url = remoteUrl?.Trim();

        if (string.IsNullOrEmpty(url))
        {
            return null;
        }

        string host;
        string path;

        if (url.Contains("://", StringComparison.Ordinal))
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                return null;
            }

            host = uri.Host;
            path = uri.AbsolutePath;
        }
        else
        {
            // O atalho do scp: [usuário@]host:caminho.
            var colon = url.IndexOf(':', StringComparison.Ordinal);

            if (colon <= 0)
            {
                return null;
            }

            host = url[..colon];
            host = host[(host.IndexOf('@', StringComparison.Ordinal) + 1)..];
            path = url[(colon + 1)..];
        }

        if (!string.Equals(host, Host, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var parts = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts is not [var owner, var name])
        {
            return null;
        }

        if (name.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        return name.Length > 0 ? new GitHubRepository(owner, name) : null;
    }
}
