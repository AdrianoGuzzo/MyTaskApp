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
/// <para>
/// Os <b>apelidos de banco</b> também são variáveis, mas só existem em runtime:
/// <c>{lock_eco_core_1010}</c> vale o banco copiado quando a cópia do ambiente
/// veio daquele apelido, e os outros apelidos ficam indisponíveis com o motivo
/// (ADR-060). Uma variável fixa com o mesmo nome vence.
/// </para>
/// </remarks>
public sealed class CommandContext
{
    /// <summary>Por que <c>{database}</c> falta num ambiente sem cópia.</summary>
    public const string NoDatabaseReason =
        "este ambiente não tem banco copiado. Use \"Copiar banco\" na aba Desenvolvimento.";

    private readonly Dictionary<string, string?> _values;

    private readonly Dictionary<string, string> _unavailable;

    private readonly HashSet<string> _aliases = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="values">Nome da variável → valor; <c>null</c> é adiado.</param>
    /// <param name="windows">
    /// Qual shell recebe a linha, para saber o que ele interpretaria. Padrão: o
    /// sistema em que o app roda.
    /// </param>
    /// <param name="unavailable">Nome da variável → por que este ambiente não a tem.</param>
    /// <param name="aliases">Os apelidos de banco, cada um com valor, adiado ou indisponível.</param>
    public CommandContext(
        IEnumerable<KeyValuePair<string, string?>> values,
        bool? windows = null,
        IEnumerable<KeyValuePair<string, string>>? unavailable = null,
        IEnumerable<DatabaseAliasVariable>? aliases = null)
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

        foreach (var alias in aliases ?? [])
        {
            // A variável fixa vence: um apelido "database" não troca o {database}.
            if (CommandVariables.IsContextName(alias.Name) || !_aliases.Add(alias.Name))
            {
                continue;
            }

            if (alias.UnavailableReason is { } reason)
            {
                _unavailable[alias.Name] = reason;
            }
            else
            {
                _values[alias.Name] = alias.Value;
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
    public static CommandContext Deferred(
        IEnumerable<string> names,
        IReadOnlyDictionary<string, string> unavailable,
        IEnumerable<DatabaseAliasVariable>? aliases = null) =>
        new(names.Select(name => new KeyValuePair<string, string?>(name, null)), unavailable: unavailable, aliases: aliases);

    /// <summary>Tudo o que o ambiente da tarefa sabe dizer.</summary>
    /// <param name="tagName">A etiqueta do diretório que ofereceu o comando; sem ela, <c>{tag}</c> fica sem valor.</param>
    /// <param name="aliases">Os apelidos de banco cadastrados, para valerem como <c>{apelido}</c>.</param>
    public static CommandContext For(
        TaskItem task,
        TaskDevelopment development,
        string? tagName,
        bool? windows = null,
        IEnumerable<string>? aliases = null)
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

        var aliasVariables = AliasesFor(development.Database, aliases ?? []);

        if (development.Database is not { } database)
        {
            return new CommandContext(values, windows, DatabaseUnavailable(NoDatabaseReason), aliasVariables);
        }

        values[CommandVariables.Database] = database.Name;
        values[CommandVariables.DatabaseName] = database.Name;
        values[CommandVariables.DatabaseHost] = database.Host;
        values[CommandVariables.DatabasePort] = database.Port.ToString(CultureInfo.InvariantCulture);
        values[CommandVariables.DatabaseUser] = database.Username;

        return new CommandContext(values, windows, aliases: aliasVariables);
    }

    /// <summary>
    /// Cada apelido como variável: o da cópia do ambiente vale o banco criado;
    /// os outros explicam de onde o banco veio. O apelido da cópia vale mesmo
    /// que tenha saído do cadastro depois.
    /// </summary>
    public static IReadOnlyList<DatabaseAliasVariable> AliasesFor(TaskDevelopmentDatabase? database, IEnumerable<string> aliases)
    {
        var variables = new List<DatabaseAliasVariable>();

        if (database?.Alias is { } current)
        {
            variables.Add(new DatabaseAliasVariable(current, database.Name));
        }

        var reason = database switch
        {
            null => NoDatabaseReason,
            { Alias: { } copied } =>
                $"o banco deste ambiente veio do apelido {copied}. Use {{{copied}}} ou {{database}}, ou copie deste apelido.",
            _ => $"o banco deste ambiente ({database.Name}) não veio de um apelido. Use {{database}}, ou copie deste apelido.",
        };

        variables.AddRange(aliases
            .Where(alias => !string.Equals(alias, database?.Alias, StringComparison.OrdinalIgnoreCase))
            .Select(alias => new DatabaseAliasVariable(alias, UnavailableReason: reason)));

        return variables;
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

    /// <summary>O nome é de uma variável — fixa, ou um apelido de banco — e não de um parâmetro do usuário.</summary>
    public bool Knows(string name) => CommandVariables.IsContextName(name) || _aliases.Contains(name);

    /// <summary>Os apelidos de banco que este contexto trata como variável.</summary>
    public IReadOnlyCollection<string> DatabaseAliases => _aliases;

    /// <summary>O valor, ou <c>null</c> se a variável é adiada ou desconhecida.</summary>
    public string? ValueOf(string name) => _values.GetValueOrDefault(name);

    /// <summary>Por que este ambiente não tem a variável, ou <c>null</c>.</summary>
    public string? UnavailableReason(string name) => _unavailable.GetValueOrDefault(name);

    private static string FolderName(string path) =>
        Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path;
}

/// <summary>
/// Um apelido de banco como variável (ADR-060): com <see cref="Value"/>, ou
/// adiado (sem os dois), ou indisponível com o motivo.
/// </summary>
public sealed record DatabaseAliasVariable(string Name, string? Value = null, string? UnavailableReason = null);
