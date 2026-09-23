using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace MyTaskApp.Infrastructure.Processes;

/// <summary>
/// Um processo a executar. Os argumentos vão <b>um a um</b>, nunca como uma
/// linha de comando montada: é o que torna caminho com espaço, branch com
/// <c>&amp;</c> e qualquer outra coisa digitada pelo usuário inofensivos (ADR-027).
/// </summary>
internal sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null)
{
    /// <summary>Para logs e para "Ver detalhes". Só exibição: nunca é executado.</summary>
    public string Display =>
        string.Join(' ', Arguments.Prepend(Path.GetFileNameWithoutExtension(FileName)).Select(Quote));

    private static string Quote(string argument) =>
        argument.Length == 0 || argument.Any(char.IsWhiteSpace) || argument.Contains('"', StringComparison.Ordinal)
            ? $"\"{argument.Replace("\"", "\\\"", StringComparison.Ordinal)}\""
            : argument;
}

internal sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError, bool TimedOut);

/// <summary>O executável não existe ou não pôde ser iniciado.</summary>
internal sealed class ProcessStartException(string fileName, Exception inner)
    : Exception($"Não foi possível iniciar {fileName}.", inner);

/// <summary>
/// Roda um processo externo e devolve o que ele disse. Interface para os testes
/// do <see cref="Git.GitClient"/> simularem o Git sem executar nada.
/// </summary>
internal interface IProcessRunner
{
    /// <summary>
    /// Espera o fim, com timeout. Estourar o tempo mata a árvore do processo e
    /// devolve <see cref="ProcessResult.TimedOut"/>; cancelar mata e lança
    /// <see cref="OperationCanceledException"/>.
    /// </summary>
    Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default);
}

/// <summary>
/// A única porta do app para processos externos. Sem shell, sem janela, com as
/// duas saídas lidas ao mesmo tempo — ler uma depois da outra trava quando o
/// processo enche o buffer da que ninguém está lendo.
/// </summary>
internal sealed class ProcessRunner : IProcessRunner
{
    public async Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        var startInfo = new ProcessStartInfo(request.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (request.WorkingDirectory is not null)
        {
            startInfo.WorkingDirectory = request.WorkingDirectory;
        }

        foreach (var (name, value) in request.Environment ?? new Dictionary<string, string?>())
        {
            startInfo.Environment[name] = value;
        }

        using var process = new Process { StartInfo = startInfo };

        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new ProcessStartException(request.FileName, exception);
        }

        // Ninguém vai digitar nada: um processo que pedisse entrada ficaria
        // esperando para sempre. Fechar a entrada faz ele receber fim de arquivo.
        process.StandardInput.Close();

        var output = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var error = process.StandardError.ReadToEndAsync(CancellationToken.None);

        using var timeout = new CancellationTokenSource(request.Timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

        try
        {
            await process.WaitForExitAsync(linked.Token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);

            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            // Um neto que herdou a saída pode segurá-la aberta: o que já chegou
            // basta para o diagnóstico, e esperar mais seria o mesmo travamento.
            await Task.WhenAny(Task.WhenAll(output, error), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));

            return new ProcessResult(
                -1,
                output.IsCompletedSuccessfully ? output.Result : string.Empty,
                error.IsCompletedSuccessfully ? error.Result : string.Empty,
                TimedOut: true);
        }

        return new ProcessResult(process.ExitCode, await output, await error, TimedOut: false);
    }

    /// <summary>A árvore inteira: o git chama ssh, credential helper, hooks.</summary>
    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(5000);
        }
        catch (InvalidOperationException)
        {
            // Já tinha terminado.
        }
    }
}
