using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Agents;

namespace MyTaskApp.Infrastructure.Terminals.Windows;

/// <summary>
/// Abre o agente numa janela de console nova, hospedada pelo terminal padrão do
/// Windows (ADR-029).
/// </summary>
/// <remarks>
/// <para>
/// <b>Por que iniciar o programa direto, e não pelo <c>wt.exe</c>:</b> o
/// <c>wt.exe</c> é um lançador — entrega o pedido ao Windows Terminal e sai na
/// hora. O PID que ele devolve morre em milissegundos, e o shell de verdade vira
/// filho do <c>WindowsTerminal.exe</c>, sem ligação que se possa seguir. Iniciar
/// o programa de console direto a partir de um app gráfico faz o Windows criar
/// um console novo para ele, e quem hospeda esse console é o <b>terminal padrão
/// do usuário</b> (Windows Terminal, se estiver configurado assim; senão o
/// console clássico). O PID é o do próprio agente — vive exatamente enquanto
/// ele vive.
/// </para>
/// <para>
/// Sem shell no meio: programa e argumentos vão separados, a pasta vai como
/// diretório de trabalho do processo. Um <c>claude.cmd</c> (instalação pelo
/// npm) é aberto pelo próprio Windows com o <c>cmd.exe</c>; aí o PID é o do
/// <c>cmd.exe</c>, que também vive enquanto o Claude vive.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed class WindowsTerminalLauncher(
    Func<string, bool> fileExists,
    Func<string, bool> directoryExists,
    ILogger<WindowsTerminalLauncher> logger) : ITerminalLauncher
{
    public WindowsTerminalLauncher(ILogger<WindowsTerminalLauncher> logger)
        : this(File.Exists, Directory.Exists, logger)
    {
    }

    public Task<TerminalLaunchResult> LaunchAsync(
        TerminalLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Validate(options) is { } invalid)
        {
            return Task.FromResult(TerminalLaunchResult.Failed(invalid));
        }

        var startInfo = new ProcessStartInfo(options.Executable)
        {
            // Sem shell, com janela: o agente é interativo e o usuário conversa
            // com ele pelo terminal. Nada é redirecionado — o app não lê a sessão.
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = options.WorkingDirectory,
        };

        foreach (var argument in options.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            // Descartar o Process só solta o handle — não encerra o agente, que
            // continua vivo depois que o app fecha (ADR-029).
            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return Task.FromResult(TerminalLaunchResult.Failed("O Windows não iniciou o processo."));
            }

            return Task.FromResult(new TerminalLaunchResult
            {
                Started = true,
                ProcessId = process.Id,
                ProcessStartedAt = StartTimeOf(process),
            });
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(exception, "AgentTerminalLaunchFailed {Executable}", options.Executable);

            return Task.FromResult(TerminalLaunchResult.Failed(
                $"Não foi possível abrir o terminal: {exception.Message}"));
        }
    }

    /// <summary>
    /// Caminho absoluto e existente para os dois: nada de deixar o sistema
    /// "achar" um executável de mesmo nome na pasta errada.
    /// </summary>
    public string? Validate(TerminalLaunchOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Executable)
            || !Path.IsPathFullyQualified(options.Executable)
            || !fileExists(options.Executable))
        {
            return $"O executável não foi encontrado: {options.Executable}";
        }

        if (string.IsNullOrWhiteSpace(options.WorkingDirectory)
            || !Path.IsPathFullyQualified(options.WorkingDirectory)
            || !directoryExists(options.WorkingDirectory))
        {
            return $"A pasta não existe: {options.WorkingDirectory}";
        }

        return null;
    }

    /// <summary>
    /// O início segundo o sistema. Se ele não disser (acesso negado), a hora de
    /// agora é aproximação suficiente: o processo acabou de nascer.
    /// </summary>
    private static DateTimeOffset StartTimeOf(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime).ToUniversalTime();
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return DateTimeOffset.UtcNow;
        }
    }
}
