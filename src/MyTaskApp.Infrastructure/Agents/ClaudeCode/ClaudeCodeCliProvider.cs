using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Agents;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Agents.ClaudeCode;

/// <summary>
/// O Claude Code CLI (<c>claude</c>) como agente de desenvolvimento (ADR-030).
/// É o único lugar do app que sabe o nome do executável, onde ele costuma
/// morar e como pedir a versão.
/// </summary>
/// <remarks>
/// <para>
/// No Windows há duas instalações comuns: o instalador nativo, que deixa um
/// <c>claude.exe</c> em <c>%USERPROFILE%\.local\bin</c>, e o npm, que deixa um
/// <c>claude.cmd</c> em <c>%APPDATA%\npm</c>. As duas pastas entram como
/// conhecidas, porque o PATH de quem acabou de instalar pode ainda não tê-las.
/// </para>
/// <para>
/// A versão vem de <c>claude --version</c>, que responde e sai — nunca abre
/// sessão interativa. Se ela não vier, o Claude continua "instalado": achar o
/// executável é o que importa para abrir; a versão é informação.
/// </para>
/// </remarks>
internal sealed partial class ClaudeCodeCliProvider(
    ExecutableLocator locator,
    IProcessRunner runner,
    ClaudeCodeHooks hooks,
    ILogger<ClaudeCodeCliProvider> logger) : IAgentCliProvider
{
    public const string ProviderId = "claude-code";

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);

    public string Id => ProviderId;

    public string Name => "Claude Code";

    public string Command => "claude";

    /// <summary>
    /// O worktree é um lugar descartável e isolado, feito para o agente
    /// trabalhar sem parar a cada arquivo — daí abrir sem pedir permissões.
    /// O usuário troca no card do agente.
    /// </summary>
    public string DefaultArguments => "--dangerously-skip-permissions";

    public async Task<CliDetectionResult> DetectAsync(CancellationToken cancellationToken = default)
    {
        var executable = Locate();

        if (executable is null)
        {
            return CliDetectionResult.NotInstalled("Claude Code não encontrado.");
        }

        return new CliDetectionResult
        {
            IsInstalled = true,
            ExecutablePath = executable,
            Version = await ReadVersionAsync(executable, cancellationToken),
        };
    }

    public AgentCliInstallGuide? InstallGuideFor(OSPlatform platform) =>
        platform == OSPlatform.Windows ? WindowsInstallationGuide.Guide
        : platform == OSPlatform.Linux ? LinuxInstallationGuide.Guide
        : null;

    /// <summary>
    /// O Claude abre a sessão interativa na pasta em que nasceu — o worktree.
    /// Primeiro os parâmetros escolhidos no card. Com texto, ele vai depois, como
    /// a primeira mensagem: <c>claude [parâmetros] "texto"</c> executa direto;
    /// <c>claude [parâmetros] --permission-mode plan "texto"</c> só monta o plano
    /// e espera aprovação antes de alterar arquivos.
    /// </summary>
    /// <remarks>
    /// O texto é <b>um</b> argumento, nunca uma linha de shell. A exceção é o
    /// <c>claude.cmd</c> do npm: o Windows o roda pelo <c>cmd.exe</c>, que corta
    /// o texto na quebra de linha e interpreta <c>%</c> e <c>"</c>. Ali as quebras
    /// viram espaço, e o que o <c>cmd.exe</c> estragaria é recusado com o motivo.
    /// <para>
    /// Com acompanhamento (ADR-037), <c>--settings &lt;arquivo de hooks&gt;</c> vem
    /// antes de tudo, e o ambiente leva a sessão e o segredo. Antes dos
    /// parâmetros do usuário de propósito: se ele passar o próprio
    /// <c>--settings</c>, o dele vale — perde-se o acompanhamento, e não a
    /// configuração dele.
    /// </para>
    /// </remarks>
    public TerminalLaunchOptions CreateLaunch(AgentCliStartContext context, CliDetectionResult detection)
    {
        var executable = detection.ExecutablePath
            ?? throw new InvalidOperationException("O Claude Code não foi encontrado.");

        IReadOnlyList<string> monitoring = context.Monitoring is { } settings
            ? ["--settings", hooks.EnsureSettingsFile(settings.Endpoint)]
            : [];

        if (string.IsNullOrWhiteSpace(context.Prompt))
        {
            return new TerminalLaunchOptions(
                executable,
                [.. monitoring, .. context.Arguments],
                context.WorkingDirectory,
                context.Monitoring?.Environment);
        }

        var prompt = RunsThroughCmd(executable) ? ForCmd(context.Prompt) : context.Prompt;

        string[] arguments = context.RunDirectly
            ? [.. monitoring, .. context.Arguments, prompt]
            : [.. monitoring, .. context.Arguments, "--permission-mode", "plan", prompt];

        return new TerminalLaunchOptions(executable, arguments, context.WorkingDirectory, context.Monitoring?.Environment);
    }

    public string? MonitoringUnavailableReason(string workingDirectory, Uri endpoint) =>
        hooks.UnavailableReason(workingDirectory, endpoint);

    private static bool RunsThroughCmd(string executable) =>
        Path.GetExtension(executable).ToUpperInvariant() is ".CMD" or ".BAT";

    private static string ForCmd(string prompt)
    {
        if (prompt.Contains('"', StringComparison.Ordinal) || prompt.Contains('%', StringComparison.Ordinal))
        {
            throw new DomainException(
                "O Claude Code instalado pelo npm (claude.cmd) não recebe texto com aspas (\") ou %. "
                + "Tire esses caracteres do texto ou use o instalador nativo do Claude Code.");
        }

        return string.Join(' ', prompt.Split(["\r\n", "\n", "\r"], StringSplitOptions.None));
    }

    /// <summary>O caminho absoluto do <c>claude</c>, ou <c>null</c>.</summary>
    public string? Locate() => locator.Locate(FileNames(), KnownDirectories());

    public IEnumerable<string> Candidates() => locator.Candidates(FileNames(), KnownDirectories());

    /// <summary>A primeira versão <c>x.y.z</c> da saída de <c>claude --version</c>.</summary>
    public static string? ParseVersion(string output) =>
        VersionPattern().Match(output) is { Success: true } match ? match.Value : null;

    private string[] FileNames() =>
        locator.IsWindows ? ["claude.exe", "claude.cmd"] : ["claude"];

    private IEnumerable<string> KnownDirectories()
    {
        var home = locator.IsWindows ? locator.Variable("USERPROFILE") : locator.Variable("HOME");

        if (home is not null)
        {
            yield return Path.Combine(home, ".local", "bin");
        }

        if (locator.IsWindows)
        {
            if (locator.Variable("APPDATA") is { } roaming)
            {
                yield return Path.Combine(roaming, "npm");
            }

            yield break;
        }

        yield return "/usr/local/bin";
        yield return "/opt/homebrew/bin";
    }

    private async Task<string?> ReadVersionAsync(string executable, CancellationToken cancellationToken)
    {
        try
        {
            var result = await runner.RunAsync(
                new ProcessRequest(executable, ["--version"], VersionTimeout),
                cancellationToken);

            if (result is { TimedOut: false, ExitCode: 0 } && ParseVersion(result.StandardOutput) is { } version)
            {
                return version;
            }

            logger.LogWarning(
                "ClaudeCodeVersionUnavailable {ExitCode} {TimedOut}",
                result.ExitCode,
                result.TimedOut);
        }
        catch (ProcessStartException exception)
        {
            logger.LogWarning(exception, "ClaudeCodeVersionUnavailable");
        }

        return null;
    }

    [GeneratedRegex(@"\d+\.\d+\.\d+[0-9A-Za-z.\-+]*", RegexOptions.CultureInvariant)]
    private static partial Regex VersionPattern();
}
