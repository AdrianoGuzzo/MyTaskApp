using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.GitHub;

/// <summary>
/// Onde está o GitHub CLI (<c>gh</c>), como caminho absoluto (ADR-047).
/// </summary>
/// <remarks>
/// A procura é a do <see cref="ExecutableLocator"/>, que relê o PATH a cada
/// vez: quem instala o <c>gh</c> com o app aberto e clica em "Verificar
/// novamente" já é atendido. Aqui ficam só o nome e as pastas dos instaladores.
/// </remarks>
internal sealed class GhLocator(
    Func<string, EnvironmentVariableTarget, string?> environment,
    Func<string, bool> fileExists,
    bool isWindows)
{
    private readonly ExecutableLocator _locator = new(environment, fileExists, isWindows);

    public static GhLocator ForCurrentSystem() =>
        new(Environment.GetEnvironmentVariable, File.Exists, OperatingSystem.IsWindows());

    /// <summary>O primeiro candidato que existe, ou <c>null</c>.</summary>
    public string? Locate() => _locator.Locate(FileNames(), KnownDirectories());

    public IEnumerable<string> Candidates() => _locator.Candidates(FileNames(), KnownDirectories());

    private string[] FileNames() => [isWindows ? "gh.exe" : "gh"];

    private IEnumerable<string> KnownDirectories()
    {
        if (isWindows)
        {
            // O MSI (e o winget, que usa o MSI) instala em "Program Files\GitHub CLI".
            foreach (var variable in (string[])["ProgramFiles", "ProgramW6432", "ProgramFiles(x86)"])
            {
                if (_locator.Variable(variable) is { } programFiles)
                {
                    yield return Path.Combine(programFiles, "GitHub CLI");
                }
            }

            if (_locator.Variable("LOCALAPPDATA") is { } local)
            {
                yield return Path.Combine(local, "Programs", "GitHub CLI");
            }

            yield break;
        }

        yield return "/usr/bin";
        yield return "/usr/local/bin";
        yield return "/opt/homebrew/bin";
        yield return "/home/linuxbrew/.linuxbrew/bin";
    }
}
