namespace MyTaskApp.Infrastructure.Processes;

/// <summary>
/// Acha um executável como caminho absoluto: PATH primeiro, depois pastas de
/// instalação conhecidas (ADR-027, ADR-030).
/// </summary>
/// <remarks>
/// <para>
/// Não basta executar "git" ou "claude" e deixar o sistema achar: o PATH de um
/// processo é copiado quando ele nasce. Quem instala a ferramenta com o app
/// aberto e clica em "Verificar novamente" continuaria ouvindo "não encontrado"
/// até reiniciar o app. Por isso, no Windows, o PATH é relido do registro
/// (máquina + usuário) a cada procura.
/// </para>
/// <para>
/// O ambiente e o disco chegam por parâmetro para os testes montarem qualquer
/// máquina sem tocar na de verdade.
/// </para>
/// </remarks>
internal sealed class ExecutableLocator(
    Func<string, EnvironmentVariableTarget, string?> environment,
    Func<string, bool> fileExists,
    bool isWindows)
{
    public bool IsWindows => isWindows;

    public static ExecutableLocator ForCurrentSystem() =>
        new(Environment.GetEnvironmentVariable, File.Exists, OperatingSystem.IsWindows());

    /// <summary>O primeiro candidato que existe, ou <c>null</c>.</summary>
    public string? Locate(IReadOnlyList<string> fileNames, IEnumerable<string> knownDirectories) =>
        Candidates(fileNames, knownDirectories).FirstOrDefault(fileExists);

    /// <summary>
    /// Cada pasta, na ordem do PATH, com cada nome — como o próprio shell
    /// procura: uma pasta anterior ganha de uma posterior, qualquer que seja a
    /// extensão.
    /// </summary>
    public IEnumerable<string> Candidates(IReadOnlyList<string> fileNames, IEnumerable<string> knownDirectories)
    {
        var seen = new HashSet<string>(isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var directory in PathDirectories().Concat(knownDirectories))
        {
            foreach (var fileName in fileNames)
            {
                string candidate;

                try
                {
                    candidate = Path.Combine(directory, fileName);
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
    }

    /// <summary>Uma variável do processo, vazia virando <c>null</c>.</summary>
    public string? Variable(string name) =>
        environment(name, EnvironmentVariableTarget.Process) is { Length: > 0 } value ? value : null;

    private IEnumerable<string> PathDirectories()
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
    }
}
