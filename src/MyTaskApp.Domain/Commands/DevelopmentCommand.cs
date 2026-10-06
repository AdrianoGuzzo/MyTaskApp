namespace MyTaskApp.Domain.Commands;

/// <summary>
/// Um comando de terminal reaproveitável, chamado pelo <c>@alias</c> na lista de
/// comandos pós-Worktree de qualquer tarefa (ADR-028). É global: não pertence a
/// etiqueta, checklist nem repositório.
/// </summary>
/// <remarks>
/// Ao contrário do alias de diretório, este alias é <b>referência</b>: a tarefa
/// guarda <c>@restore</c> e o texto do comando é lido na hora de executar. Mudar
/// o comando aqui vale para todas as tarefas que o chamam.
/// <para>
/// O texto pode ter parâmetros <c>{nome}</c> (ver <see cref="CommandParameters"/>):
/// <c>eco-sync {nomebanco} -Dev</c>, chamado por <c>@eco-sync nomebanco=MeuBanco</c>.
/// </para>
/// <para>
/// O mesmo comando é um <b>comando rápido</b> (ADR-051): ligado ao diretório de
/// uma etiqueta, vira um botão "▶ <see cref="Name"/>" no ambiente pronto da
/// tarefa. O que só o comando rápido usa — modo, pasta, terminal, confirmação e
/// a definição dos parâmetros — mora em <see cref="DevelopmentCommandSettings"/>,
/// e o padrão é o comportamento de sempre.
/// </para>
/// </remarks>
public sealed class DevelopmentCommand
{
    public const int MaxAliasLength = AliasRule.MaxLength;

    public const int MaxCommandLength = 2000;

    public const int MaxDescriptionLength = 500;

    public const int MaxNameLength = 80;

    private readonly List<DevelopmentCommandParameter> _parameters = [];

    private DevelopmentCommand(
        Guid id,
        string alias,
        string command,
        string? description,
        DateTimeOffset createdAt)
    {
        Id = id;
        Alias = alias;
        Command = command;
        Description = description;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
        KeepTerminalOpen = true;
    }

    public Guid Id { get; }

    /// <summary>Sempre começa com <c>@</c> (ver <see cref="NormalizeAlias"/>).</summary>
    public string Alias { get; private set; }

    /// <summary>O texto que vai para o shell, como o usuário o digitaria.</summary>
    public string Command { get; private set; }

    public string? Description { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>O rótulo do botão: "Executar aplicação". Sem nome, a tela mostra o alias.</summary>
    public string? Name { get; private set; }

    public CommandMode Mode { get; private set; }

    /// <summary>Relativa ao worktree (ver <see cref="CommandWorkingDirectory"/>). <c>null</c> é a raiz.</summary>
    public string? WorkingDirectory { get; private set; }

    /// <summary>No modo terminal, se a janela fica aberta quando o comando termina.</summary>
    public bool KeepTerminalOpen { get; private set; }

    /// <summary>Mostra a linha final e pede um clique antes de executar.</summary>
    public bool RequiresConfirmation { get; private set; }

    /// <summary>Como perguntar cada <c>{nome}</c>, na ordem do formulário.</summary>
    public IReadOnlyList<DevelopmentCommandParameter> Parameters =>
        [.. _parameters.OrderBy(parameter => parameter.Order)];

    /// <summary>O que a tela mostra: o nome, ou o alias.</summary>
    public string DisplayName => Name ?? Alias;

    public DevelopmentCommandSettings Settings =>
        new(Name, Mode, WorkingDirectory, KeepTerminalOpen, RequiresConfirmation,
            [.. Parameters.Select(parameter => parameter.ToSpec())]);

    public static DevelopmentCommand Create(
        string alias,
        string command,
        string? description,
        DateTimeOffset createdAt) =>
        Create(alias, command, description, DevelopmentCommandSettings.Default, createdAt);

    public static DevelopmentCommand Create(
        string alias,
        string command,
        string? description,
        DevelopmentCommandSettings settings,
        DateTimeOffset createdAt)
    {
        var created = new DevelopmentCommand(
            Guid.CreateVersion7(createdAt),
            NormalizeAlias(alias),
            NormalizeCommand(command),
            NormalizeDescription(description),
            createdAt);

        created.Apply(created.Command, settings, createdAt);

        return created;
    }

    /// <summary>
    /// Atômico: valida tudo antes de trocar qualquer campo. Mantém as
    /// configurações de comando rápido como estão.
    /// </summary>
    public void Update(string alias, string command, string? description, DateTimeOffset at) =>
        Update(alias, command, description, Settings, at);

    /// <summary>Atômico: valida tudo antes de trocar qualquer campo.</summary>
    public void Update(
        string alias,
        string command,
        string? description,
        DevelopmentCommandSettings settings,
        DateTimeOffset at)
    {
        var normalizedAlias = NormalizeAlias(alias);
        var normalizedCommand = NormalizeCommand(command);
        var normalizedDescription = NormalizeDescription(description);

        // Valida antes de trocar qualquer coisa: Apply só falha aqui, e não no meio.
        _ = NormalizeSettings(normalizedCommand, settings);

        Alias = normalizedAlias;
        Command = normalizedCommand;
        Description = normalizedDescription;
        Apply(normalizedCommand, settings, at);
        UpdatedAt = at;
    }

    private void Apply(string command, DevelopmentCommandSettings settings, DateTimeOffset at)
    {
        var normalized = NormalizeSettings(command, settings);

        Name = normalized.Name;
        Mode = normalized.Mode;
        WorkingDirectory = normalized.WorkingDirectory;
        KeepTerminalOpen = normalized.KeepTerminalOpen;
        RequiresConfirmation = normalized.RequiresConfirmation;
        ReplaceParameters(normalized.Parameters ?? [], at);
    }

    /// <summary>
    /// Reaproveita as linhas que já existem — reescreve nome, tipo e posição — em
    /// vez de apagar e inserir tudo de novo, como a lista pós-Worktree.
    /// </summary>
    private void ReplaceParameters(IReadOnlyList<CommandParameterSpec> specs, DateTimeOffset at)
    {
        var existing = Parameters.ToList();

        for (var index = 0; index < specs.Count; index++)
        {
            if (index < existing.Count)
            {
                existing[index].Place(specs[index], index);
            }
            else
            {
                _parameters.Add(DevelopmentCommandParameter.Create(Id, specs[index], index, at));
            }
        }

        foreach (var surplus in existing.Skip(specs.Count))
        {
            _parameters.Remove(surplus);
        }
    }

    /// <summary>
    /// Valida as configurações contra o texto do comando. As definições de
    /// parâmetros que o texto já não tem saem; definir uma variável de contexto
    /// (<c>{worktree}</c>…) é recusado, porque quem a preenche é o app.
    /// </summary>
    private static DevelopmentCommandSettings NormalizeSettings(string command, DevelopmentCommandSettings settings)
    {
        var name = string.IsNullOrWhiteSpace(settings.Name) ? null : settings.Name.Trim();

        if (name?.Length > MaxNameLength)
        {
            throw new DomainException($"O nome do comando não pode passar de {MaxNameLength} caracteres.");
        }

        if (!Enum.IsDefined(settings.Mode))
        {
            throw new DomainException("O tipo do comando não é válido.");
        }

        var names = CommandParameters.Names(command);
        var specs = new List<CommandParameterSpec>();

        foreach (var spec in settings.Parameters ?? [])
        {
            var normalized = spec.Normalized();

            if (CommandVariables.IsContextName(normalized.Name))
            {
                throw new DomainException(
                    $"{{{normalized.Name}}} é preenchido pelo app e não pode ser definido como parâmetro.");
            }

            if (specs.Exists(other => string.Equals(other.Name, normalized.Name, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DomainException($"O parâmetro {normalized.Name} foi definido duas vezes.");
            }

            if (names.Contains(normalized.Name, StringComparer.OrdinalIgnoreCase))
            {
                specs.Add(normalized);
            }
        }

        return new DevelopmentCommandSettings(
            name,
            settings.Mode,
            CommandWorkingDirectory.NormalizeDefault(settings.WorkingDirectory),
            settings.KeepTerminalOpen,
            settings.RequiresConfirmation,
            specs);
    }

    /// <summary>
    /// A definição de cada parâmetro do texto, na ordem em que aparecem: o
    /// cadastrado, ou texto obrigatório (<see cref="CommandParameterSpec.Plain"/>).
    /// As variáveis de contexto ficam de fora.
    /// </summary>
    public IReadOnlyList<CommandParameterSpec> ParametersOf(string text)
    {
        var defined = Parameters.Select(parameter => parameter.ToSpec()).ToList();

        return [.. CommandParameters.Names(text)
            .Where(name => !CommandVariables.IsContextName(name))
            .Select(name => defined.Find(spec => string.Equals(spec.Name, name, StringComparison.OrdinalIgnoreCase))
                            ?? CommandParameterSpec.Plain(name))];
    }

    public static string NormalizeAlias(string? alias) =>
        AliasRule.Normalize(alias, "O comando precisa de um apelido (ex.: @restore).");

    /// <summary>
    /// Apara, e só. O texto é do usuário e vai inteiro para o shell: aspas,
    /// <c>&amp;&amp;</c> e pipes são dele. Quebra de linha não — cada comando é
    /// uma linha, e várias etapas viram vários itens na lista da tarefa.
    /// </summary>
    public static string NormalizeCommand(string? command)
    {
        var normalized = command?.Trim() ?? string.Empty;

        if (normalized.Length == 0)
        {
            throw new DomainException("Informe o comando.");
        }

        if (normalized.Length > MaxCommandLength)
        {
            throw new DomainException($"O comando não pode passar de {MaxCommandLength} caracteres.");
        }

        if (normalized.Contains('\n', StringComparison.Ordinal) || normalized.Contains('\r', StringComparison.Ordinal))
        {
            throw new DomainException("O comando ocupa uma linha só.");
        }

        return normalized;
    }

    private static string? NormalizeDescription(string? description)
    {
        var normalized = description?.Trim();

        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        if (normalized.Length > MaxDescriptionLength)
        {
            throw new DomainException(
                $"A descrição do comando não pode passar de {MaxDescriptionLength} caracteres.");
        }

        return normalized;
    }
}
