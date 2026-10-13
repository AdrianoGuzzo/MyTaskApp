using System.Globalization;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Commands;

/// <summary>
/// Os valores das variáveis de contexto (<c>{worktree}</c>, <c>{branch}</c>…)
/// para um comando que vai rodar num ambiente (ADR-051).
/// </summary>
/// <remarks>
/// <para>
/// Uma variável pode ser <b>adiada</b>: conta como fornecida, mas não tem valor
/// ainda. É o caso de conferir a lista pós-Worktree antes de o worktree existir
/// — <c>{worktree}</c> não pode ser cobrado como parâmetro em falta, e fica no
/// texto até a execução.
/// </para>
/// <para>
/// Ou <b>indisponível</b>, com o motivo: o ambiente sabe que ela não existe e
/// diz por quê. É o <c>{database}</c> de um ambiente sem banco copiado
/// (ADR-060) — o comando é recusado com a explicação, em vez de levar
/// <c>{database}</c> cru ao shell.
/// </para>
/// </remarks>
public sealed class CommandContext
{
    /// <summary>Por que <c>{database}</c> falta num ambiente sem cópia.</summary>
    public const string NoDatabaseReason =
        "este ambiente não tem banco copiado. Use \"Copiar banco\" na aba Desenvolvimento.";

    private readonly Dictionary<string, string?> _values;

    private readonly Dictionary<string, string> _unavailable;

    /// <param name="values">Nome da variável → valor; <c>null</c> é adiado.</param>
    /// <param name="windows">
    /// Qual shell recebe a linha, para saber o que ele interpretaria. Padrão: o
    /// sistema em que o app roda.
    /// </param>
    /// <param name="unavailable">Nome da variável → por que este ambiente não a tem.</param>
    public CommandContext(
        IEnumerable<KeyValuePair<string, string?>> values,
        bool? windows = null,
        IEnumerable<KeyValuePair<string, string>>? unavailable = null)
    {
        _values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        _unavailable = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, value) in values)
        {
            if (CommandVariables.IsContextName(name))
            {
                _values[name] = value;
            }
        }

        foreach (var (name, reason) in unavailable ?? [])
        {
            if (CommandVariables.IsContextName(name) && !_values.ContainsKey(name))
            {
                _unavailable[name] = reason;
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

    /// <summary>Como <see cref="Deferred(IEnumerable{string})"/>, com as que este ambiente não terá.</summary>
    public static CommandContext Deferred(IEnumerable<string> names, IReadOnlyDictionary<string, string> unavailable) =>
        new(names.Select(name => new KeyValuePair<string, string?>(name, null)), unavailable: unavailable);

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

        if (development.Database is not { } database)
        {
            return new CommandContext(values, windows, DatabaseUnavailable(NoDatabaseReason));
        }

        values[CommandVariables.Database] = database.Name;
        values[CommandVariables.DatabaseName] = database.Name;
        values[CommandVariables.DatabaseHost] = database.Host;
        values[CommandVariables.DatabasePort] = database.Port.ToString(CultureInfo.InvariantCulture);
        values[CommandVariables.DatabaseUser] = database.Username;

        return new CommandContext(values, windows);
    }

    /// <summary>Só o worktree: "Testar" na janela de globais roda numa pasta escolhida.</summary>
    public static CommandContext ForFolder(string folder) =>
        new(
            new Dictionary<string, string?>
            {
                [CommandVariables.Worktree] = folder,
                [CommandVariables.WorktreePath] = folder,
                [CommandVariables.WorktreeName] = FolderName(folder),
            },
            unavailable: DatabaseUnavailable(
                "o teste roda fora de um ambiente, sem banco copiado. Para testar, passe o valor: database=meu_banco."));

    /// <summary>As variáveis do banco, todas com o mesmo motivo.</summary>
    public static IReadOnlyDictionary<string, string> DatabaseUnavailable(string reason) =>
        CommandVariables.DatabaseNames.ToDictionary(name => name, _ => reason, StringComparer.OrdinalIgnoreCase);

    /// <summary>O contexto conhece a variável, com ou sem valor (adiada).</summary>
    public bool Provides(string name) => _values.ContainsKey(name);

    /// <summary>O valor, ou <c>null</c> se a variável é adiada ou desconhecida.</summary>
    public string? ValueOf(string name) => _values.GetValueOrDefault(name);

    /// <summary>Por que este ambiente não tem a variável, ou <c>null</c>.</summary>
    public string? UnavailableReason(string name) => _unavailable.GetValueOrDefault(name);

    private static string FolderName(string path) =>
        Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path;
}
