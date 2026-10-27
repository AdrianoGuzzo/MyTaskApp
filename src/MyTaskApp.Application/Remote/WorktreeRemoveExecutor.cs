using System.Text.Json;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Development;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>O ambiente que o pedido quer apagar, como está nesta máquina.</summary>
public sealed record RemoteRemovalTarget(Guid TaskId, Guid DevelopmentId, string TaskTitle, string Branch);

/// <summary>
/// Confere um <c>worktree.remove</c> antes de perguntar a alguém: o ambiente
/// existe, foi criado aqui, e não tem o Claude aberto (ADR-030). O caso de uso
/// confere de novo na hora de apagar.
/// </summary>
public sealed class RemoteRemovalPlanner(
    ITaskItemRepository tasks,
    IAgentSessionRepository sessions,
    IAgentProcessTracker processes,
    TimeProvider timeProvider)
{
    public async Task<RemoteRemovalTarget> PlanAsync(Guid? taskId, WorktreeRemovalRequest request, CancellationToken cancellationToken = default)
    {
        if (taskId != request.TaskId)
        {
            throw new DomainException("O pedido diz uma tarefa e o ambiente, outra.");
        }

        var task = await tasks.GetByIdAsync(request.TaskId, cancellationToken);
        var development = task.Developments.FirstOrDefault(item => item.Id == request.DevelopmentId)
            ?? throw new DomainException("A tarefa não tem esse ambiente nesta máquina.");

        if (development.Status is TaskDevelopmentStatus.Removed or TaskDevelopmentStatus.NotCreatedHere)
        {
            throw new DomainException(development.Status == TaskDevelopmentStatus.Removed
                ? "Esse worktree já foi apagado."
                : "Esse worktree não foi criado nesta máquina.");
        }

        // Sem gravar: a sessão que acabou sem avisar só não segura o pedido; o caso de uso grava o fim dela.
        if (await sessions.FindLatestForDevelopmentAsync(development.Id, cancellationToken) is { IsActive: true } session
            && !AgentSessionReconciler.EndIfGone(session, processes, timeProvider.GetUtcNow()))
        {
            throw new DomainException("O Claude está aberto neste worktree. Encerre a sessão antes de apagá-lo.");
        }

        return new RemoteRemovalTarget(task.Id, development.Id, task.Title, development.Branch);
    }
}

/// <summary>
/// <c>worktree.remove</c> (E4): o "Remover worktree" da aba Desenvolvimento,
/// pedido de longe, <b>sempre</b> com aprovação. As travas da tela valem
/// igual, porque é o mesmo caso de uso: com o Claude aberto no worktree, nada
/// é apagado; sem <c>Force</c>, alterações não commitadas recusam; a branch
/// fica no repositório.
/// </summary>
/// <remarks>
/// Quem segura a pasta não é encerrado de longe: a sobra fica para a limpeza
/// da tela (ADR-029, ADR-065).
/// </remarks>
public sealed class WorktreeRemoveExecutor(IUseCaseRunner runner) : IRemoteOperationExecutor
{
    public string Kind => RemoteOperationKinds.WorktreeRemove;

    public async Task<RemoteRefusal?> CheckAsync(RemoteOperationRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await TargetAsync(request, cancellationToken);
            return null;
        }
        catch (DomainException exception)
        {
            return new RemoteRefusal(RemoteErrorCodes.PolicyRefused, exception.Message);
        }
    }

    public async Task<RemoteOperationResult> ExecuteAsync(RemoteExecution execution, CancellationToken cancellationToken)
    {
        var target = await TargetAsync(execution.Request, cancellationToken);
        var force = execution.Payload<WorktreeRemovalRequest>().Force;

        await execution.EmitAsync(RemoteEventKind.StepStarted, "Conferindo as alterações do worktree.", "inspect", cancellationToken: cancellationToken);
        var inspection = await runner.RunAsync<InspectWorktreeHandler, WorktreeInspection>(
            (handler, token) => handler.HandleAsync(new InspectWorktree(target.TaskId, target.DevelopmentId), token),
            cancellationToken);

        if (!inspection.IsClean && !force)
        {
            return RemoteOperationResult.Failure(
                RemoteErrorCodes.PolicyRefused,
                $"O worktree de {target.Branch} tem {Changes(inspection.Changes.Count)} não commitadas. Nada foi apagado; para descartá-las, peça de novo com \"forçar\".");
        }

        var what = $"Apagar o worktree de {target.Branch} (\"{target.TaskTitle}\")";
        var summary = inspection.IsClean
            ? $"{what}, a pedido de {execution.ControllerNickname}. A branch fica no repositório."
            : $"{what}, a pedido de {execution.ControllerNickname}. As {Changes(inspection.Changes.Count)} não commitadas vão junto; a branch fica.";

        var approval = await execution.RequestApprovalAsync(
            new RemoteApprovalAsk(
                ApprovalKind.WorktreeRemoval,
                SensitiveText.Mask(summary),
                JsonSerializer.SerializeToElement(
                    new Dictionary<string, object>
                    {
                        ["developmentId"] = target.DevelopmentId,
                        ["branch"] = target.Branch,
                        ["uncommittedChanges"] = inspection.Changes.Count,
                        ["force"] = force,
                    },
                    RemoteJson.Options)),
            cancellationToken);

        if (!approval.IsAllowed)
        {
            return approval.AsFailure(what);
        }

        await execution.EmitAsync(RemoteEventKind.StepStarted, "Apagando o worktree.", "remove", cancellationToken: cancellationToken);

        try
        {
            // Sem o token: o Git no meio do remove deixa metade do worktree.
            await runner.RunAsync<RemoveWorktreeHandler, TaskDevelopmentView>(
                (handler, token) => handler.HandleAsync(
                    new RemoveWorktree(
                        target.TaskId,
                        target.DevelopmentId,
                        Force: force && !inspection.IsClean ? WorktreeRemoveForce.Force : WorktreeRemoveForce.None),
                    token),
                CancellationToken.None);
        }
        catch (DevelopmentStepException exception)
        {
            return RemoteOperationResult.Failure(
                RemoteErrorCodes.ExecutionFailed,
                exception.IsDirectoryLocked
                    ? "O Git tirou o worktree, mas a pasta está presa por um programa nesta máquina. Ela sai pela limpeza de worktrees, aqui."
                    : SensitiveText.Mask(exception.Message));
        }
        catch (DomainException exception)
        {
            // O Claude abriu entre a aprovação e agora: a trava do caso de uso.
            return RemoteOperationResult.Failure(RemoteErrorCodes.PolicyRefused, exception.Message);
        }

        return RemoteOperationResult.Success($"Worktree de {target.Branch} apagado. A branch ficou no repositório.");
    }

    private static string Changes(int count) => count == 1 ? "1 alteração" : $"{count} alterações";

    private Task<RemoteRemovalTarget> TargetAsync(RemoteOperationRequest request, CancellationToken cancellationToken) =>
        runner.RunAsync<RemoteRemovalPlanner, RemoteRemovalTarget>(
            (planner, token) => planner.PlanAsync(request.TaskId, RemoteOperationRules.PayloadOf<WorktreeRemovalRequest>(request), token),
            cancellationToken);
}
