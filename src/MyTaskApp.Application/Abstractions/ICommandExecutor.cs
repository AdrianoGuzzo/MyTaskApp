using MyTaskApp.Domain;

namespace MyTaskApp.Application.Abstractions;

/// <summary>
/// Roda uma linha de comando no shell do sistema, numa pasta, e devolve o que
/// ela disse (ADR-028). Interface para os casos de uso e as telas serem testados
/// sem abrir processo nenhum.
/// </summary>
/// <remarks>
/// Cancelar mata o processo <b>e os filhos</b> e devolve o resultado com
/// <see cref="CommandExecutionResult.Canceled"/> — o output capturado até ali
/// não se perde. Só a falha em iniciar o shell vira exceção
/// (<see cref="CommandStartException"/>); exit code diferente de zero é
/// resultado, não erro.
/// </remarks>
public interface ICommandExecutor
{
    Task<CommandExecutionResult> ExecuteAsync(
        CommandExecutionRequest request,
        IProgress<CommandOutputLine>? output = null,
        CancellationToken cancellationToken = default);
}

/// <param name="Command">O texto como iria para o terminal: pode ter <c>&amp;&amp;</c>, pipes, aspas.</param>
/// <param name="WorkingDirectory">Pasta absoluta onde o comando roda.</param>
/// <param name="Timeout"><c>null</c> usa o padrão do executor.</param>
public sealed record CommandExecutionRequest(
    string Command,
    string WorkingDirectory,
    TimeSpan? Timeout = null);

/// <summary>Uma linha de saída, na ordem em que chegou. <see cref="IsError"/> é stderr.</summary>
public sealed record CommandOutputLine(string Text, bool IsError);

public sealed record CommandExecutionResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    bool TimedOut = false,
    bool Canceled = false)
{
    public bool Success => ExitCode == 0 && !TimedOut && !Canceled;

    public TimeSpan Duration => FinishedAt - StartedAt;
}

/// <summary>O shell não abriu (não existe, sem permissão). A mensagem vai à tela como está.</summary>
public sealed class CommandStartException(string message, Exception? inner = null)
    : DomainException(message)
{
    public Exception? Cause { get; } = inner;
}
