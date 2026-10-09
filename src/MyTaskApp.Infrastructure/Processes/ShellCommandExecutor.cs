using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Infrastructure.Processes;

/// <summary>
/// Roda a linha do usuário no shell do sistema e entrega o output linha a linha,
/// enquanto ele sai (ADR-028).
/// </summary>
/// <remarks>
/// <para>
/// Irmão do <see cref="ProcessRunner"/>, não substituto: aquele roda um
/// executável conhecido com argumentos separados e lê tudo no fim; este passa
/// por um shell — é o que o usuário pediu — e precisa mostrar o output ao vivo.
/// </para>
/// <para>
/// A entrada é fechada logo: um <c>npm init</c> que perguntasse algo ficaria
/// esperando para sempre. As duas saídas são lidas ao mesmo tempo, pelo mesmo
/// motivo do <see cref="ProcessRunner"/>. Cada uma guarda no máximo
/// <see cref="MaxCapturedChars"/>; o excedente ainda vai para a tela.
/// </para>
/// </remarks>
internal sealed class ShellCommandExecutor(TimeProvider timeProvider) : ICommandExecutor
{
    /// <summary>Um <c>npm install</c> frio passa de dez minutos; meia hora é teto, não expectativa.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(30);

    internal const int MaxCapturedChars = 1_000_000;

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    public async Task<CommandExecutionResult> ExecuteAsync(
        CommandExecutionRequest request,
        IProgress<CommandOutputLine>? output = null,
        CancellationToken cancellationToken = default)
    {
        var launch = ShellCommandPlanner.ForCurrentSystem(request.Command);

        var startInfo = new ProcessStartInfo(launch.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = request.WorkingDirectory,
        };

        if (launch.RawArguments is not null)
        {
            startInfo.Arguments = launch.RawArguments;
        }

        foreach (var argument in launch.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };

        var startedAt = timeProvider.GetUtcNow();

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new CommandStartException(
                $"Não foi possível iniciar o shell ({launch.FileName}) em {request.WorkingDirectory}: {exception.Message}",
                exception);
        }

        process.StandardInput.Close();

        var standardOutput = new CapturedOutput(MaxCapturedChars);
        var standardError = new CapturedOutput(MaxCapturedChars);

        var readOutput = ProcessOutputPump.PumpAsync(process.StandardOutput, standardOutput, isError: false, output);
        var readError = ProcessOutputPump.PumpAsync(process.StandardError, standardError, isError: true, output);

        using var timeout = new CancellationTokenSource(request.Timeout ?? DefaultTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token);

            // Terminou: o que ainda está no buffer do pipe chega agora. Um
            // processo deixado em segundo plano (um servidor, um daemon) herda a
            // saída e a segura aberta — o comando acabou, não é para esperar por ele.
            await Task.WhenAny(
                Task.WhenAll(readOutput, readError),
                Task.Delay(DrainTimeout, CancellationToken.None));

            return new CommandExecutionResult(
                process.ExitCode,
                standardOutput.Text,
                standardError.Text,
                startedAt,
                timeProvider.GetUtcNow());
        }
        catch (OperationCanceledException)
        {
            Kill(process);

            // Um neto que herdou a saída pode segurá-la aberta: o que já chegou
            // fica, e esperar mais seria travar do mesmo jeito.
            await Task.WhenAny(
                Task.WhenAll(readOutput, readError),
                Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));

            var canceled = cancellationToken.IsCancellationRequested;

            return new CommandExecutionResult(
                -1,
                standardOutput.Text,
                standardError.Text,
                startedAt,
                timeProvider.GetUtcNow(),
                TimedOut: !canceled,
                Canceled: canceled);
        }
    }

    /// <summary>A árvore inteira: <c>npm</c> chama <c>node</c>, que chama o resto.</summary>
    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // Já tinha terminado, ou morreu no meio do caminho.
        }
    }
}
