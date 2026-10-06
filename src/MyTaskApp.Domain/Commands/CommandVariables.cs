using System.Text.RegularExpressions;

namespace MyTaskApp.Domain.Commands;

/// <summary>Uma variável que o app preenche sozinho a partir do ambiente da tarefa (ADR-051).</summary>
public sealed record CommandVariable(string Name, string Description)
{
    /// <summary>Como aparece no texto do comando: <c>{worktree}</c>.</summary>
    public string Placeholder => $"{{{Name}}}";
}

/// <summary>
/// As variáveis de contexto de um comando: <c>{worktree}</c>, <c>{branch}</c>,
/// <c>{task.title}</c>… (ADR-051). Quem preenche é o app, a partir do ambiente
/// onde o comando roda.
/// </summary>
/// <remarks>
/// <para>
/// Os nomes sem ponto têm a mesma forma de um parâmetro do usuário
/// (<see cref="CommandParameters"/>). Um comando antigo pode já ter um
/// <c>{branch}</c> preenchido por <c>@x branch=…</c> — por isso o valor
/// explícito sempre vence, e o contexto só preenche o que ficou sem valor.
/// </para>
/// <para>
/// O preenchimento é de <b>uma passada só</b>: um valor que contenha
/// <c>{branch}</c> entra como texto, e não é preenchido de novo.
/// </para>
/// </remarks>
public static partial class CommandVariables
{
    public const string Worktree = "worktree";

    public const string WorktreePath = "worktree.path";

    public const string WorktreeName = "worktree.name";

    public const string Repository = "repository";

    public const string RepositoryPath = "repository.path";

    public const string RepositoryName = "repository.name";

    public const string Branch = "branch";

    public const string TaskId = "task.id";

    public const string TaskTitle = "task.title";

    public const string Tag = "tag";

    /// <summary>Todas, na ordem em que a tela as explica.</summary>
    public static IReadOnlyList<CommandVariable> All { get; } =
    [
        new(Worktree, "Pasta do worktree da tarefa"),
        new(WorktreePath, "O mesmo que {worktree}"),
        new(WorktreeName, "Nome da pasta do worktree"),
        new(Repository, "Pasta do repositório principal"),
        new(RepositoryPath, "O mesmo que {repository}"),
        new(RepositoryName, "Nome da pasta do repositório"),
        new(Branch, "Branch da tarefa"),
        new(TaskId, "Id da tarefa"),
        new(TaskTitle, "Título da tarefa"),
        new(Tag, "Nome da etiqueta do diretório"),
    ];

    private static readonly HashSet<string> Names =
        new(All.Select(variable => variable.Name), StringComparer.OrdinalIgnoreCase);

    /// <summary>O nome é de uma variável de contexto, e não de um parâmetro do usuário.</summary>
    public static bool IsContextName(string? name) => name is not null && Names.Contains(name);

    /// <summary>Os nomes entre chaves no texto — de variável ou de parâmetro —, sem repetir.</summary>
    public static IReadOnlyList<string> Placeholders(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return [];
        }

        return [.. Placeholder().Matches(text)
            .Select(match => match.Groups["name"].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>As variáveis de contexto que o texto usa.</summary>
    public static IReadOnlyList<string> Used(string? text) =>
        [.. Placeholders(text).Where(IsContextName)];

    /// <summary>
    /// Troca cada <c>{nome}</c> pelo que <paramref name="lookup"/> devolver, numa
    /// passada só. <c>null</c> deixa o <c>{nome}</c> como está.
    /// </summary>
    public static string Fill(string text, Func<string, string?> lookup) =>
        Placeholder().Replace(text, match => lookup(match.Groups["name"].Value) ?? match.Value);

    /// <summary>
    /// O primeiro caractere de <paramref name="value"/> que o shell interpretaria
    /// em vez de passar adiante, ou <c>null</c>. Espaço não conta: quem usa um
    /// caminho com espaço põe a variável entre aspas no comando.
    /// </summary>
    /// <remarks>
    /// No Windows o shell é o cmd: <c>!</c> só seria especial com expansão
    /// atrasada, que vem desligada. Fora dele, o <c>/bin/sh</c> interpreta mais.
    /// </remarks>
    public static char? UnsafeCharacter(string value, bool windows)
    {
        var unsafeCharacters = windows ? WindowsUnsafe : PosixUnsafe;
        var index = value.IndexOfAny(unsafeCharacters);

        return index < 0 ? null : value[index];
    }

    private static readonly char[] WindowsUnsafe = ['"', '%', '&', '|', '<', '>', '^', '\r', '\n'];

    private static readonly char[] PosixUnsafe =
        ['"', '&', '|', '<', '>', '\r', '\n', '`', '$', ';', '\\', '(', ')', '\''];

    [GeneratedRegex(@"\{(?<name>[A-Za-z_][A-Za-z0-9_-]*(?:\.[A-Za-z_][A-Za-z0-9_-]*)?)\}")]
    private static partial Regex Placeholder();
}
