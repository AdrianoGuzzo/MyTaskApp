using System.Globalization;
using System.Text.RegularExpressions;
using MyTaskApp.Application.Development;

namespace MyTaskApp.Infrastructure.Git;

/// <summary>
/// A saída do Git, virando dado. Puro e isolado para os testes cobrirem cada
/// formato sem processo nenhum. Os formatos escolhidos são os de máquina
/// (<c>--porcelain</c>, <c>-z</c>, <c>%00</c>): o texto para humanos muda de
/// versão para versão e com o idioma.
/// </summary>
internal static partial class GitOutputParser
{
    /// <summary>"git version 2.51.0.windows.1" vira "2.51.0.windows.1".</summary>
    public static string? ParseVersion(string output) =>
        VersionPattern().Match(output) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>
    /// Linhas de <c>for-each-ref --format=%(refname)%00%(upstream)%00%(HEAD)</c>.
    /// Os <c>origin/HEAD</c> saem: são apelidos, não branches.
    /// </summary>
    public static IReadOnlyList<GitBranch> ParseBranches(string output)
    {
        var branches = new List<GitBranch>();

        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var fields = line.Split('\0');
            var fullRef = fields[0];
            var upstream = fields.Length > 1 && fields[1].Length > 0 ? fields[1] : null;
            var isHead = fields.Length > 2 && fields[2] == "*";

            if (fullRef.StartsWith(GitBranch.LocalPrefix, StringComparison.Ordinal))
            {
                var name = fullRef[GitBranch.LocalPrefix.Length..];
                branches.Add(new GitBranch(fullRef, name, false, null, upstream, isHead));
            }
            else if (fullRef.StartsWith(GitBranch.RemotePrefix, StringComparison.Ordinal))
            {
                var shortName = fullRef[GitBranch.RemotePrefix.Length..];
                var slash = shortName.IndexOf('/', StringComparison.Ordinal);

                if (slash <= 0 || shortName[(slash + 1)..] == "HEAD")
                {
                    continue;
                }

                branches.Add(new GitBranch(fullRef, shortName, true, shortName[..slash], null, false));
            }
        }

        return branches;
    }

    /// <summary>Os blocos de <c>git worktree list --porcelain</c>, na ordem — o primeiro é o principal.</summary>
    public static IReadOnlyList<GitWorktree> ParseWorktrees(string output)
    {
        var worktrees = new List<GitWorktree>();
        GitWorktree? current = null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.TrimEnd('\r');

            if (line.Length == 0)
            {
                Flush();
                continue;
            }

            var space = line.IndexOf(' ', StringComparison.Ordinal);
            var key = space < 0 ? line : line[..space];
            var value = space < 0 ? string.Empty : line[(space + 1)..];

            switch (key)
            {
                case "worktree":
                    Flush();
                    current = new GitWorktree(value, null);
                    break;
                case "branch" when current is not null:
                    current = current with { BranchRef = value };
                    break;
                case "detached" when current is not null:
                    current = current with { IsDetached = true };
                    break;
                case "bare" when current is not null:
                    current = current with { IsBare = true };
                    break;
                case "locked" when current is not null:
                    current = current with { IsLocked = true };
                    break;
                case "prunable" when current is not null:
                    current = current with { IsPrunable = true };
                    break;
            }
        }

        Flush();
        return worktrees;

        void Flush()
        {
            if (current is not null)
            {
                worktrees.Add(current);
                current = null;
            }
        }
    }

    /// <summary>
    /// Registros de <c>git ls-files -z</c>. Um arquivo em conflito aparece uma
    /// vez por estágio, e sai uma vez só.
    /// </summary>
    public static IReadOnlyList<string> ParseFileList(string output) =>
        [.. output.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)];

    /// <summary>
    /// Registros de <c>git status --porcelain=v1 -z</c>. Renomeação e cópia
    /// trazem um registro extra com o nome antigo, que é pulado.
    /// </summary>
    public static IReadOnlyList<string> ParseStatus(string output)
    {
        var entries = new List<string>();
        var records = output.Split('\0');

        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];

            if (record.Length < 4)
            {
                continue;
            }

            entries.Add(record);

            if (record[0] is 'R' or 'C')
            {
                index++;
            }
        }

        return entries;
    }

    /// <summary>
    /// Registros de <c>git status --porcelain=v2 --branch -z</c>. Os cabeçalhos
    /// <c># branch.upstream</c> e <c># branch.ab +N -M</c> dão o upstream e a
    /// distância; o resto são alterações. Renomeação (<c>2 …</c>) traz um
    /// registro extra com o nome antigo, que é pulado.
    /// </summary>
    public static GitBranchStatus ParseBranchStatus(string output)
    {
        const string UpstreamHeader = "# branch.upstream ";
        const string DivergenceHeader = "# branch.ab ";

        var changes = new List<string>();
        string? upstream = null;
        var ahead = 0;
        var behind = 0;

        var records = output.Split('\0');

        for (var index = 0; index < records.Length; index++)
        {
            var record = records[index];

            if (record.Length == 0)
            {
                continue;
            }

            if (record.StartsWith(UpstreamHeader, StringComparison.Ordinal))
            {
                upstream = record[UpstreamHeader.Length..].Trim();
            }
            else if (record.StartsWith(DivergenceHeader, StringComparison.Ordinal))
            {
                var parts = record[DivergenceHeader.Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length == 2
                    && int.TryParse(parts[0].TrimStart('+'), NumberStyles.None, CultureInfo.InvariantCulture, out var left)
                    && int.TryParse(parts[1].TrimStart('-'), NumberStyles.None, CultureInfo.InvariantCulture, out var right))
                {
                    ahead = left;
                    behind = right;
                }
            }
            else if (record[0] is '1' or '2' or 'u' or '?')
            {
                changes.Add(record);

                if (record[0] == '2')
                {
                    index++;
                }
            }
        }

        return new GitBranchStatus(changes, upstream, ahead, behind);
    }

    /// <summary>"3\t5" de <c>rev-list --left-right --count</c>: 3 só na esquerda, 5 só na direita.</summary>
    public static GitDivergence ParseDivergence(string output)
    {
        var parts = output.Split(['\t', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        return parts.Length == 2
            && int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var ahead)
            && int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var behind)
            ? new GitDivergence(ahead, behind)
            : throw new FormatException($"Saída inesperada do rev-list: \"{output.Trim()}\".");
    }

    [GeneratedRegex(@"^git version (\S+)", RegexOptions.Multiline)]
    private static partial Regex VersionPattern();
}
