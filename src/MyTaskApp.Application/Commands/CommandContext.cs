using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Commands;

/// <summary>
/// Os valores das variáveis de contexto (<c>{worktree}</c>, <c>{branch}</c>…)
/// para um comando que vai rodar num ambiente (ADR-051).
/// </summary>
/// <remarks>
/// Uma variável pode ser <b>adiada</b>: conta como fornecida, mas não tem valor
/// ainda. É o caso de conferir a lista pós-Worktree antes de o worktree existir
/// — <c>{worktree}</c> não pode ser cobrado como parâmetro em falta, e fica no
/// texto até a execução.
/// </remarks>
public sealed class CommandContext
{
    private readonly Dictionary<string, string?> _values;

    /// <param name="values">Nome da variável → valor; <c>null</c> é adiado.</param>
    /// <param name="windows">
    /// Qual shell recebe a linha, para saber o que ele interpretaria. Padrão: o
    /// sistema em que o app roda.
    /// </param>
    public CommandContext(IEnumerable<KeyValuePair<string, string?>> values, bool? windows = null)
    {
        _values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in values)
        {
            if (CommandVariables.IsContextName(name))
            {
                _values[name] = value;
            }
        }

        IsWindows = windows ?? OperatingSystem.IsWindows();
    }

    public bool IsWindows { get; }

    /// <summary>Nenhuma variável: só os parâmetros do usuário valem.</summary>
    public static CommandContext None { get; } = new([]);

    /// <summary>As variáveis contam como fornecidas, sem valor ainda.</summary>
    public static CommandContext Deferred(IEnumerable<string> names) =>
        new(names.Select(name => new KeyValuePair<string, string?>(name, null)));

    /// <summary>Tudo o que o ambiente da tarefa sabe dizer.</summary>
    /// <param name="tagName">A etiqueta do diretório que ofereceu o comando; sem ela, <c>{tag}</c> fica sem valor.</param>
    public static CommandContext For(TaskItem task, TaskDevelopment development, string? tagName, bool? windows = null)
    {
        var values = new Dictionary<string, string?>
        {
            [CommandVariables.Worktree] = development.WorktreePath,
            [CommandVariables.WorktreePath] = development.WorktreePath,
            [CommandVariables.WorktreeName] = FolderName(development.WorktreePath),
            [CommandVariables.Repository] = development.RepositoryPath,
            [CommandVariables.RepositoryPath] = development.RepositoryPath,
            [CommandVariables.RepositoryName] = FolderName(development.RepositoryPath),
            [CommandVariables.Branch] = development.Branch,
            [CommandVariables.TaskId] = task.Id.ToString(),
            [CommandVariables.TaskTitle] = task.Title,
        };

        if (!string.IsNullOrWhiteSpace(tagName))
        {
            values[CommandVariables.Tag] = tagName;
        }

        return new CommandContext(values, windows);
    }

    /// <summary>Só o worktree: "Testar" na janela de globais roda numa pasta escolhida.</summary>
    public static CommandContext ForFolder(string folder) =>
        new(new Dictionary<string, string?>
        {
            [CommandVariables.Worktree] = folder,
            [CommandVariables.WorktreePath] = folder,
            [CommandVariables.WorktreeName] = FolderName(folder),
        });

    /// <summary>O contexto conhece a variável, com ou sem valor (adiada).</summary>
    public bool Provides(string name) => _values.ContainsKey(name);

    /// <summary>O valor, ou <c>null</c> se a variável é adiada ou desconhecida.</summary>
    public string? ValueOf(string name) => _values.GetValueOrDefault(name);

    private static string FolderName(string path) =>
        Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path;
}
