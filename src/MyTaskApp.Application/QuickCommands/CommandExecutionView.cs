using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Application.QuickCommands;

/// <summary>Uma execução de comando rápido como a tela a mostra (ADR-051).</summary>
public sealed record CommandExecutionView(
    Guid Id,
    Guid TaskId,
    Guid? DevelopmentId,
    Guid? CommandId,
    Guid? BindingId,
    string CommandName,
    string CommandLine,
    string WorkingDirectory,
    CommandMode Mode,
    CommandExecutionStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    int? ProcessId,
    int? ExitCode,
    string? Output,
    string? ErrorOutput,
    string? FailureReason)
{
    public bool IsActive => Status is CommandExecutionStatus.Queued or CommandExecutionStatus.Running;

    /// <summary>Um terminal aberto, que dá para trazer para a frente.</summary>
    public bool HasTerminal => Mode == CommandMode.Terminal && Status == CommandExecutionStatus.Running && ProcessId is not null;

    public static CommandExecutionView From(CommandExecution execution) =>
        new(
            execution.Id,
            execution.TaskItemId,
            execution.TaskDevelopmentId,
            execution.DevelopmentCommandId,
            execution.TagDirectoryCommandId,
            execution.CommandName,
            execution.CommandLine,
            execution.WorkingDirectory,
            execution.Mode,
            execution.Status,
            execution.StartedAt,
            execution.FinishedAt,
            execution.ProcessId,
            execution.ExitCode,
            execution.Output,
            execution.ErrorOutput,
            execution.FailureReason);
}
