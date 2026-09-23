using System.Text;
using MyTaskApp.Domain;

namespace MyTaskApp.Application.Development;

/// <summary>
/// Onde o worktree da tarefa nasce: sempre ao lado do repositório, nunca dentro
/// dele — <c>../{projeto}-{branch-sanitizada}</c> (ADR-027).
/// </summary>
/// <remarks>
/// A branch nunca vira caminho sem passar por <see cref="SanitizeSegment"/>:
/// <c>feature/123-x</c> cru daria <c>../projeto-feature/123-x</c>, uma pasta
/// dentro de outra. As regras são as do Windows em qualquer sistema, para o
/// mesmo repositório dar o mesmo nome de pasta em qualquer máquina.
/// </remarks>
public static class WorktreePathPlanner
{
    public const int MaxSegmentLength = 80;

    private const string InvalidChars = "<>:\"|?*/\\";

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// <c>C:\Projects\ecossistema-core</c> + <c>feature/123-x</c> =
    /// <c>C:\Projects\ecossistema-core-feature-123-x</c>.
    /// </summary>
    public static string Plan(string repositoryPath, string branch)
    {
        var repository = TrimSeparators(repositoryPath);
        var parent = Path.GetDirectoryName(repository);
        var project = Path.GetFileName(repository);

        if (string.IsNullOrEmpty(parent) || string.IsNullOrEmpty(project))
        {
            throw new DomainException(
                "O repositório está na raiz do disco: não há pasta ao lado dele para o worktree.");
        }

        var folder = $"{project}-{SanitizeSegment(branch)}";

        if (ReservedNames.Contains(folder.Split('.')[0]))
        {
            folder += "-wt";
        }

        return Path.Combine(parent, folder);
    }

    /// <summary>
    /// Um nome de pasta seguro a partir da branch, preservando o máximo dela:
    /// barras e caracteres inválidos viram hífen, <c>..</c> vira ponto, e pontas
    /// com ponto, espaço ou hífen saem.
    /// </summary>
    public static string SanitizeSegment(string? branch)
    {
        var builder = new StringBuilder((branch ?? string.Empty).Length);

        foreach (var c in (branch ?? string.Empty).Trim())
        {
            builder.Append(char.IsControl(c) || char.IsWhiteSpace(c) || InvalidChars.Contains(c, StringComparison.Ordinal)
                ? '-'
                : c);
        }

        var segment = builder.ToString();

        while (segment.Contains("..", StringComparison.Ordinal))
        {
            segment = segment.Replace("..", ".", StringComparison.Ordinal);
        }

        while (segment.Contains("--", StringComparison.Ordinal))
        {
            segment = segment.Replace("--", "-", StringComparison.Ordinal);
        }

        segment = segment.Trim('-', '.', ' ');

        if (segment.Length > MaxSegmentLength)
        {
            segment = segment[..MaxSegmentLength].TrimEnd('-', '.', ' ');
        }

        if (segment.Length == 0)
        {
            return "worktree";
        }

        return ReservedNames.Contains(segment.Split('.')[0]) ? segment + "-wt" : segment;
    }

    /// <summary>
    /// A próxima variação livre: <c>…-2</c>, <c>…-3</c>. É a sugestão de "Escolher
    /// outro caminho" quando o calculado já está ocupado.
    /// </summary>
    public static async Task<string> NextFreeAsync(
        string path,
        Func<string, Task<bool>> exists)
    {
        for (var suffix = 2; suffix < 100; suffix++)
        {
            var candidate = $"{path}-{suffix}";

            if (!await exists(candidate))
            {
                return candidate;
            }
        }

        return $"{path}-{Guid.NewGuid():N}"[..(path.Length + 9)];
    }

    /// <summary>
    /// Compara caminhos como o sistema compara: o Git escreve <c>C:/x/y</c>, o
    /// Windows <c>C:\x\y</c>, e no Windows maiúscula não importa.
    /// </summary>
    public static bool SamePath(string? left, string? right)
    {
        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(
            Canonical(left),
            Canonical(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>O caminho no formato do sistema, sem barra no fim.</summary>
    public static string Canonical(string path)
    {
        var native = OperatingSystem.IsWindows() ? path.Replace('/', '\\') : path;

        try
        {
            native = Path.GetFullPath(native);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Caminho que o sistema nem aceita: compara como veio.
        }

        return TrimSeparators(native);
    }

    private static string TrimSeparators(string path)
    {
        var trimmed = path.Trim();
        var root = Path.GetPathRoot(trimmed) ?? string.Empty;

        while (trimmed.Length > root.Length && (trimmed[^1] == '\\' || trimmed[^1] == '/'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed;
    }
}
