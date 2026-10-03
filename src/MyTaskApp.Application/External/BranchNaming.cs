using System.Text;
using MyTaskApp.Application.Development;
using MyTaskApp.Domain;

namespace MyTaskApp.Application.External;

/// <summary>Dá nome à branch de uma tarefa vinculada a uma issue (ADR-045).</summary>
public interface IBranchNameStrategy
{
    string GenerateBranchName(ExternalTask task);
}

/// <summary>
/// As convenções do usuário: tipo da issue → molde da branch
/// (<c>Bug = bug/{id}</c>). Dado do usuário, no banco (ADR-014), editado como
/// texto na janela de Integrações — uma linha por tipo.
/// </summary>
/// <remarks>
/// Moldes aceitam <c>{id}</c> (obrigatório: é o que liga a branch à issue),
/// <c>{type}</c> e <c>{slug}</c> (o título sem acento, minúsculo, com hífens).
/// </remarks>
public sealed record BranchConventions(IReadOnlyDictionary<string, string> Patterns)
{
    public const string IdPlaceholder = "{id}";

    public const string TypePlaceholder = "{type}";

    public const string SlugPlaceholder = "{slug}";

    /// <summary>O molde de quem não tem tipo, ou de um tipo que não se tornaria nome de pasta.</summary>
    public const string FallbackPattern = "task/{id}";

    private const string UnknownTypePattern = "{type}/{id}";

    private static readonly string[] Placeholders = [IdPlaceholder, TypePlaceholder, SlugPlaceholder];

    public static readonly BranchConventions Default = new(Ordinal(new Dictionary<string, string>
    {
        ["Bug"] = "bug/{id}",
        ["Story"] = "feature/{id}",
        ["Task"] = "task/{id}",
        ["Improvement"] = "improvement/{id}",
        ["Hotfix"] = "hotfix/{id}",
    }));

    /// <summary>
    /// A API do Jira devolve o nome do tipo na língua do usuário. "História" e
    /// "Story" são o mesmo tipo, e quem configurou "Story" não deveria precisar
    /// repetir a linha em português. Um tipo localizado escrito à mão ganha.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> Aliases = Ordinal(new Dictionary<string, string>
    {
        ["História"] = "Story",
        ["Historia"] = "Story",
        ["User Story"] = "Story",
        ["Epic"] = "Story",
        ["Épico"] = "Story",
        ["Epico"] = "Story",
        ["Feature"] = "Story",
        ["Funcionalidade"] = "Story",
        ["Tarefa"] = "Task",
        ["Subtarefa"] = "Task",
        ["Sub-tarefa"] = "Task",
        ["Sub-task"] = "Task",
        ["Subtask"] = "Task",
        ["Melhoria"] = "Improvement",
        ["Defeito"] = "Bug",
        ["Erro"] = "Bug",
        ["Hot fix"] = "Hotfix",
    });

    /// <summary>O molde do tipo: o escrito, depois o equivalente, depois o próprio tipo como prefixo.</summary>
    public string PatternFor(string? issueType)
    {
        var type = issueType?.Trim();

        if (string.IsNullOrEmpty(type))
        {
            return Patterns.GetValueOrDefault("Task", FallbackPattern);
        }

        if (Patterns.TryGetValue(type, out var exact))
        {
            return exact;
        }

        if (Aliases.TryGetValue(type, out var canonical))
        {
            return Patterns.GetValueOrDefault(canonical)
                ?? Default.Patterns.GetValueOrDefault(canonical, FallbackPattern);
        }

        return UnknownTypePattern;
    }

    /// <summary>
    /// Uma linha por tipo, <c>Tipo = molde</c>. Linha vazia e <c>#comentário</c>
    /// passam. O que não puder virar branch é recusado com a linha, antes de gravar.
    /// </summary>
    public static BranchConventions Parse(string? text)
    {
        var patterns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = (text ?? string.Empty).Split('\n');

        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();

            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            var where = $"Convenção de branch, linha {index + 1}";
            var equals = line.IndexOf('=');

            if (equals < 0)
            {
                throw new DomainException($"{where}: use \"Tipo = molde\", como \"Bug = bug/{{id}}\".");
            }

            var type = line[..equals].Trim();
            var pattern = line[(equals + 1)..].Trim();

            if (type.Length == 0)
            {
                throw new DomainException($"{where}: falta o tipo da issue antes do \"=\".");
            }

            if (!pattern.Contains(IdPlaceholder, StringComparison.Ordinal))
            {
                throw new DomainException($"{where}: o molde precisa de {IdPlaceholder}, que liga a branch à issue.");
            }

            if (UnknownPlaceholder(pattern) is { } unknown)
            {
                throw new DomainException($"{where}: {unknown} não existe. Use {string.Join(", ", Placeholders)}.");
            }

            if (GitBranchName.Validate(Fill(pattern, "PROJ-1", "tipo", "titulo")) is { } problem)
            {
                throw new DomainException($"{where}: {char.ToLowerInvariant(problem[0])}{problem[1..]}");
            }

            if (!patterns.TryAdd(type, pattern))
            {
                throw new DomainException($"{where}: \"{type}\" já tem um molde numa linha anterior.");
            }
        }

        return new BranchConventions(patterns);
    }

    public string ToText()
    {
        var builder = new StringBuilder();

        foreach (var (type, pattern) in Patterns)
        {
            builder.Append(type).Append(" = ").Append(pattern).Append('\n');
        }

        return builder.ToString().TrimEnd('\n');
    }

    internal static string Fill(string pattern, string id, string type, string slug) =>
        pattern
            .Replace(IdPlaceholder, id, StringComparison.Ordinal)
            .Replace(TypePlaceholder, type, StringComparison.Ordinal)
            .Replace(SlugPlaceholder, slug, StringComparison.Ordinal);

    private static string? UnknownPlaceholder(string pattern)
    {
        var start = pattern.IndexOf('{');

        while (start >= 0)
        {
            var end = pattern.IndexOf('}', start);

            if (end < 0)
            {
                return pattern[start..];
            }

            var placeholder = pattern[start..(end + 1)];

            if (!Placeholders.Contains(placeholder, StringComparer.Ordinal))
            {
                return placeholder;
            }

            start = pattern.IndexOf('{', end);
        }

        return null;
    }

    private static Dictionary<string, string> Ordinal(Dictionary<string, string> source) =>
        new(source, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// <c>{tipo}/{chave}</c> pelas convenções do usuário: <c>Bug</c> +
/// <c>GAECO-1234</c> → <c>bug/GAECO-1234</c>. A chave entra como o Jira a
/// escreve, porque é ela que o usuário procura no <c>git branch</c>.
/// </summary>
public sealed class ConventionBranchNameStrategy(BranchConventions conventions) : IBranchNameStrategy
{
    public string GenerateBranchName(ExternalTask task)
    {
        var type = GitBranchName.Slug(task.IssueType);
        var name = BranchConventions.Fill(
            conventions.PatternFor(task.IssueType),
            task.Id.Trim(),
            type,
            GitBranchName.Slug(task.Title));

        // Uma chave ou um tipo esquisitos não podem render um nome que o Git
        // recuse: cai no molde mais simples, que só depende da chave.
        return GitBranchName.Validate(name) is null
            ? name
            : BranchConventions.Fill(BranchConventions.FallbackPattern, GitBranchName.Slug(task.Id), type, string.Empty);
    }
}

/// <summary>As convenções de branch do usuário, em linha única no banco (ADR-014).</summary>
public interface IBranchConventionStore
{
    /// <summary>Sem nada gravado, as de fábrica.</summary>
    Task<BranchConventions> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(BranchConventions conventions, CancellationToken cancellationToken = default);
}
