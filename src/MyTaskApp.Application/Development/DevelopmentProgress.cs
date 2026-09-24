using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Development;

/// <summary>
/// As etapas de "Iniciar implementação", na ordem em que rodam (ADR-027). A
/// tela lista todas e marca cada uma conforme o caso de uso avança.
/// </summary>
public enum DevelopmentStep
{
    CheckGit,
    ValidateDirectory,
    ValidateRepository,
    Fetch,
    ValidateSource,
    CheckChanges,
    UpdateSource,
    ValidateBranchName,
    PlanWorktreePath,
    CreateWorktree,
    ValidateWorktree,
    SaveTask,
    RemoveWorktree,
}

public enum DevelopmentStepState
{
    Running,
    Done,

    /// <summary>Passou, mas com algo que o usuário precisa saber.</summary>
    Warning,

    /// <summary>Não se aplicou (origem remota não tem o que atualizar, por exemplo).</summary>
    Skipped,
}

/// <summary>Um aviso de progresso. A falha não vem por aqui: vem como exceção.</summary>
public sealed record DevelopmentProgress(
    DevelopmentStep Step,
    DevelopmentStepState State,
    string? Note = null);

/// <summary>
/// Uma etapa falhou. É <see cref="DomainException"/> porque a mensagem é para o
/// usuário ler; o comando do Git e as alterações vão junto para "Ver detalhes".
/// </summary>
public sealed class DevelopmentStepException(
    DevelopmentStep step,
    string message,
    GitCommandResult? command = null,
    IReadOnlyList<string>? changes = null,
    IReadOnlyList<DirectoryLocker>? lockers = null) : DomainException(message)
{
    public DevelopmentStep Step { get; } = step;

    public GitCommandResult? Command { get; } = command;

    /// <summary>As alterações locais que impediram a etapa, quando for o caso.</summary>
    public IReadOnlyList<string> Changes { get; } = changes ?? [];

    /// <summary>
    /// A pasta não pôde ser apagada. <see cref="Lockers"/> diz quem a segura,
    /// quando o sistema deixa saber (ADR-029).
    /// </summary>
    public bool IsDirectoryLocked { get; init; }

    public IReadOnlyList<DirectoryLocker> Lockers { get; } = lockers ?? [];
}

/// <summary>
/// O caminho calculado já está ocupado. <see cref="Registered"/> é o worktree do
/// repositório que está lá, se for um — só esse pode ser adotado.
/// </summary>
public sealed record WorktreeConflict(string Path, GitWorktree? Registered, string SuggestedPath)
{
    public bool CanAdopt => Registered is { IsPrunable: false, IsBare: false, BranchRef: not null };
}

/// <summary>
/// O resultado da preparação: tudo validado, a origem atualizada e o caminho
/// calculado. Nada foi gravado ainda — é isto que o usuário confirma ou ajusta.
/// </summary>
public sealed record DevelopmentPlan(
    Guid TaskId,
    string RepositoryPath,
    GitBranch Source,
    string NewBranch,
    string WorktreePath,
    IReadOnlyList<string> RepositoryChanges,
    WorktreeConflict? Conflict,
    /// <summary>O ambiente que tenta de novo; <c>null</c> = um repositório novo na tarefa (ADR-031).</summary>
    Guid? DevelopmentId = null);

/// <summary>Um ambiente da tarefa, como a tela o mostra.</summary>
public sealed record TaskDevelopmentView(
    Guid Id,
    Guid TaskId,
    string RepositoryPath,
    string SourceBranch,
    string Branch,
    string WorktreePath,
    TaskDevelopmentStatus Status,
    DateTimeOffset CreatedAt,
    string? FailureReason,
    IReadOnlyList<string>? Commands = null)
{
    public static TaskDevelopmentView From(TaskDevelopment development) =>
        new(
            development.Id,
            development.TaskItemId,
            development.RepositoryPath,
            development.SourceBranch,
            development.Branch,
            development.WorktreePath,
            development.Status,
            development.CreatedAt,
            development.FailureReason,
            development.Commands.Select(command => command.Command).ToList());
}

internal static class ProgressExtensions
{
    public static void Report(
        this IProgress<DevelopmentProgress>? progress,
        DevelopmentStep step,
        DevelopmentStepState state,
        string? note = null) =>
        progress?.Report(new DevelopmentProgress(step, state, note));
}
