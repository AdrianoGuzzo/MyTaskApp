using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Development;

/// <summary>Troca a lista de comandos pós-Worktree de um ambiente que já existe (ADR-028).</summary>
public sealed record SetDevelopmentCommands(Guid TaskId, IReadOnlyList<string> Commands);

public sealed class SetDevelopmentCommandsHandler(
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    TimeProvider timeProvider,
    ILogger<SetDevelopmentCommandsHandler> logger)
{
    public async Task<TaskDevelopmentView> HandleAsync(
        SetDevelopmentCommands command,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);

        task.SetDevelopmentCommands(command.Commands, timeProvider.GetUtcNow());

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "DevelopmentCommandsSaved {TaskId} {Count}",
            task.Id,
            task.Development!.Commands.Count);

        return TaskDevelopmentView.From(task.Development);
    }
}

/// <summary>
/// Roda a lista de comandos pós-Worktree da tarefa, em ordem, dentro do
/// worktree (ADR-028).
/// </summary>
public sealed record RunDevelopmentCommands(Guid TaskId);

/// <summary>
/// Só com o ambiente <see cref="TaskDevelopmentStatus.Ready"/>: comando nenhum
/// roda num worktree que não foi criado. A lista é a gravada, e os apelidos são
/// lidos agora.
/// </summary>
/// <remarks>
/// O output não vai para o log — pode ter segredo de quem escreveu o script.
/// Vai o comando e o exit code.
/// </remarks>
public sealed class RunDevelopmentCommandsHandler(
    ITaskItemRepository tasks,
    IDevelopmentCommandRepository commands,
    ICommandExecutor executor,
    IDirectoryProbe directories,
    ILogger<RunDevelopmentCommandsHandler> logger)
{
    public async Task<CommandRunSummary> HandleAsync(
        RunDevelopmentCommands command,
        IProgress<CommandStepProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var task = await tasks.GetByIdAsync(command.TaskId, cancellationToken);

        if (task.Development is not { } development)
        {
            throw new DomainException("Esta tarefa não tem ambiente de desenvolvimento.");
        }

        if (development.Status != TaskDevelopmentStatus.Ready)
        {
            throw new DomainException("Os comandos só rodam com o worktree pronto.");
        }

        var entries = development.Commands.Select(item => item.Command).ToList();

        if (entries.Count == 0)
        {
            return CommandRunSummary.Empty;
        }

        if (!await directories.ExistsAsync(development.WorktreePath, cancellationToken))
        {
            throw new DomainException(
                $"A pasta do worktree ({development.WorktreePath}) não existe mais. Nenhum comando foi executado.");
        }

        var globals = await GlobalCommandLookup.LoadAsync(commands, cancellationToken);
        var resolved = CommandAliasResolver.Resolve(entries, globals);

        logger.LogInformation(
            "DevelopmentCommandsStarted {TaskId} {Count} {WorktreePath}",
            task.Id,
            resolved.Count,
            development.WorktreePath);

        var summary = await CommandSequence.RunAsync(
            resolved,
            development.WorktreePath,
            executor,
            progress,
            cancellationToken);

        foreach (var step in summary.Steps)
        {
            logger.LogInformation(
                "DevelopmentCommandFinished {TaskId} {Index} {State} {ExitCode} {Command}",
                task.Id,
                step.Index,
                step.State,
                step.Result?.ExitCode,
                step.Command ?? step.Entry);
        }

        return summary;
    }
}
