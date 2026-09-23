namespace MyTaskApp.Domain.Tasks;

/// <summary>Em que pé está o ambiente de desenvolvimento da tarefa (ADR-027).</summary>
/// <remarks>
/// Não há "NotStarted": tarefa sem ambiente simplesmente não tem
/// <see cref="TaskItem.Development"/>. Um valor para "nada" seria uma linha que
/// não diz coisa nenhuma.
/// </remarks>
public enum TaskDevelopmentStatus
{
    /// <summary>
    /// Gravado antes do <c>git worktree add</c>. Se o app cair no meio, é isto
    /// que sobra — e a tela oferece tentar de novo em vez de fingir que nada houve.
    /// </summary>
    Creating = 1,

    Ready = 2,

    Error = 3,

    /// <summary>O worktree foi removido; a branch continua no repositório.</summary>
    Removed = 4,
}

/// <summary>
/// O worktree Git em que a tarefa está sendo implementada: de qual repositório,
/// a partir de qual branch, em qual branch nova e em qual pasta (ADR-027).
/// </summary>
/// <remarks>
/// <para>
/// Guarda <b>cópias</b> dos caminhos, e não uma referência ao diretório da
/// etiqueta: o alias é atalho de digitação (ADR-026), e renomear ou apagar o
/// diretório da etiqueta não pode deixar um worktree em uso sem endereço.
/// </para>
/// <para>
/// Só o que o Git não sabe responder depois: de onde a branch saiu e quando. O
/// estado do worktree (alterações, commits) é sempre perguntado ao Git na hora.
/// </para>
/// </remarks>
public sealed class TaskDevelopment
{
    public const int MaxPathLength = 1024;

    public const int MaxBranchLength = 255;

    public const int MaxFailureLength = 2000;

    public const int MaxCommands = 50;

    private readonly List<TaskDevelopmentCommand> _commands = [];

    private TaskDevelopment(Guid id, Guid taskItemId)
    {
        Id = id;
        TaskItemId = taskItemId;
    }

    public Guid Id { get; }

    public Guid TaskItemId { get; }

    /// <summary>O worktree principal do repositório de onde a branch saiu.</summary>
    public string RepositoryPath { get; private set; } = string.Empty;

    /// <summary>A origem como o usuário escolheu: <c>develop</c> ou <c>origin/develop</c>.</summary>
    public string SourceBranch { get; private set; } = string.Empty;

    /// <summary>A branch da tarefa.</summary>
    public string Branch { get; private set; } = string.Empty;

    public string WorktreePath { get; private set; } = string.Empty;

    public TaskDevelopmentStatus Status { get; private set; }

    /// <summary>Quando esta tentativa começou.</summary>
    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset StatusChangedAt { get; private set; }

    /// <summary>O motivo, em pt-BR, quando <see cref="Status"/> é <see cref="TaskDevelopmentStatus.Error"/>.</summary>
    public string? FailureReason { get; private set; }

    /// <summary>
    /// Os comandos pós-Worktree, na ordem de execução (ADR-028). Vazio é o fluxo
    /// de antes: cria o worktree e pronto.
    /// </summary>
    public IReadOnlyList<TaskDevelopmentCommand> Commands =>
        _commands.OrderBy(command => command.Order).ToList().AsReadOnly();

    internal static TaskDevelopment Begin(
        Guid taskItemId,
        string repositoryPath,
        string sourceBranch,
        string branch,
        string worktreePath,
        DateTimeOffset at)
    {
        var development = new TaskDevelopment(Guid.CreateVersion7(at), taskItemId);
        development.Restart(repositoryPath, sourceBranch, branch, worktreePath, at);
        return development;
    }

    /// <summary>
    /// Começa de novo na mesma instância. Trocar por uma nova faria o EF inserir
    /// a linha nova antes de apagar a velha, e o índice único da tarefa recusaria.
    /// </summary>
    internal void Restart(
        string repositoryPath,
        string sourceBranch,
        string branch,
        string worktreePath,
        DateTimeOffset at)
    {
        var normalizedRepository = NormalizePath(repositoryPath, "do repositório");
        var normalizedSource = NormalizeBranch(sourceBranch, "A branch de origem");
        var normalizedBranch = NormalizeBranch(branch, "A branch da tarefa");
        var normalizedWorktree = NormalizePath(worktreePath, "do worktree");

        RepositoryPath = normalizedRepository;
        SourceBranch = normalizedSource;
        Branch = normalizedBranch;
        WorktreePath = normalizedWorktree;
        Status = TaskDevelopmentStatus.Creating;
        CreatedAt = at;
        StatusChangedAt = at;
        FailureReason = null;
    }

    /// <summary>
    /// Troca a lista inteira, na ordem dada. Reaproveita as linhas que já existem
    /// — reescreve texto e posição — em vez de apagar e inserir tudo de novo.
    /// </summary>
    internal void ReplaceCommands(IEnumerable<string?>? commands, DateTimeOffset at)
    {
        var normalized = TaskDevelopmentCommand.NormalizeList(commands);

        if (normalized.Count > MaxCommands)
        {
            throw new DomainException($"A lista aceita até {MaxCommands} comandos.");
        }

        var existing = _commands.OrderBy(command => command.Order).ToList();

        for (var index = 0; index < normalized.Count; index++)
        {
            if (index < existing.Count)
            {
                existing[index].Place(normalized[index], index);
            }
            else
            {
                _commands.Add(TaskDevelopmentCommand.Create(Id, normalized[index], index, at));
            }
        }

        foreach (var surplus in existing.Skip(normalized.Count))
        {
            _commands.Remove(surplus);
        }
    }

    internal void MarkReady(DateTimeOffset at)
    {
        if (Status is not (TaskDevelopmentStatus.Creating or TaskDevelopmentStatus.Error))
        {
            throw new DomainException("Só um ambiente em criação pode ficar pronto.");
        }

        Status = TaskDevelopmentStatus.Ready;
        StatusChangedAt = at;
        FailureReason = null;
    }

    internal void MarkFailed(string reason, DateTimeOffset at)
    {
        var normalized = string.IsNullOrWhiteSpace(reason)
            ? "Falha sem descrição."
            : reason.Trim();

        Status = TaskDevelopmentStatus.Error;
        StatusChangedAt = at;
        FailureReason = normalized.Length > MaxFailureLength
            ? normalized[..MaxFailureLength]
            : normalized;
    }

    internal void MarkRemoved(DateTimeOffset at)
    {
        Status = TaskDevelopmentStatus.Removed;
        StatusChangedAt = at;
        FailureReason = null;
    }

    private static string NormalizePath(string? path, string label)
    {
        var normalized = path?.Trim().Trim('"').Trim() ?? string.Empty;

        if (normalized.Length == 0)
        {
            throw new DomainException($"Falta o caminho {label}.");
        }

        if (normalized.Length > MaxPathLength)
        {
            throw new DomainException($"O caminho {label} não pode passar de {MaxPathLength} caracteres.");
        }

        if (!Path.IsPathFullyQualified(normalized))
        {
            throw new DomainException($"O caminho {label} precisa ser completo.");
        }

        var root = Path.GetPathRoot(normalized) ?? string.Empty;

        while (normalized.Length > root.Length
               && (normalized[^1] == '\\' || normalized[^1] == '/'))
        {
            normalized = normalized[..^1];
        }

        return normalized;
    }

    private static string NormalizeBranch(string? branch, string label)
    {
        var normalized = branch?.Trim() ?? string.Empty;

        if (normalized.Length == 0)
        {
            throw new DomainException($"{label} não foi informada.");
        }

        if (normalized.Length > MaxBranchLength)
        {
            throw new DomainException($"{label} não pode passar de {MaxBranchLength} caracteres.");
        }

        return normalized;
    }
}
