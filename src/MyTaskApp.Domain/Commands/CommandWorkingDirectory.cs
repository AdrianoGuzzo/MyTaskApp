namespace MyTaskApp.Domain.Commands;

/// <summary>
/// A pasta onde um comando rápido roda, relativa ao worktree (ADR-051):
/// <c>src/Eco.Web</c>. O worktree muda a cada tarefa, então a pasta nunca é um
/// caminho absoluto.
/// </summary>
public static class CommandWorkingDirectory
{
    public const int MaxLength = 1024;

    /// <summary>A raiz do worktree, dita explicitamente (num override, "usar a raiz").</summary>
    public const string Root = ".";

    private static readonly char[] InvalidCharacters = ['<', '>', '|', '"', '?', '*'];

    private static readonly string[] RootPrefixes =
        ["{" + CommandVariables.WorktreePath + "}", "{" + CommandVariables.Worktree + "}"];

    /// <summary>
    /// Em branco é <c>null</c>; a raiz (<c>.</c>, <c>{worktree}</c>) é
    /// <see cref="Root"/>; o resto vira um caminho relativo com <c>/</c>, sem
    /// <c>./</c> no começo nem barra no fim. <c>{worktree}/src</c> vale
    /// <c>src</c>. Recusa caminho absoluto, de unidade (<c>C:x</c>) e o que sai do
    /// worktree (<c>..</c>).
    /// </summary>
    public static string? Normalize(string? path)
    {
        var text = path?.Trim().Trim('"').Trim() ?? string.Empty;

        if (text.Length == 0)
        {
            return null;
        }

        if (text.Length > MaxLength)
        {
            throw new DomainException($"A pasta não pode passar de {MaxLength} caracteres.");
        }

        var fromWorktree = false;

        foreach (var prefix in RootPrefixes)
        {
            if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                text = text[prefix.Length..];
                fromWorktree = true;
                break;
            }
        }

        text = text.Replace('\\', '/');

        // "/src" sozinho é a raiz do disco; depois de {worktree} é só a barra que separa.
        if (text.StartsWith('/') && !fromWorktree)
        {
            throw Absolute();
        }

        if (text.Length >= 2 && char.IsAsciiLetter(text[0]) && text[1] == ':')
        {
            throw Absolute();
        }

        if (text.Contains('{', StringComparison.Ordinal) || text.Contains('}', StringComparison.Ordinal))
        {
            throw new DomainException("Na pasta, só {worktree} no começo é aceito.");
        }

        if (text.IndexOfAny(InvalidCharacters) >= 0 || text.Any(char.IsControl))
        {
            throw new DomainException("A pasta tem caracteres que não podem estar num caminho.");
        }

        var segments = new List<string>();

        foreach (var segment in text.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = segment.Trim();

            if (trimmed is "." or "")
            {
                continue;
            }

            if (trimmed == "..")
            {
                throw new DomainException("A pasta precisa ficar dentro do worktree: sem \"..\".");
            }

            segments.Add(trimmed);
        }

        return segments.Count == 0 ? Root : string.Join('/', segments);
    }

    /// <summary>Como <see cref="Normalize"/>, mas a raiz vira <c>null</c>: num comando global, raiz é o padrão.</summary>
    public static string? NormalizeDefault(string? path) =>
        Normalize(path) is { } normalized && normalized != Root ? normalized : null;

    private static DomainException Absolute() =>
        new("Informe a pasta relativa ao worktree (ex.: src/Eco.Web), e não um caminho completo.");
}
