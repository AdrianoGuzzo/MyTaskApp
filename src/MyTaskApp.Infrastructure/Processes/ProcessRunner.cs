using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Infrastructure.Processes;

/// <summary>
/// Um processo a executar. Os argumentos vão <b>um a um</b>, nunca como uma
/// linha de comando montada: é o que torna caminho com espaço, branch com
/// <c>&amp;</c> e qualquer outra coisa digitada pelo usuário inofensivos (ADR-027).
/// </summary>
/// <remarks>
/// Em <see cref="Environment"/>, valor <c>null</c> <b>remove</b> a variável
/// herdada — é como o PostgreSQL deixa de ler um <c>PGPASSWORD</c> ou
/// <c>PGSERVICE</c> que já estivesse no ambiente (ADR-056).
/// <see cref="StandardInput"/> é escrito em UTF-8 sem BOM e a entrada é fechada
/// em seguida; é o único jeito de passar um segredo sem ele aparecer nos
/// argumentos. Nenhum dos dois entra em <see cref="Display"/>.
/// </remarks>
internal sealed record ProcessRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    TimeSpan Timeout,
    string? WorkingDirectory = null,
    IReadOnlyDictionary<string, string?>? Environment = null,
    string? StandardInput = null)
{
    /// <summary>Para logs e para "Ver detalhes". Só exibição: nunca é executado.</summary>
    public string Display =>
        string.Join(' ', Arguments.Prepend(Path.GetFileNameWithoutExtension(FileName)).Select(Quote));

    /// <summary>
    /// Records imprimem todos os membros; a entrada pode ser um segredo e o
    /// ambiente pode ter senha, então nenhum dos dois aparece aqui.
    /// </summary>
    public override string ToString() => Display;

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

    /// <summary>
    /// O mesmo, entregando cada linha enquanto ela sai — o <c>pg_dump</c> de
    /// uma hora não pode ficar mudo (ADR-056). O padrão ignora o
    /// <paramref name="output"/>, para os dublês de teste que só respondem.
    /// </summary>
    Task<ProcessResult> RunAsync(
        ProcessRequest request,
        IProgress<CommandOutputLine>? output,
        CancellationToken cancellationToken = default) =>
        RunAsync(request, cancellationToken);
}

/// <summary>
/// A única porta do app para processos externos. Sem shell, sem janela, com as
/// duas saídas lidas ao mesmo tempo — ler uma depois da outra trava quando o
/// processo enche o buffer da que ninguém está lendo.
/// </summary>
internal sealed class ProcessRunner : IProcessRunner
{
    /// <summary>Teto do que fica guardado de cada saída na execução ao vivo; o resto só passa pela tela.</summary>
    internal const int MaxStreamedChars = 1_000_000;

    public Task<ProcessResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default) =>
        RunCoreAsync(request, output: null, cancellationToken);

    public Task<ProcessResult> RunAsync(
        ProcessRequest request,
        IProgress<CommandOutputLine>? output,
        CancellationToken cancellationToken = default) =>
        RunCoreAsync(request, output ?? NoOutput.Instance, cancellationToken);

    private static async Task<ProcessResult> RunCoreAsync(
        ProcessRequest request,
        IProgress<CommandOutputLine>? output,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = StartInfoFor(request) };

        try
        {
            process.Start();
        }
        catch (Win32Exception exception)
        {
            throw new ProcessStartException(request.FileName, exception);
        }

        await FeedInputAsync(process, request.StandardInput);

        // Sem quem acompanhe, o texto vai inteiro e cru: o parser do Git lê
        // quebras de linha e espaços como vieram. Ao vivo, linha a linha.
        Task<string> readOutput;
        Task<string> readError;

        if (output is null)
        {
            readOutput = process.StandardOutput.ReadToEndAsync(CancellationToken.None);
            readError = process.StandardError.ReadToEndAsync(CancellationToken.None);
        }
        else
        {
            readOutput = PumpAsync(process.StandardOutput, isError: false, output);
            readError = PumpAsync(process.StandardError, isError: true, output);
        }

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
            await Task.WhenAny(Task.WhenAll(readOutput, readError), Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));

            return new ProcessResult(
                -1,
                readOutput.IsCompletedSuccessfully ? readOutput.Result : string.Empty,
                readError.IsCompletedSuccessfully ? readError.Result : string.Empty,
                TimedOut: true);
        }

        return new ProcessResult(process.ExitCode, await readOutput, await readError, TimedOut: false);
    }

    private static ProcessStartInfo StartInfoFor(ProcessRequest request)
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

            // Sem BOM: o secret-tool guardaria os três bytes como parte da senha.
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
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
            if (value is null)
            {
                startInfo.Environment.Remove(name);
            }
            else
            {
                startInfo.Environment[name] = value;
            }
        }

        return startInfo;
    }

    /// <summary>
    /// Ninguém vai digitar nada: um processo que pedisse entrada ficaria
    /// esperando para sempre. Fechar a entrada faz ele receber fim de arquivo —
    /// depois de entregar o que o pedido trouxe, se trouxe.
    /// </summary>
    private static async Task FeedInputAsync(Process process, string? input)
    {
        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input);
                await process.StandardInput.FlushAsync();
            }

            process.StandardInput.Close();
        }
        catch (IOException)
        {
            // Saiu antes de ler: o código de saída conta o que houve.
        }
    }

    private static async Task<string> PumpAsync(StreamReader reader, bool isError, IProgress<CommandOutputLine> output)
    {
        var captured = new CapturedOutput(MaxStreamedChars);
        await ProcessOutputPump.PumpAsync(reader, captured, isError, output);
        return captured.Text;
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

    private sealed class NoOutput : IProgress<CommandOutputLine>
    {
        public static readonly NoOutput Instance = new();

        public void Report(CommandOutputLine value)
        {
        }
    }
}
