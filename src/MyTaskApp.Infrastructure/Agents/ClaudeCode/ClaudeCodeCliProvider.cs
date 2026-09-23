using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Agents;
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
    ILogger<ClaudeCodeCliProvider> logger) : IAgentCliProvider
{
    public const string ProviderId = "claude-code";

    private static readonly TimeSpan VersionTimeout = TimeSpan.FromSeconds(10);

    public string Id => ProviderId;

    public string Name => "Claude Code";

    public string Command => "claude";

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
    /// Só o executável, sem argumento: o Claude abre a sessão interativa na
    /// pasta em que nasceu — o worktree.
    /// </summary>
    public TerminalLaunchOptions CreateLaunch(AgentCliStartContext context, CliDetectionResult detection)
    {
        var executable = detection.ExecutablePath
            ?? throw new InvalidOperationException("O Claude Code não foi encontrado.");

        return new TerminalLaunchOptions(executable, [], context.WorkingDirectory);
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
