using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Commands;

namespace MyTaskApp.Application.QuickCommands;

/// <summary>
/// O banco não é a verdade; o processo é (ADR-030, ADR-051). Uma execução
/// ativa cujo processo já não existe termina aqui, antes de chegar à tela.
/// </summary>
internal static class CommandExecutionReconciler
{
    /// <summary>
    /// Quanto uma execução pode ficar gravada sem processo antes de ser dada
    /// por perdida: o caso de uso grava antes de abrir o terminal.
    /// </summary>
    public static readonly TimeSpan QueuedGrace = TimeSpan.FromMinutes(2);

    public const string InterruptedMessage = "O app foi fechado enquanto o comando rodava.";

    /// <returns><c>true</c> quando a execução mudou e precisa ser gravada.</returns>
    public static bool EndIfGone(
        CommandExecution execution,
        IAgentProcessTracker processes,
        ICommandExecutionWatcher watcher,
        DateTimeOffset now)
    {
        if (!execution.IsActive || watcher.IsInProcess(execution.Id))
        {
            return false;
        }

        if (execution is { ProcessId: { } processId, ProcessStartedAt: { } startedAt })
        {
            // Saiu sem o vigia ver (app fechado): o exit code se perdeu.
            return !processes.IsAlive(processId, startedAt) && execution.Finish(null, null, null, now);
        }

        if (execution.Status == CommandExecutionStatus.Running)
        {
            // Escondida e não está neste processo: o app que a rodava caiu.
            return execution.Fail(InterruptedMessage, now);
        }

        return now - execution.StartedAt >= QueuedGrace
               && execution.Fail("A execução não chegou a começar.", now);
    }
}

public sealed record CommandReconciliation(int Alive, int Ended)
{
    public static readonly CommandReconciliation Nothing = new(0, 0);
}

/// <summary>
/// Ao abrir o app: terminais que fecharam com ele fechado terminam, os que
/// continuam abertos voltam a ser vigiados. Nenhuma execução é criada.
/// </summary>
public sealed record ReconcileCommandExecutions;

public sealed class ReconcileCommandExecutionsHandler(
    ICommandExecutionRepository executions,
    IUnitOfWork unitOfWork,
    IAgentProcessTracker processes,
    ICommandExecutionWatcher watcher,
    TimeProvider timeProvider)
{
    public async Task<CommandReconciliation> HandleAsync(
        ReconcileCommandExecutions command,
        CancellationToken cancellationToken = default)
    {
        var active = await executions.ListActiveAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var ended = active.Where(execution => CommandExecutionReconciler.EndIfGone(execution, processes, watcher, now)).ToList();

        if (ended.Count > 0)
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
        }

        foreach (var taskId in ended.Select(execution => execution.TaskItemId).Distinct())
        {
            watcher.NotifyChanged(taskId);
        }

        var alive = active.Except(ended).Select(CommandExecutionWatch.For).OfType<CommandExecutionWatch>().ToList();

        foreach (var watch in alive)
        {
            watcher.Watch(watch);
        }

        return new CommandReconciliation(alive.Count, ended.Count);
    }
}

/// <summary>O processo do terminal saiu. Chega do vigia, com o exit code quando o sistema o deu.</summary>
public sealed record EndCommandExecution(Guid ExecutionId, int? ExitCode);

public sealed class EndCommandExecutionHandler(
    ICommandExecutionRepository executions,
    IUnitOfWork unitOfWork,
    ICommandExecutionWatcher watcher,
    TimeProvider timeProvider,
    ILogger<EndCommandExecutionHandler> logger)
{
    public async Task HandleAsync(EndCommandExecution command, CancellationToken cancellationToken = default)
    {
        if (await executions.FindByIdAsync(command.ExecutionId, cancellationToken) is not { } execution)
        {
            return;
        }

        // Com /k o exit code é o de quem fechou a janela, e não do comando: não
        // diz nada. Só o /c devolve o do comando.
        var exitCode = execution.KeepTerminalOpen ? null : command.ExitCode;

        if (!execution.Finish(exitCode, null, null, timeProvider.GetUtcNow()))
        {
            return;
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "QuickCommandTerminalClosed {TaskId} {ExecutionId} {Status} {ExitCode}",
            execution.TaskItemId,
            execution.Id,
            execution.Status,
            execution.ExitCode);

        watcher.NotifyChanged(execution.TaskItemId);
    }
}

/// <summary>"Mostrar terminal": traz para a frente a janela de um comando rápido aberto.</summary>
public sealed record FocusCommandExecution(Guid ExecutionId);

public sealed record CommandFocusResult(CommandExecutionView Execution, bool Focused);

public sealed class FocusCommandExecutionHandler(
    ICommandExecutionRepository executions,
    IUnitOfWork unitOfWork,
    IAgentProcessTracker processes,
    ITerminalWindowManager windows,
    ICommandExecutionWatcher watcher,
    TimeProvider timeProvider,
    ILogger<FocusCommandExecutionHandler> logger)
{
    public async Task<CommandFocusResult> HandleAsync(
        FocusCommandExecution command,
        CancellationToken cancellationToken = default)
    {
        var execution = await executions.FindByIdAsync(command.ExecutionId, cancellationToken)
            ?? throw new DomainException("Esta execução não existe mais.");

        if (CommandExecutionReconciler.EndIfGone(execution, processes, watcher, timeProvider.GetUtcNow()))
        {
            await unitOfWork.SaveChangesAsync(cancellationToken);
            watcher.NotifyChanged(execution.TaskItemId);
        }

        if (execution is not { Status: CommandExecutionStatus.Running, ProcessId: { } processId })
        {
            return new CommandFocusResult(CommandExecutionView.From(execution), false);
        }

        var focused = await windows.FocusAsync(processId);

        if (!focused)
        {
            logger.LogWarning("QuickCommandWindowNotFound {TaskId} {ProcessId}", execution.TaskItemId, processId);
        }

        return new CommandFocusResult(CommandExecutionView.From(execution), focused);
    }
}
