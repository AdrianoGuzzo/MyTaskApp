using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Mobile.Core.Presentation;

/// <summary>Um passo de mudança de status, sempre pelo caso de uso que já existe.</summary>
public enum StatusStep
{
    Complete,
    Cancel,
    Reopen,
}

/// <summary>
/// Como ir de um status a outro só com as transições do domínio
/// (<see cref="TaskOccurrence"/>): concluir e cancelar saem de pendente, e
/// reabrir volta para pendente. De concluída para cancelada é reabrir e
/// cancelar — nada de pular a regra.
/// </summary>
public static class StatusTransitions
{
    public static IReadOnlyList<StatusStep> Plan(TaskItemStatus from, TaskItemStatus to) => (from, to) switch
    {
        _ when from == to => [],
        (TaskItemStatus.Pending, TaskItemStatus.Completed) => [StatusStep.Complete],
        (TaskItemStatus.Pending, TaskItemStatus.Cancelled) => [StatusStep.Cancel],
        (_, TaskItemStatus.Pending) => [StatusStep.Reopen],
        (_, TaskItemStatus.Completed) => [StatusStep.Reopen, StatusStep.Complete],
        (_, TaskItemStatus.Cancelled) => [StatusStep.Reopen, StatusStep.Cancel],
        _ => [],
    };
}
