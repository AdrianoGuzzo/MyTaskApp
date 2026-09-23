using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Commands;

public enum CommandStepState
{
    Waiting,
    Running,
    Succeeded,
    Failed,
    Canceled,

    /// <summary>Não chegou a vez: um anterior falhou ou a execução foi cancelada.</summary>
    NotRun,
}

/// <summary>
/// O que acontece com uma etapa, na hora: começou (com o comando resolvido),
/// escreveu uma linha (<see cref="Line"/>), terminou (<see cref="Result"/>).
/// </summary>
public sealed record CommandStepProgress(
    int Index,
    CommandStepState State,
    string? Command = null,
    CommandOutputLine? Line = null,
    CommandExecutionResult? Result = null,
    string? Error = null);

public sealed record CommandStepResult(
    int Index,
    string Entry,
    string? Command,
    CommandStepState State,
    CommandExecutionResult? Result,
    string? Error);

public sealed record CommandRunSummary(IReadOnlyList<CommandStepResult> Steps)
{
    public static readonly CommandRunSummary Empty = new([]);

    public bool Succeeded => Steps.All(step => step.State == CommandStepState.Succeeded);

    public bool WasCanceled => Steps.Any(step => step.State == CommandStepState.Canceled);
}

/// <summary>
/// Roda as etapas em ordem, uma de cada vez, e para na primeira que não der
/// certo (ADR-028). As seguintes ficam <see cref="CommandStepState.NotRun"/>.
/// </summary>
internal static class CommandSequence
{
    public static async Task<CommandRunSummary> RunAsync(
        IReadOnlyList<ResolvedCommand> steps,
        string workingDirectory,
        ICommandExecutor executor,
        IProgress<CommandStepProgress>? progress,
        CancellationToken cancellationToken)
    {
        var results = new List<CommandStepResult>(steps.Count);
        var stopped = false;

        foreach (var step in steps)
        {
            if (stopped || cancellationToken.IsCancellationRequested)
            {
                // Cancelado entre duas etapas: a que ia começar é a cancelada,
                // as outras nem chegaram a vez.
                var skipped = stopped ? CommandStepState.NotRun : CommandStepState.Canceled;

                results.Add(new CommandStepResult(step.Index, step.Entry, step.Command, skipped, null, null));
                progress?.Report(new CommandStepProgress(step.Index, skipped, step.Command));
                stopped = true;
                continue;
            }

            if (step.Command is null)
            {
                results.Add(new CommandStepResult(step.Index, step.Entry, null, CommandStepState.Failed, null, step.Error));
                progress?.Report(new CommandStepProgress(step.Index, CommandStepState.Failed, Error: step.Error));
                stopped = true;
                continue;
            }

            progress?.Report(new CommandStepProgress(step.Index, CommandStepState.Running, step.Command));

            var lines = progress is null ? null : new LineForwarder(step.Index, progress);

            CommandStepResult result;

            try
            {
                var execution = await executor.ExecuteAsync(
                    new CommandExecutionRequest(step.Command, workingDirectory),
                    lines,
                    cancellationToken);

                var state = execution.Success
                    ? CommandStepState.Succeeded
                    : execution.Canceled
                        ? CommandStepState.Canceled
                        : CommandStepState.Failed;

                var error = execution.TimedOut ? "O comando demorou demais e foi interrompido." : null;

                result = new CommandStepResult(step.Index, step.Entry, step.Command, state, execution, error);
            }
            catch (CommandStartException exception)
            {
                result = new CommandStepResult(
                    step.Index, step.Entry, step.Command, CommandStepState.Failed, null, exception.Message);
            }

            results.Add(result);
            progress?.Report(new CommandStepProgress(
                step.Index, result.State, step.Command, Result: result.Result, Error: result.Error));

            stopped = result.State != CommandStepState.Succeeded;
        }

        return new CommandRunSummary(results);
    }

    /// <summary>Carimba cada linha com a etapa que a escreveu.</summary>
    private sealed class LineForwarder(int index, IProgress<CommandStepProgress> progress)
        : IProgress<CommandOutputLine>
    {
        public void Report(CommandOutputLine value) =>
            progress.Report(new CommandStepProgress(index, CommandStepState.Running, Line: value));
    }
}
