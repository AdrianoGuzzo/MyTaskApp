namespace MyTaskApp.Infrastructure.Git;

/// <summary>
/// Onde está o <c>git</c>, como caminho absoluto (ADR-027).
/// </summary>
/// <remarks>
/// <para>
/// Não basta executar "git" e deixar o sistema achar: o PATH de um processo é
/// copiado quando ele nasce. Quem instala o Git com o app aberto e clica em
/// "Verificar novamente" continuaria ouvindo "não encontrado" até reiniciar o
/// app. Por isso, no Windows, o PATH é relido do registro (máquina + usuário) a
/// cada procura, e as pastas de instalação conhecidas entram na lista.
/// </para>
/// <para>
/// O ambiente e o disco chegam por parâmetro para os testes montarem qualquer
/// máquina sem tocar na de verdade.
/// </para>
/// </remarks>
internal sealed class GitLocator(
    Func<string, EnvironmentVariableTarget, string?> environment,
    Func<string, bool> fileExists,
    bool isWindows)
{
    public static GitLocator ForCurrentSystem() =>
        new(Environment.GetEnvironmentVariable, File.Exists, OperatingSystem.IsWindows());

    /// <summary>O primeiro candidato que existe, ou <c>null</c>.</summary>
    public string? Locate() => Candidates().FirstOrDefault(fileExists);

    public IEnumerable<string> Candidates()
    {
        var executable = isWindows ? "git.exe" : "git";
        var seen = new HashSet<string>(isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var directory in SearchDirectories())
        {
            string candidate;

            try
            {
                candidate = Path.Combine(directory, executable);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (seen.Add(candidate))
            {
                yield return candidate;
            }
        }
    }

    private IEnumerable<string> SearchDirectories()
    {
        var targets = isWindows
            ? new[] { EnvironmentVariableTarget.Machine, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Process }
            : new[] { EnvironmentVariableTarget.Process };

        var separator = isWindows ? ';' : ':';

        foreach (var target in targets)
        {
            var value = environment("PATH", target);

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            foreach (var entry in value.Split(separator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                // No registro o PATH guarda "%SystemRoot%\…" sem expandir.
                var expanded = isWindows ? Environment.ExpandEnvironmentVariables(entry.Trim('"')) : entry;

                if (Path.IsPathFullyQualified(expanded))
                {
                    yield return expanded;
                }
            }
        }

        foreach (var known in KnownDirectories())
        {
            yield return known;
        }
    }

    private IEnumerable<string> KnownDirectories()
    {
        if (isWindows)
        {
            foreach (var variable in (string[])["ProgramFiles", "ProgramW6432", "ProgramFiles(x86)"])
            {
                if (environment(variable, EnvironmentVariableTarget.Process) is { Length: > 0 } programFiles)
                {
                    yield return Path.Combine(programFiles, "Git", "cmd");
                }
            }

            // Instalação por usuário (winget --scope user).
            if (environment("LOCALAPPDATA", EnvironmentVariableTarget.Process) is { Length: > 0 } local)
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
