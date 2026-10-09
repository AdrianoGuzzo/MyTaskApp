using System.Text;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Infrastructure.Processes;

/// <summary>
/// Lê uma saída de processo linha a linha, enquanto ela sai, e guarda o texto
/// com teto. Compartilhado pelo <see cref="ShellCommandExecutor"/> (ADR-028) e
/// pela execução ao vivo do <see cref="ProcessRunner"/> (ADR-056).
/// </summary>
internal static class ProcessOutputPump
{
    /// <summary>Até o fim do stream, ou até o processo morrer com o pipe aberto.</summary>
    public static async Task PumpAsync(
        StreamReader reader,
        CapturedOutput captured,
        bool isError,
        IProgress<CommandOutputLine>? output)
    {
        try
        {
            // Fora da thread de UI de propósito: um npm install são milhares de
            // linhas, e quem mostra cada uma decide como chegar à tela.
            while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } line)
            {
                captured.Append(line);
                output?.Report(new CommandOutputLine(line, isError));
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // O processo foi morto com o pipe aberto: o que chegou até aqui basta.
        }
    }
}

/// <summary>O texto de uma saída, com teto. Lido de outra thread só no fim.</summary>
internal sealed class CapturedOutput(int maxChars)
{
    private readonly StringBuilder _text = new();

    private readonly Lock _gate = new();

    private bool _truncated;

    public string Text
    {
        get
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }
    }

    public void Append(string line)
    {
        lock (_gate)
        {
            if (_truncated)
            {
                return;
            }

            if (_text.Length + line.Length + 1 > maxChars)
            {
                _text.AppendLine("… (output truncado)");
                _truncated = true;
                return;
            }

            _text.AppendLine(line);
        }
    }
}
