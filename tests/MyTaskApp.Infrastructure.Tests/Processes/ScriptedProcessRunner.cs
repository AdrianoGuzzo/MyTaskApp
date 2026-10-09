using MyTaskApp.Application.Abstractions;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Tests.Processes;

/// <summary>
/// Um <see cref="IProcessRunner"/> que não executa nada: responde pelo
/// executável e pelo primeiro argumento, entrega as linhas roteirizadas ao
/// vivo e guarda cada pedido para o teste conferir argumentos, ambiente e
/// entrada.
/// </summary>
internal sealed class ScriptedProcessRunner : IProcessRunner
{
    private readonly List<(Func<ProcessRequest, bool> Matches, Func<ProcessRequest, Script> Script)> _scripts = [];

    public List<ProcessRequest> Requests { get; } = [];

    public ProcessStartException? StartFailure { get; set; }

    /// <summary>Lança <see cref="OperationCanceledException"/> em todo pedido.</summary>
    public bool Cancels { get; set; }

    /// <summary>Quando definido, é chamado antes de responder — para cancelar no meio, por exemplo.</summary>
    public Action<ProcessRequest>? OnRun { get; set; }

    public ScriptedProcessRunner When(Func<ProcessRequest, bool> matches, ProcessResult result, params CommandOutputLine[] lines)
    {
        _scripts.Add((matches, _ => new Script(result, lines)));
        return this;
    }

    public ScriptedProcessRunner When(Func<ProcessRequest, bool> matches, Func<ProcessRequest, ProcessResult> result)
    {
        _scripts.Add((matches, request => new Script(result(request), [])));
        return this;
    }

    /// <summary>Pelo nome do executável, sem pasta e sem extensão, e opcionalmente o primeiro argumento.</summary>
    public ScriptedProcessRunner WhenTool(string tool, ProcessResult result, params CommandOutputLine[] lines) =>
        When(request => ToolOf(request) == tool, result, lines);

    public static string ToolOf(ProcessRequest request) => Path.GetFileNameWithoutExtension(request.FileName);

    public static ProcessResult Ok(string output = "", string error = "") => new(0, output, error, false);

    public static ProcessResult Fail(int exitCode, string error = "") => new(exitCode, string.Empty, error, false);

    public static ProcessResult TimedOut() => new(-1, string.Empty, string.Empty, true);

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) =>
        RunAsync(request, null, cancellationToken);

    public Task<ProcessResult> RunAsync(
        ProcessRequest request,
        IProgress<CommandOutputLine>? output,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(request);
        OnRun?.Invoke(request);

        if (StartFailure is not null)
        {
            throw StartFailure;
        }

        if (Cancels || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        foreach (var (matches, script) in _scripts)
        {
            if (!matches(request))
            {
                continue;
            }

            var answer = script(request);

            foreach (var line in answer.Lines)
            {
                output?.Report(line);
            }

            return Task.FromResult(answer.Result);
        }

        return Task.FromResult(Ok());
    }

    private sealed record Script(ProcessResult Result, IReadOnlyList<CommandOutputLine> Lines);
}
