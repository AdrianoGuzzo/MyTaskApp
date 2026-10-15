using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Tags;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Development;

/// <summary>Por que o worktree pode sair na limpeza (ADR-065).</summary>
public enum WorktreeCleanupReason
{
    /// <summary>Nenhuma tarefa aponta para ele: apagada de vez, ou criado fora do app.</summary>
    NoTask = 0,

    Completed = 1,

    /// <summary>Nada pendente e nada concluído: tudo cancelado.</summary>
    Cancelled = 2,

    Archived = 3,

    Trashed = 4,
}

/// <summary>
/// Um worktree que a limpeza achou. <see cref="Sync"/> traz as alterações não
/// commitadas e os commits sem push — o que se perde, e o que a tela avisa.
/// </summary>
/// <param name="RepositoryPath">De onde o <c>git worktree remove</c> roda: o worktree principal.</param>
/// <param name="Branch">A branch em checkout; <c>null</c> com HEAD destacado.</param>
/// <param name="TaskId">A tarefa encerrada dona dele; <c>null</c> em <see cref="WorktreeCleanupReason.NoTask"/>.</param>
/// <param name="IsLocked">Travado com <c>git worktree lock</c>: alguém pediu para o Git não mexer.</param>
/// <param name="HasActiveAgent">Um agente de IA do app aberto nele (ADR-030): não sai.</param>
/// <param name="Exists">A pasta ainda existe; sem ela, a limpeza só faz o Git esquecer.</param>
public sealed record WorktreeCleanupCandidate(
    string RepositoryPath,
    string WorktreePath,
    string? Branch,
    WorktreeCleanupReason Reason,
    Guid? TaskId,
    string? TaskTitle,
    Guid? DevelopmentId,
    WorktreeSync Sync,
    bool IsLocked = false,
    bool HasActiveAgent = false,
    bool Exists = true)
{
    public string RepositoryName => GetTodayBoardHandler.RepositoryName(RepositoryPath) ?? RepositoryPath;
}

/// <summary>
/// O que a varredura achou. <see cref="Problems"/> são os repositórios em que o
/// Git falhou: ficam de fora, e a tela diz quais.
/// </summary>
public sealed record WorktreeCleanupScan(
    IReadOnlyList<WorktreeCleanupCandidate> Candidates,
    IReadOnlyList<string> Problems);

/// <summary>Procura os worktrees que podem sair (ADR-065). Só leitura.</summary>
public sealed record FindWorktreeCleanup;

/// <summary>
/// Varre os repositórios que o app conhece — os diretórios mapeados das
/// etiquetas e os repositórios das tarefas — e devolve todo worktree que não
/// está ligado a uma tarefa em andamento.
/// </summary>
/// <remarks>
/// <para>
/// Nunca entram o worktree principal do repositório, um repositório bare, nem
/// a pasta que é ela mesma um diretório mapeado de etiqueta: o usuário a
/// cadastrou, então está em uso, mesmo sem tarefa.
/// </para>
/// <para>
/// Worktree em criação também fica de fora, seja de quem for: a pasta está
/// nascendo agora.
/// </para>
/// </remarks>
public sealed class FindWorktreeCleanupHandler(
    ITaskItemRepository tasks,
    ITagQuery tags,
    IGitClient git,
    IDirectoryProbe directories,
    IAgentSessionRepository agentSessions,
    IAgentProcessTracker processes,
    IUnitOfWork unitOfWork,
    ProbeWorktreesHandler probe,
    TimeProvider timeProvider,
    ILogger<FindWorktreeCleanupHandler> logger)
{
    public async Task<WorktreeCleanupScan> HandleAsync(
        FindWorktreeCleanup query,
        CancellationToken cancellationToken = default)
    {
        var owned = await LinkedDevelopmentsAsync(cancellationToken);
        var mapped = await MappedRepositoriesAsync(cancellationToken);

        var repositories = Distinct(mapped.Concat(owned.Select(link => link.Development.RepositoryPath)));
        List<string> problems = [];
        List<Found> found = [];

        foreach (var repository in repositories)
        {
            if (!await directories.ExistsAsync(repository, cancellationToken))
            {
                continue;
            }

            IReadOnlyList<GitWorktree> worktrees;

            try
            {
                worktrees = await git.ListWorktreesAsync(repository, cancellationToken);
            }
            catch (GitCommandFailedException exception)
            {
                logger.LogWarning(
                    "WorktreeCleanupListFailed {Repository} {StandardError}",
                    repository,
                    exception.Result.StandardError);

                problems.Add($"{repository}: {FirstLine(exception.Result.StandardError) ?? "o Git não listou os worktrees."}");
                continue;
            }

            if (worktrees.Count == 0)
            {
                continue;
            }

            var main = worktrees[0];
            var mainPath = WorktreePathPlanner.Canonical(main.Path);

            foreach (var worktree in worktrees.Skip(1))
            {
                if (worktree.IsBare
                    || mapped.Any(directory => WorktreePathPlanner.SamePath(directory, worktree.Path))
                    || found.Any(other => WorktreePathPlanner.SamePath(other.Path, worktree.Path)))
                {
                    continue;
                }

                found.Add(new Found(
                    main.IsBare ? repository : mainPath,
                    WorktreePathPlanner.Canonical(worktree.Path),
                    worktree.BranchName,
                    main.BranchRef ?? "HEAD",
                    worktree.IsLocked,
                    worktree.IsPrunable));
            }
        }

        List<(WorktreeCleanupCandidate Candidate, string SourceRef)> candidates = [];

        foreach (var worktree in found)
        {
            var link = owned.FirstOrDefault(link => WorktreePathPlanner.SamePath(link.Development.WorktreePath, worktree.Path));

            if (link is null)
            {
                candidates.Add((
                    new WorktreeCleanupCandidate(
                        worktree.Repository,
                        worktree.Path,
                        worktree.Branch,
                        WorktreeCleanupReason.NoTask,
                        null,
                        null,
                        null,
                        WorktreeSync.Unknown(Guid.NewGuid()),
                        worktree.IsLocked,
                        Exists: !worktree.IsPrunable),
                    worktree.SourceRef));

                continue;
            }

            if (link.Development.Status is TaskDevelopmentStatus.Ready && ReasonFor(link.Task) is { } reason)
            {
                candidates.Add((
                    await FromDevelopmentAsync(link, reason, worktree.IsLocked, cancellationToken),
                    SourceRef(link.Development)));
            }
        }

        // O Git já esqueceu, mas a tarefa encerrada ainda acha que tem worktree:
        // sobra de pasta, ou pasta que sumiu. A remoção de sempre resolve os dois.
        foreach (var link in owned)
        {
            if (link.Development.Status is TaskDevelopmentStatus.Ready
                && ReasonFor(link.Task) is { } reason
                && !found.Any(worktree => WorktreePathPlanner.SamePath(worktree.Path, link.Development.WorktreePath)))
            {
                candidates.Add((
                    await FromDevelopmentAsync(link, reason, false, cancellationToken),
                    SourceRef(link.Development)));
            }
        }

        var probed = await ProbeAsync(candidates, cancellationToken);

        return new WorktreeCleanupScan(
            probed
                .OrderBy(candidate => candidate.RepositoryName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(candidate => candidate.WorktreePath, StringComparer.OrdinalIgnoreCase)
                .ToList(),
            problems);
    }

    /// <summary>
    /// O motivo para a tarefa liberar o worktree, ou <c>null</c> se ela ainda
    /// está em andamento. O estado vem de <see cref="TaskItem.Lifecycle"/>; só o
    /// "tudo cancelado", que não tem estado próprio, vem de <see cref="TaskItem.IsSettled"/>.
    /// </summary>
    public static WorktreeCleanupReason? ReasonFor(TaskItem task) =>
        task.Lifecycle switch
        {
            TaskLifecycle.Trashed => WorktreeCleanupReason.Trashed,
            TaskLifecycle.Archived => WorktreeCleanupReason.Archived,
            TaskLifecycle.Completed => WorktreeCleanupReason.Completed,
            _ => task.IsSettled ? WorktreeCleanupReason.Cancelled : null,
        };

    private async Task<IReadOnlyList<DevelopmentLink>> LinkedDevelopmentsAsync(CancellationToken cancellationToken)
    {
        var owners = await tasks.FindWithWorktreesAsync(cancellationToken);

        return owners
            .SelectMany(task => task.Developments
                .Where(development => development.Status is TaskDevelopmentStatus.Ready or TaskDevelopmentStatus.Creating)
                .Select(development => new DevelopmentLink(task, development)))
            .ToList();
    }

    /// <summary>A raiz de cada diretório mapeado que é repositório; o resto não tem worktree.</summary>
    private async Task<IReadOnlyList<string>> MappedRepositoriesAsync(CancellationToken cancellationToken)
    {
        var paths = await tags.ListMappedDirectoryPathsAsync(cancellationToken);
        List<string> roots = [];

        foreach (var path in paths)
        {
            if (!await directories.ExistsAsync(path, cancellationToken))
            {
                continue;
            }

            try
            {
                var inspection = await git.InspectAsync(path, cancellationToken);

                if (inspection is { IsRepository: true, TopLevel: { } topLevel })
                {
                    roots.Add(WorktreePathPlanner.Canonical(topLevel));
                }
            }
            catch (GitCommandFailedException exception)
            {
                logger.LogWarning("WorktreeCleanupInspectFailed {Path} {StandardError}", path, exception.Result.StandardError);
            }
        }

        return Distinct(roots);
    }

    private async Task<WorktreeCleanupCandidate> FromDevelopmentAsync(
        DevelopmentLink link,
        WorktreeCleanupReason reason,
        bool isLocked,
        CancellationToken cancellationToken)
    {
        var development = link.Development;

        return new WorktreeCleanupCandidate(
            development.RepositoryPath,
            development.WorktreePath,
            development.Branch,
            reason,
            link.Task.Id,
            link.Task.Title,
            development.Id,
            WorktreeSync.Unknown(development.Id),
            isLocked,
            await HasActiveAgentAsync(development.Id, cancellationToken),
            await directories.ExistsAsync(development.WorktreePath, cancellationToken));
    }

    /// <remarks>Sessão cujo processo já morreu é encerrada aqui, como na remoção de sempre.</remarks>
    private async Task<bool> HasActiveAgentAsync(Guid developmentId, CancellationToken cancellationToken)
    {
        var session = await agentSessions.FindLatestForDevelopmentAsync(developmentId, cancellationToken);

        if (session is null)
        {
            return false;
        }

        if (AgentSessionReconciler.EndIfGone(session, processes, timeProvider.GetUtcNow()))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            return false;
        }

        return session.IsActive;
    }

    /// <summary>
    /// As alterações e os commits sem push, com a mesma sonda da bolinha da
    /// lista (ADR-034). Sem tarefa, os commits contam a partir da branch do
    /// worktree principal: é de onde quase todo worktree nasce.
    /// </summary>
    private async Task<IReadOnlyList<WorktreeCleanupCandidate>> ProbeAsync(
        IReadOnlyList<(WorktreeCleanupCandidate Candidate, string SourceRef)> candidates,
        CancellationToken cancellationToken)
    {
        var syncs = await probe.HandleAsync(
            new ProbeWorktrees(candidates
                .Select(entry => new TaskWorktree(
                    entry.Candidate.Sync.DevelopmentId,
                    entry.Candidate.RepositoryName,
                    entry.Candidate.Branch ?? string.Empty,
                    entry.SourceRef,
                    entry.Candidate.WorktreePath))
                .ToList()),
            cancellationToken);

        return candidates
            .Select((entry, index) => entry.Candidate with { Sync = syncs[index] })
            .ToList();
    }

    // Nascida de uma tag, a branch conta os commits a partir dela (ADR-043), como na lista.
    private static string SourceRef(TaskDevelopment development) =>
        development.SourceTag is null ? development.SourceBranch : GitTag.Prefix + development.SourceTag;

    private static IReadOnlyList<string> Distinct(IEnumerable<string> paths)
    {
        List<string> distinct = [];

        foreach (var path in paths.Select(WorktreePathPlanner.Canonical))
        {
            if (!distinct.Any(other => WorktreePathPlanner.SamePath(other, path)))
            {
                distinct.Add(path);
            }
        }

        return distinct;
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();

    private sealed record DevelopmentLink(TaskItem Task, TaskDevelopment Development);

    private sealed record Found(
        string Repository,
        string Path,
        string? Branch,
        string SourceRef,
        bool IsLocked,
        bool IsPrunable);
}

/// <summary>Apaga os worktrees que o usuário deixou marcados na lista da limpeza (ADR-065).</summary>
public sealed record CleanWorktrees(IReadOnlyList<WorktreeCleanupCandidate> Worktrees);

/// <summary>Um worktree que ficou, e por quê — na língua de quem lê a tela.</summary>
public sealed record WorktreeCleanupFailure(string WorktreePath, string Message);

public sealed record WorktreeCleanupResult(int Removed, IReadOnlyList<WorktreeCleanupFailure> Failures);

/// <summary>
/// Remove, um a um, com <c>--force</c>: alterações não commitadas vão junto,
/// porque o usuário as viu na lista e confirmou. A branch fica no repositório,
/// como em toda remoção do app, e o banco copiado do ambiente também (ADR-060).
/// </summary>
/// <remarks>
/// <para>
/// Antes de cada um, confere de novo o que a lista mostrou: entre a varredura e
/// o clique, uma tarefa pode ter sido reaberta, ou um worktree sem dono pode ter
/// sido adotado por uma tarefa. Nesses casos ele fica.
/// </para>
/// <para>
/// E pergunta quem está com a pasta aberta. O Git forçado apagaria os arquivos
/// debaixo de um terminal ou de um agente rodando ali e deixaria só a pasta
/// vazia presa; na limpeza em lote, nenhum processo é encerrado — o worktree
/// fica, com o nome de quem o segura.
/// </para>
/// <para>Um que falha não impede os outros.</para>
/// </remarks>
public sealed class CleanWorktreesHandler(
    ITaskItemRepository tasks,
    IGitClient git,
    IDirectoryProbe directories,
    IDirectoryRemover remover,
    RemoveWorktreeHandler removeWorktree,
    ILogger<CleanWorktreesHandler> logger)
{
    public async Task<WorktreeCleanupResult> HandleAsync(
        CleanWorktrees command,
        CancellationToken cancellationToken = default)
    {
        var removed = 0;
        List<WorktreeCleanupFailure> failures = [];

        foreach (var worktree in command.Worktrees)
        {
            try
            {
                await CleanAsync(worktree, cancellationToken);
                removed++;

                logger.LogInformation(
                    "WorktreeCleaned {WorktreePath} {Reason} {TaskId}",
                    worktree.WorktreePath,
                    worktree.Reason,
                    worktree.TaskId);
            }
            catch (DomainException exception)
            {
                logger.LogWarning(
                    "WorktreeCleanupFailed {WorktreePath} {Message}",
                    worktree.WorktreePath,
                    exception.Message);

                failures.Add(new WorktreeCleanupFailure(worktree.WorktreePath, exception.Message));
            }
        }

        return new WorktreeCleanupResult(removed, failures);
    }

    private async Task CleanAsync(WorktreeCleanupCandidate worktree, CancellationToken cancellationToken)
    {
        await EnsureNotInUseAsync(worktree.WorktreePath, cancellationToken);

        var force = worktree.IsLocked ? WorktreeRemoveForce.ForceLocked : WorktreeRemoveForce.Force;

        // Quem é o dono agora, e não na hora da varredura: a tarefa pode ter
        // sido reaberta, apagada de vez, ou ter adotado o worktree sem dono.
        if (await OwnerAsync(worktree, cancellationToken) is { } owner)
        {
            var (task, development) = owner;

            if (FindWorktreeCleanupHandler.ReasonFor(task) is null)
            {
                throw new DomainException($"A tarefa \"{task.Title}\" está em andamento: o worktree ficou.");
            }

            await removeWorktree.HandleAsync(new RemoveWorktree(task.Id, development.Id, Force: force), cancellationToken);
            return;
        }

        await EnsureNotTheMainWorktreeAsync(worktree, cancellationToken);

        await new WorktreeEraser(git, directories, remover, logger).EraseAsync(
            worktree.RepositoryPath,
            worktree.WorktreePath,
            force,
            [],
            cancellationToken);
    }

    private async Task<(TaskItem Task, TaskDevelopment Development)?> OwnerAsync(
        WorktreeCleanupCandidate worktree,
        CancellationToken cancellationToken)
    {
        if (worktree is { TaskId: { } taskId, DevelopmentId: { } developmentId }
            && await tasks.FindByIdAsync(taskId, cancellationToken) is { } task
            && task.Developments.FirstOrDefault(development => development.Id == developmentId) is { } development)
        {
            return (task, development);
        }

        var owners = await tasks.FindWithWorktreesAsync(cancellationToken);

        foreach (var candidate in owners)
        {
            var linked = candidate.Developments.FirstOrDefault(development =>
                development.Status is TaskDevelopmentStatus.Ready or TaskDevelopmentStatus.Creating
                && WorktreePathPlanner.SamePath(development.WorktreePath, worktree.WorktreePath));

            if (linked is not null)
            {
                return (candidate, linked);
            }
        }

        return null;
    }

    private async Task EnsureNotInUseAsync(string worktreePath, CancellationToken cancellationToken)
    {
        var lockers = await remover.FindLockersAsync(worktreePath, cancellationToken);

        if (lockers.Count == 0)
        {
            return;
        }

        var who = string.Join(", ", lockers.Select(locker => $"{locker.ProcessName} (PID {locker.ProcessId})"));

        throw new DevelopmentStepException(
            DevelopmentStep.RemoveWorktree,
            $"A pasta está aberta em {who}. Feche e limpe de novo; nada foi apagado.",
            lockers: lockers)
        { IsDirectoryLocked = true };
    }

    private async Task EnsureNotTheMainWorktreeAsync(WorktreeCleanupCandidate worktree, CancellationToken cancellationToken)
    {
        try
        {
            var worktrees = await git.ListWorktreesAsync(worktree.RepositoryPath, cancellationToken);

            if (worktrees.Count > 0 && WorktreePathPlanner.SamePath(worktrees[0].Path, worktree.WorktreePath))
            {
                throw new DomainException("Este é o worktree principal do repositório: ele nunca sai na limpeza.");
            }
        }
        catch (GitCommandFailedException exception)
        {
            throw new DevelopmentStepException(
                DevelopmentStep.RemoveWorktree,
                "Não foi possível listar os worktrees do repositório.",
                exception.Result);
        }
    }
}
