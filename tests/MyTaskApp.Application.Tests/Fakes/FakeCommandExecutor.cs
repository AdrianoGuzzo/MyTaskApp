using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>
/// Comandos de mentira (ADR-028): nenhum processo abre. Cada comando responde o
/// que o teste roteirizou — por padrão, uma linha de stdout e exit 0 — e fica
/// registrado com a pasta onde "rodou".
/// </summary>
internal sealed class FakeCommandExecutor : ICommandExecutor
{
    public static readonly DateTimeOffset Started = new(2026, 9, 23, 10, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<string, (int ExitCode, string[] Output, string[] Errors)> _scripts = [];

    public List<CommandExecutionRequest> Requests { get; } = [];

    /// <summary>Comandos que ficam rodando até o token ser cancelado.</summary>
    public HashSet<string> Hanging { get; } = [];

    /// <summary>Comandos cujo shell não abre.</summary>
    public HashSet<string> Unstartable { get; } = [];

    /// <summary>Avisa quando um comando que "trava" começou a esperar.</summary>
    public TaskCompletionSource HangStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IEnumerable<string> Commands => Requests.Select(request => request.Command);

    public void Script(string command, int exitCode, string[]? output = null, string[]? errors = null) =>
        _scripts[command] = (exitCode, output ?? [], errors ?? []);

    public async Task<CommandExecutionResult> ExecuteAsync(
        CommandExecutionRequest request,
        IProgress<CommandOutputLine>? output = null,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);

        if (Unstartable.Contains(request.Command))
        {
            throw new CommandStartException("Não foi possível iniciar o shell.");
        }

        var (exitCode, lines, errors) = _scripts.TryGetValue(request.Command, out var script)
            ? script
            : (0, [$"ok: {request.Command}"], []);

        foreach (var line in lines)
        {
            output?.Report(new CommandOutputLine(line, false));
        }

        foreach (var line in errors)
        {
            output?.Report(new CommandOutputLine(line, true));
        }

        var stdout = string.Concat(lines.Select(line => line + Environment.NewLine));
        var stderr = string.Concat(errors.Select(line => line + Environment.NewLine));

        if (Hanging.Contains(request.Command))
        {
            HangStarted.TrySetResult();

            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Como o executor real: cancelar é resultado, com o output de até ali.
                return new CommandExecutionResult(-1, stdout, stderr, Started, Started.AddSeconds(1), Canceled: true);
            }
        }

        return new CommandExecutionResult(exitCode, stdout, stderr, Started, Started.AddSeconds(2));
    }
}
