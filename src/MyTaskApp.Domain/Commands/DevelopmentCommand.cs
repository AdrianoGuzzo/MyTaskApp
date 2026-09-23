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
/// </remarks>
public sealed class DevelopmentCommand
{
    public const int MaxAliasLength = AliasRule.MaxLength;

    public const int MaxCommandLength = 2000;

    public const int MaxDescriptionLength = 500;

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
    }

    public Guid Id { get; }

    /// <summary>Sempre começa com <c>@</c> (ver <see cref="NormalizeAlias"/>).</summary>
    public string Alias { get; private set; }

    /// <summary>O texto que vai para o shell, como o usuário o digitaria.</summary>
    public string Command { get; private set; }

    public string? Description { get; private set; }

    public DateTimeOffset CreatedAt { get; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static DevelopmentCommand Create(
        string alias,
        string command,
        string? description,
        DateTimeOffset createdAt) =>
        new(
            Guid.CreateVersion7(createdAt),
            NormalizeAlias(alias),
            NormalizeCommand(command),
            NormalizeDescription(description),
            createdAt);

    /// <summary>Atômico: valida tudo antes de trocar qualquer campo.</summary>
    public void Update(string alias, string command, string? description, DateTimeOffset at)
    {
        var normalizedAlias = NormalizeAlias(alias);
        var normalizedCommand = NormalizeCommand(command);
        var normalizedDescription = NormalizeDescription(description);

        Alias = normalizedAlias;
        Command = normalizedCommand;
        Description = normalizedDescription;
        UpdatedAt = at;
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
