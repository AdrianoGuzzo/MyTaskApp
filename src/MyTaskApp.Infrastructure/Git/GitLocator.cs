using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Git;

/// <summary>
/// Onde está o <c>git</c>, como caminho absoluto (ADR-027).
/// </summary>
/// <remarks>
/// A procura em si — PATH relido do registro a cada vez — é do
/// <see cref="ExecutableLocator"/>; aqui ficam só o nome e as pastas em que os
/// instaladores do Git costumam pôr o executável.
/// </remarks>
internal sealed class GitLocator(
    Func<string, EnvironmentVariableTarget, string?> environment,
    Func<string, bool> fileExists,
    bool isWindows)
{
    private readonly ExecutableLocator _locator = new(environment, fileExists, isWindows);

    public static GitLocator ForCurrentSystem() =>
        new(Environment.GetEnvironmentVariable, File.Exists, OperatingSystem.IsWindows());

    /// <summary>O primeiro candidato que existe, ou <c>null</c>.</summary>
    public string? Locate() => _locator.Locate(FileNames(), KnownDirectories());

    public IEnumerable<string> Candidates() => _locator.Candidates(FileNames(), KnownDirectories());

    private string[] FileNames() => [isWindows ? "git.exe" : "git"];

    private IEnumerable<string> KnownDirectories()
    {
        if (isWindows)
        {
            foreach (var variable in (string[])["ProgramFiles", "ProgramW6432", "ProgramFiles(x86)"])
            {
                if (_locator.Variable(variable) is { } programFiles)
                {
                    yield return Path.Combine(programFiles, "Git", "cmd");
                }
            }

            // Instalação por usuário (winget --scope user).
            if (_locator.Variable("LOCALAPPDATA") is { } local)
            {
                yield return Path.Combine(local, "Programs", "Git", "cmd");
            }

            yield break;
        }

        yield return "/usr/bin";
        yield return "/usr/local/bin";
        yield return "/opt/homebrew/bin";
    }
}
