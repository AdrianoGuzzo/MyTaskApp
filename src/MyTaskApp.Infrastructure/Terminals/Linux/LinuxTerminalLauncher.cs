using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Agents;

namespace MyTaskApp.Infrastructure.Terminals.Linux;

/// <summary>Um terminal do Linux e como pedir a ele que rode um programa e só saia junto com ele.</summary>
/// <param name="Before">Os argumentos antes do programa: o que mantém o processo vivo e o que diz "rode isto".</param>
internal sealed record LinuxTerminal(string Name, IReadOnlyList<string> Before);

/// <summary>O que iniciar, ou por que não dá.</summary>
internal sealed record LinuxTerminalPlan(string? FileName, IReadOnlyList<string> Arguments, string? Error = null);

/// <summary>
/// Abre o programa numa janela de terminal no Linux (ADR-030; fase 3, E9).
/// </summary>
/// <remarks>
/// <para>
/// <b>Só terminais que vivem enquanto o programa vive.</b> O PID devolvido é o
/// da sessão (ADR-030): o <c>gnome-terminal</c> comum entrega o pedido a um
/// servidor e sai na hora, como o <c>wt.exe</c> no Windows. Por isso cada um
/// vai com a opção que o segura (<c>--wait</c>, <c>--nofork</c>,
/// <c>--disable-server</c>), e o <c>x-terminal-emulator</c>, que pode ser
/// qualquer um, fica de fora.
/// </para>
/// <para>
/// <b>A pasta e o ambiente pelo <c>sh</c>, sem montar linha de shell.</b> O
/// terminal pode ignorar o diretório e o ambiente de quem o abriu (o servidor
/// do GNOME tem os dele). O <c>sh -c 'cd -- "$1" … exec "$@"'</c> recebe a
/// pasta, as variáveis (pelo <c>env</c>) e o programa como argumentos
/// separados: nada do que vem da tarefa vira comando.
/// </para>
/// </remarks>
internal sealed class LinuxTerminalLauncher(
    Func<string, string?> findExecutable,
    Func<string, bool> fileExists,
    Func<string, bool> directoryExists,
    ILogger<LinuxTerminalLauncher> logger) : ITerminalLauncher
{
    public const string Shell = "/bin/sh";

    public const string Env = "/usr/bin/env";

    /// <summary>Entra na pasta e troca o <c>sh</c> pelo programa: o PID segue sendo o da janela.</summary>
    public const string Script = "cd -- \"$1\" || exit 1; shift; exec \"$@\"";

    public const string NoTerminal =
        "Nenhum terminal conhecido foi encontrado. Instale o gnome-terminal, o konsole, o xfce4-terminal ou o xterm.";

    /// <summary>Na ordem de preferência.</summary>
    public static readonly IReadOnlyList<LinuxTerminal> Terminals =
    [
        new("gnome-terminal", ["--wait", "--"]),
        new("konsole", ["--nofork", "-e"]),
        new("xfce4-terminal", ["--disable-server", "-x"]),
        new("xterm", ["-e"]),
    ];

    public LinuxTerminalLauncher(ILogger<LinuxTerminalLauncher> logger)
        : this(FindOnPath, File.Exists, Directory.Exists, logger)
    {
    }

    public Task<TerminalLaunchResult> LaunchAsync(
        TerminalLaunchOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var plan = Plan(options);

        if (plan.FileName is null)
        {
            return Task.FromResult(TerminalLaunchResult.Failed(plan.Error ?? NoTerminal));
        }

        var startInfo = new ProcessStartInfo(plan.FileName)
        {
            UseShellExecute = false,
            WorkingDirectory = options.WorkingDirectory,
        };

        foreach (var argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            // Descartar o Process só solta o handle: o terminal continua depois que o app fecha (ADR-030).
            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return Task.FromResult(TerminalLaunchResult.Failed("O sistema não iniciou o terminal."));
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
            logger.LogWarning(exception, "TerminalLaunchFailed {Terminal} {Executable}", plan.FileName, options.Executable);

            return Task.FromResult(TerminalLaunchResult.Failed($"Não foi possível abrir o terminal: {exception.Message}"));
        }
    }

    /// <summary>O primeiro terminal instalado, com o programa, a pasta e o ambiente como argumentos separados.</summary>
    public LinuxTerminalPlan Plan(TerminalLaunchOptions options)
    {
        if (Validate(options) is { } invalid)
        {
            return new LinuxTerminalPlan(null, [], invalid);
        }

        foreach (var terminal in Terminals)
        {
            if (findExecutable(terminal.Name) is not { } path)
            {
                continue;
            }

            var environment = (options.Environment ?? new Dictionary<string, string>())
                .Select(pair => $"{pair.Key}={pair.Value}")
                .ToList();

            return new LinuxTerminalPlan(
                path,
                [
                    .. terminal.Before,
                    Shell, "-c", Script, "mytaskapp", options.WorkingDirectory,
                    .. environment.Count > 0 ? [Env, .. environment] : Array.Empty<string>(),
                    options.Executable,
                    .. options.Arguments,
                ]);
        }

        return new LinuxTerminalPlan(null, [], NoTerminal);
    }

    /// <summary>Caminho absoluto e existente para os dois, como no Windows.</summary>
    private string? Validate(TerminalLaunchOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.Executable)
            || !IsAbsolute(options.Executable)
            || !fileExists(options.Executable))
        {
            return $"O executável não foi encontrado: {options.Executable}";
        }

        if (string.IsNullOrWhiteSpace(options.WorkingDirectory)
            || !IsAbsolute(options.WorkingDirectory)
            || !directoryExists(options.WorkingDirectory))
        {
            return $"A pasta não existe: {options.WorkingDirectory}";
        }

        // A linha crua é do cmd do Windows (ADR-051); aqui os argumentos vão sempre separados.
        if (options.RawArguments is not null)
        {
            return "No Linux, os argumentos vão separados, não numa linha só.";
        }

        return null;
    }

    /// <summary>Absoluto no Linux: começa na raiz. O <c>Path</c> do .NET responderia pelo sistema em que roda.</summary>
    private static bool IsAbsolute(string path) => path.StartsWith('/');

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, name))
            .FirstOrDefault(File.Exists);

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
