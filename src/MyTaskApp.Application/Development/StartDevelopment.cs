using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Development;

/// <summary>
/// A segunda metade de "Iniciar implementação": cria a branch e o worktree e
/// grava o ambiente na tarefa (ADR-027).
/// </summary>
/// <param name="WorktreePath">O caminho do plano, ou o que o usuário escolheu no lugar dele.</param>
/// <param name="AdoptExisting">Usar o worktree que já está no caminho, em vez de criar.</param>
/// <param name="Commands">
/// A lista de comandos pós-Worktree, gravada junto com o ambiente (ADR-028).
/// <c>null</c> mantém a que já estava. Gravar não roda nada: a tela roda depois,
/// só se o worktree ficou pronto.
/// </param>
public sealed record StartDevelopment(
    DevelopmentPlan Plan,
    string WorktreePath,
    bool AdoptExisting = false,
    IReadOnlyList<string>? Commands = null);

/// <summary>
/// Não é cancelável depois de gravar "Criando": matar o <c>git worktree add</c>
/// no meio deixa metade de um worktree registrado no repositório. O token só
/// vale até a primeira gravação.
/// </summary>
/// <remarks>
/// A ordem é o que torna a falha visível: primeiro grava
/// <see cref="TaskDevelopmentStatus.Creating"/>, depois chama o Git, depois
/// grava o resultado. Se o app cair no meio, a tarefa diz "criação
/// interrompida" em vez de não saber de nada.
/// </remarks>
public sealed class StartDevelopmentHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    IGitClient git,
    IDirectoryProbe directories,
    TimeProvider timeProvider,
    ILogger<StartDevelopmentHandler> logger)
{
    public async Task<TaskDevelopmentView> HandleAsync(
        StartDevelopment command,
        IProgress<DevelopmentProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var plan = command.Plan;
        var task = await tasks.GetByIdAsync(plan.TaskId, cancellationToken);

        var path = command.WorktreePath.Trim().Trim('"').Trim();

        if (path.Length == 0 || !Path.IsPathFullyQualified(path))
        {
            throw new DevelopmentStepException(
                DevelopmentStep.PlanWorktreePath,
                "Informe o caminho completo da pasta do worktree.");
        }

        path = WorktreePathPlanner.Canonical(path);

        return command.AdoptExisting
            ? await AdoptAsync(task, plan, path, command.Commands, progress, cancellationToken)
            : await CreateAsync(task, plan, path, command.Commands, progress, cancellationToken);
    }

    private void SetCommands(TaskItem task, IReadOnlyList<string>? commands)
    {
        if (commands is not null)
        {
            task.SetDevelopmentCommands(commands, timeProvider.GetUtcNow());
        }
    }

    private async Task<TaskDevelopmentView> CreateAsync(
        TaskItem task,
        DevelopmentPlan plan,
        string path,
        IReadOnlyList<string>? commands,
        IProgress<DevelopmentProgress>? progress,
        CancellationToken cancellationToken)
    {
        // Entre a preparação e o clique, a pasta pode ter aparecido.
        if (await directories.PathExistsAsync(path, cancellationToken))
        {
            throw new DevelopmentStepException(
                DevelopmentStep.PlanWorktreePath,
                $"O caminho {path} já existe. Escolha outro.");
        }

        task.BeginDevelopment(plan.RepositoryPath, plan.Source.ShortName, plan.NewBranch, path, timeProvider.GetUtcNow());
        SetCommands(task, commands);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Daqui em diante, nada de cancelar: ver o remarks da classe.
        var none = CancellationToken.None;

        progress.Report(DevelopmentStep.CreateWorktree, DevelopmentStepState.Running);

        var result = plan.ExistingBranch switch
        {
            null => await git.AddWorktreeAsync(plan.RepositoryPath, path, plan.NewBranch, plan.Source.FullRef, none),
            { IsRemote: true } remote =>
                await git.AddWorktreeTrackingAsync(plan.RepositoryPath, path, plan.NewBranch, remote.FullRef, none),
            _ => await git.AddWorktreeForBranchAsync(plan.RepositoryPath, path, plan.NewBranch, none),
        };

        if (!result.Succeeded)
        {
            await FailAsync(task, DevelopmentStep.CreateWorktree, CreateFailureMessage(result, plan, path), result);
        }

        progress.Report(DevelopmentStep.CreateWorktree, DevelopmentStepState.Done, path);

        progress.Report(DevelopmentStep.ValidateWorktree, DevelopmentStepState.Running);
        await ValidateAsync(task, path, plan.NewBranch);
        progress.Report(DevelopmentStep.ValidateWorktree, DevelopmentStepState.Done);

        progress.Report(DevelopmentStep.SaveTask, DevelopmentStepState.Running);
        task.MarkDevelopmentReady(timeProvider.GetUtcNow());
        await unitOfWork.SaveChangesAsync(none);
        progress.Report(DevelopmentStep.SaveTask, DevelopmentStepState.Done);

        logger.LogInformation(
            "DevelopmentStarted {TaskId} {Branch} {WorktreePath} {ExistingBranch}",
            task.Id,
            plan.NewBranch,
            path,
            plan.ExistingBranch?.FullRef);

        return TaskDevelopmentView.From(task.Development!);
    }

    /// <summary>
    /// "Usar Worktree existente": só um worktree registrado neste repositório, e
    /// a branch passa a ser a que está nele — que pode não ser a pedida.
    /// </summary>
    private async Task<TaskDevelopmentView> AdoptAsync(
        TaskItem task,
        DevelopmentPlan plan,
        string path,
        IReadOnlyList<string>? commands,
        IProgress<DevelopmentProgress>? progress,
        CancellationToken cancellationToken)
    {
        progress.Report(DevelopmentStep.CreateWorktree, DevelopmentStepState.Running);

        var worktrees = await git.ListWorktreesAsync(plan.RepositoryPath, cancellationToken);
        var registered = worktrees.FirstOrDefault(worktree => WorktreePathPlanner.SamePath(worktree.Path, path));

        if (registered is not { IsPrunable: false, IsBare: false, BranchName: { } branch })
        {
            throw new DevelopmentStepException(
                DevelopmentStep.CreateWorktree,
                $"{path} não é um worktree deste repositório com uma branch em checkout. "
                + "Não dá para usá-lo; escolha outro caminho.");
        }

        progress.Report(DevelopmentStep.CreateWorktree, DevelopmentStepState.Skipped, "Worktree existente reaproveitado.");

        progress.Report(DevelopmentStep.SaveTask, DevelopmentStepState.Running);

        var now = timeProvider.GetUtcNow();
        task.BeginDevelopment(plan.RepositoryPath, plan.Source.ShortName, branch, path, now);
        SetCommands(task, commands);
        task.MarkDevelopmentReady(now);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        progress.Report(DevelopmentStep.SaveTask, DevelopmentStepState.Done);

        logger.LogInformation("DevelopmentAdopted {TaskId} {Branch} {WorktreePath}", task.Id, branch, path);

        return TaskDevelopmentView.From(task.Development!);
    }

    private async Task ValidateAsync(TaskItem task, string path, string branch)
    {
        try
        {
            var info = await git.InspectAsync(path, CancellationToken.None);
            var current = info.IsRepository
                ? await git.GetCurrentBranchAsync(path, CancellationToken.None)
                : null;

            if (!string.Equals(current, branch, StringComparison.Ordinal))
            {
                await FailAsync(
                    task,
                    DevelopmentStep.ValidateWorktree,
                    $"O worktree foi criado, mas não está na branch {branch}. Confira {path}.",
                    info.Result);
            }
        }
        catch (GitCommandFailedException exception)
        {
            await FailAsync(
                task,
                DevelopmentStep.ValidateWorktree,
                "Não foi possível conferir o worktree criado.",
                exception.Result);
        }
    }

    /// <summary>Grava a falha na tarefa e a devolve à tela como exceção.</summary>
    private async Task FailAsync(TaskItem task, DevelopmentStep step, string message, GitCommandResult? result)
    {
        task.MarkDevelopmentFailed(message, timeProvider.GetUtcNow());

        try
        {
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            // A falha do Git é a notícia; não perder por causa do banco.
            logger.LogError(exception, "DevelopmentFailureNotSaved {TaskId}", task.Id);
        }

        logger.LogWarning(
            "DevelopmentFailed {TaskId} {Step} {ExitCode} {StandardError}",
            task.Id,
            step,
            result?.ExitCode,
            result?.StandardError);

        throw new DevelopmentStepException(step, message, result);
    }

    /// <summary>
    /// O que o Git disse, traduzido quando dá. O texto original vai sempre junto
    /// em "Ver detalhes"; o <c>LC_ALL=C</c> do cliente garante que é inglês.
    /// </summary>
    internal static string CreateFailureMessage(GitCommandResult result, DevelopmentPlan plan, string path)
    {
        var error = result.StandardError;

        if (result.TimedOut)
        {
            return "O Git demorou demais para criar o worktree.";
        }

        if (error.Contains("a branch named", StringComparison.OrdinalIgnoreCase)
            && error.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return $"A branch {plan.NewBranch} já existe.";
        }

        if (error.Contains("already exists", StringComparison.OrdinalIgnoreCase))
        {
            return $"O caminho {path} já existe.";
        }

        if (error.Contains("already checked out", StringComparison.OrdinalIgnoreCase)
            || error.Contains("already used by worktree", StringComparison.OrdinalIgnoreCase))
        {
            return "A branch já está em uso em outro worktree.";
        }

        if (error.Contains("invalid reference", StringComparison.OrdinalIgnoreCase))
        {
            return $"A branch de origem {plan.Source.ShortName} não foi encontrada.";
        }

        return "O Git não conseguiu criar o worktree.";
    }
}
