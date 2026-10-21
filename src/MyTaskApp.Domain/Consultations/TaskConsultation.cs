using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Domain.Consultations;

/// <summary>De onde vem uma pasta da consulta. Os números são gravados no banco; não reordenar.</summary>
public enum ConsultationSourceKind
{
    /// <summary>Um diretório da etiqueta (ADR-026): a pasta mapeada neste computador.</summary>
    TagDirectory = 1,

    /// <summary>Um ambiente da tarefa (ADR-031): o worktree com o código em desenvolvimento.</summary>
    Worktree = 2,
}

/// <summary>Uma pasta escolhida, pela referência estável — nunca pelo caminho, que é de cada computador.</summary>
public sealed record ConsultationSourceReference(ConsultationSourceKind Kind, Guid ReferenceId);

/// <summary>
/// A Consulta Inteligente de uma tarefa (ADR-081): o assunto (etiqueta), as
/// pastas com o código, o banco local opcional e as instruções para a IA. Uma
/// por tarefa.
/// </summary>
/// <remarks>
/// <para>
/// Guarda <b>referências</b> ao que já existe — o diretório da etiqueta, o
/// ambiente, a conexão — e nunca uma cópia do que elas configuram. As
/// referências não têm chave estrangeira de propósito: excluir uma conexão ou
/// um diretório não deve ser bloqueado pela consulta, nem apagá-la. Quem lê
/// confere cada uma e diz o que deixou de existir.
/// </para>
/// <para>
/// Não sincroniza (ADR-061): pastas e conexões são deste computador.
/// </para>
/// </remarks>
public sealed class TaskConsultation
{
    public const int MaxInstructionsLength = 4000;

    public const int MaxSources = 20;

    public const int MaxOptionLength = 64;

    private readonly List<TaskConsultationSource> _sources = [];

    private TaskConsultation(Guid taskItemId, DateTimeOffset createdAt)
    {
        TaskItemId = taskItemId;
        CreatedAt = createdAt;
        UpdatedAt = createdAt;
    }

    public Guid TaskItemId { get; private set; }

    /// <summary>A etiqueta que dá o assunto; <c>null</c> = ainda não escolhida.</summary>
    public Guid? TagId { get; private set; }

    /// <summary>As pastas, na ordem escolhida: a primeira é onde o Claude abre.</summary>
    public IReadOnlyList<TaskConsultationSource> Sources => _sources;

    /// <summary>A conexão <b>de leitura</b> com que a IA consulta; <c>null</c> = só o código.</summary>
    public Guid? DatabaseConnectionId { get; private set; }

    /// <summary>O banco, quando a conexão é só o servidor (ADR-057).</summary>
    public string? DatabaseName { get; private set; }

    /// <summary>O que o usuário quer acrescentar às instruções geradas.</summary>
    public string? Instructions { get; private set; }

    /// <summary>O modelo do agente (ADR-040); <c>null</c> = o padrão dele.</summary>
    public string? Model { get; private set; }

    public string? Effort { get; private set; }

    /// <summary>A última pergunta com que a consulta abriu — volta na caixa ao reabrir a tarefa.</summary>
    public string? LastQuestion { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public bool HasDatabase => DatabaseConnectionId is not null;

    public static TaskConsultation Create(Guid taskItemId, DateTimeOffset createdAt)
    {
        if (taskItemId == Guid.Empty)
        {
            throw new DomainException("A consulta precisa de uma tarefa.");
        }

        return new TaskConsultation(taskItemId, createdAt);
    }

    /// <summary>
    /// Troca a etiqueta. Os diretórios da etiqueta anterior saem junto — são
    /// dela —, e os ambientes da tarefa ficam.
    /// </summary>
    public void SetSubject(Guid? tagId, DateTimeOffset at)
    {
        if (tagId == Guid.Empty)
        {
            throw new DomainException("Etiqueta inválida.");
        }

        if (tagId == TagId)
        {
            return;
        }

        TagId = tagId;
        _sources.RemoveAll(source => source.Kind == ConsultationSourceKind.TagDirectory);
        Renumber();
        UpdatedAt = at;
    }

    /// <summary>Substitui as pastas, na ordem dada. Repetida é recusada, e vazia é permitida (ainda escolhendo).</summary>
    public void SetSources(IEnumerable<ConsultationSourceReference> sources, DateTimeOffset at)
    {
        var list = sources.ToList();

        if (list.Count > MaxSources)
        {
            throw new DomainException($"Escolha no máximo {MaxSources} pastas.");
        }

        if (list.Any(source => source.ReferenceId == Guid.Empty || !Enum.IsDefined(source.Kind)))
        {
            throw new DomainException("Pasta inválida.");
        }

        if (list.Distinct().Count() != list.Count)
        {
            throw new DomainException("A mesma pasta foi escolhida duas vezes.");
        }

        _sources.Clear();
        _sources.AddRange(list.Select((source, index) => new TaskConsultationSource(TaskItemId, source.Kind, source.ReferenceId, index)));
        UpdatedAt = at;
    }

    /// <summary>
    /// O banco da consulta. <paramref name="databaseName"/> é obrigatório quando
    /// a conexão é só o servidor — quem chama sabe; aqui só se valida o nome.
    /// </summary>
    public void SetDatabase(Guid connectionId, string? databaseName, DateTimeOffset at)
    {
        if (connectionId == Guid.Empty)
        {
            throw new DomainException("Conexão inválida.");
        }

        DatabaseConnectionId = connectionId;
        DatabaseName = string.IsNullOrWhiteSpace(databaseName) ? null : DatabaseConnection.DatabaseName(databaseName);
        UpdatedAt = at;
    }

    /// <summary>Só o código-fonte. A conexão e o banco continuam onde estão.</summary>
    public void ClearDatabase(DateTimeOffset at)
    {
        DatabaseConnectionId = null;
        DatabaseName = null;
        UpdatedAt = at;
    }

    public void SetInstructions(string? instructions, DateTimeOffset at)
    {
        var normalized = string.IsNullOrWhiteSpace(instructions) ? null : instructions.Trim();

        if (normalized is { Length: > MaxInstructionsLength })
        {
            throw new DomainException($"As instruções adicionais podem ter até {MaxInstructionsLength} caracteres.");
        }

        Instructions = normalized;
        UpdatedAt = at;
    }

    /// <summary>A pergunta é obrigatória para abrir a consulta, e fica como a última feita.</summary>
    public void RememberQuestion(string? question, DateTimeOffset at)
    {
        var normalized = question?.Trim();

        if (string.IsNullOrEmpty(normalized))
        {
            throw new DomainException("Escreva a pergunta.");
        }

        if (normalized.Length > MaxInstructionsLength)
        {
            throw new DomainException($"A pergunta pode ter até {MaxInstructionsLength} caracteres.");
        }

        LastQuestion = normalized;
        UpdatedAt = at;
    }

    /// <summary>Os valores são os do agente (ADR-040); quem confere a lista é o caso de uso.</summary>
    public void SetLaunchPreferences(string? model, string? effort, DateTimeOffset at)
    {
        Model = Option(model);
        Effort = Option(effort);
        UpdatedAt = at;
    }

    private static string? Option(string? value)
    {
        var normalized = value?.Trim();

        if (string.IsNullOrEmpty(normalized))
        {
            return null;
        }

        return normalized.Length > MaxOptionLength
            ? throw new DomainException($"O valor não pode passar de {MaxOptionLength} caracteres.")
            : normalized;
    }

    private void Renumber()
    {
        var kept = _sources.Select(source => new ConsultationSourceReference(source.Kind, source.ReferenceId)).ToList();
        _sources.Clear();
        _sources.AddRange(kept.Select((source, index) => new TaskConsultationSource(TaskItemId, source.Kind, source.ReferenceId, index)));
    }
}

/// <summary>Uma pasta da consulta, na posição em que foi escolhida.</summary>
public sealed class TaskConsultationSource
{
    // Exigido pela materialização do EF Core.
    private TaskConsultationSource()
    {
    }

    internal TaskConsultationSource(Guid taskItemId, ConsultationSourceKind kind, Guid referenceId, int position)
    {
        Id = Guid.CreateVersion7();
        TaskItemId = taskItemId;
        Kind = kind;
        ReferenceId = referenceId;
        Position = position;
    }

    public Guid Id { get; private set; }

    public Guid TaskItemId { get; private set; }

    public ConsultationSourceKind Kind { get; private set; }

    /// <summary>O id do diretório da etiqueta, ou o do ambiente da tarefa.</summary>
    public Guid ReferenceId { get; private set; }

    public int Position { get; private set; }

    public ConsultationSourceReference Reference => new(Kind, ReferenceId);
}
