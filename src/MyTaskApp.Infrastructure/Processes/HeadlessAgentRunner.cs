using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;

namespace MyTaskApp.Infrastructure.Processes;

/// <summary>
/// Roda um prompt do agente sem terminal (ADR-089): o texto vai pelo stdin,
/// que fecha em seguida, e cada linha do stdout é entregue na ordem, enquanto
/// sai.
/// </summary>
/// <remarks>
/// <para>
/// Pelo stdin, e não como argumento: o <c>claude.cmd</c> do npm passaria pelo
/// <c>cmd.exe</c>, que corta o texto na quebra de linha e interpreta <c>%</c> e
/// <c>"</c>. O stdin chega inteiro nos dois instaladores.
/// </para>
/// <para>
/// O ambiente é o herdado menos o que <see cref="HeadlessEnvironment"/> tira,
/// mais o da sessão. Cancelado, a árvore inteira é encerrada; no agente, o job
/// do Windows (<see cref="KillOnCloseJob"/>) ainda leva os filhos se ele cair.
/// Os arquivos da linha de comando são apagados no fim, de qualquer jeito.
/// </para>
/// </remarks>
internal sealed class HeadlessAgentRunner(ILogger<HeadlessAgentRunner> logger) : IHeadlessAgentRunner
{
    /// <summary>O stderr só serve ao diagnóstico; o que importa sai no stdout.</summary>
    internal const int MaxErrorChars = 20_000;

    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(5);

    public async Task<HeadlessRunResult> RunAsync(
        HeadlessAgentLaunch launch,
        string prompt,
        Func<int, DateTimeOffset, Task> started,
        Func<string, Task> line,
        CancellationToken cancellationToken)
    {
        try
        {
            return await RunCoreAsync(launch, prompt, started, line, cancellationToken);
        }
        finally
        {
            foreach (var file in launch.TemporaryFiles)
            {
                TryDelete(file);
            }
        }
    }

    private async Task<HeadlessRunResult> RunCoreAsync(
        HeadlessAgentLaunch launch,
        string prompt,
        Func<int, DateTimeOffset, Task> started,
        Func<string, Task> line,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(launch.Executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = launch.WorkingDirectory,
        };

        foreach (var argument in launch.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        foreach (var name in startInfo.Environment.Keys.ToList())
        {
            if (HeadlessEnvironment.IsRemoved(name, launch.Environment))
            {
                startInfo.Environment.Remove(name);
            }
        }

        foreach (var (name, value) in launch.Environment)
        {
            startInfo.Environment[name] = value;
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            throw new CommandStartException(
                $"Não foi possível iniciar o agente ({Path.GetFileName(launch.Executable)}): {exception.Message}",
                exception);
        }

        await started(process.Id, new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero));

        var error = new CapturedOutput(MaxErrorChars);
        var readError = ProcessOutputPump.PumpAsync(process.StandardError, error, isError: true, output: null);

        try
        {
            await process.StandardInput.WriteAsync(prompt.AsMemory(), cancellationToken);
            process.StandardInput.Close();
        }
        catch (IOException exception)
        {
            // Saiu antes de ler (versão que recusa um flag, login vencido): o stderr diz o porquê.
            logger.LogWarning(exception, "HeadlessAgentStdinClosed");
        }

        var readOutput = ReadLinesAsync(process.StandardOutput, line);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            await Task.WhenAny(Task.WhenAll(readOutput, readError), Task.Delay(DrainTimeout, CancellationToken.None));
            await PropagateAsync(readOutput);

            return new HeadlessRunResult(process.ExitCode, error.Text, Canceled: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            Kill(process);
            await Task.WhenAny(Task.WhenAll(readOutput, readError), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));

            return new HeadlessRunResult(-1, error.Text, Canceled: true);
        }
    }

    /// <summary>
    /// Uma linha de cada vez: quem recebe grava em ordem, e a próxima espera.
    /// Se quem recebe falhar, o resto da saída ainda é lido (e descartado): com
    /// o pipe cheio, o processo pararia esperando, e o fim nunca chegaria.
    /// </summary>
    private static async Task ReadLinesAsync(StreamReader reader, Func<string, Task> line)
    {
        ExceptionDispatchInfo? failure = null;

        try
        {
            while (await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false) is { } text)
            {
                if (failure is not null)
                {
                    continue;
                }

                try
                {
                    await line(text).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            // Morto com o pipe aberto: o que chegou basta.
        }

        failure?.Throw();
    }

    /// <summary>Uma falha de quem recebe as linhas não pode sumir: ela volta para o executor.</summary>
    private static async Task PropagateAsync(Task readOutput)
    {
        if (readOutput.IsCompleted)
        {
            await readOutput;
        }
    }

    private void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            logger.LogDebug(exception, "HeadlessAgentAlreadyExited");
        }
    }

    private void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "HeadlessAgentFileNotDeleted");
        }
    }
}
