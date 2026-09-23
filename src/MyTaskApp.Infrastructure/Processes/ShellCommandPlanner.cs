namespace MyTaskApp.Infrastructure.Processes;

/// <summary>
/// Como abrir o shell. Exatamente um de <see cref="RawArguments"/> e
/// <see cref="Arguments"/> vale: o cmd precisa da linha crua, o sh recebe
/// argumentos separados.
/// </summary>
internal sealed record ShellLaunch(string FileName, string? RawArguments, IReadOnlyList<string> Arguments);

/// <summary>
/// Qual shell interpreta a linha do usuário em cada sistema (ADR-028). Puro,
/// para os dois lados serem testados em qualquer máquina.
/// </summary>
/// <remarks>
/// <para>
/// <b>Windows:</b> <c>%ComSpec% /d /s /c "linha"</c> — o mesmo que Node e libuv
/// fazem. <c>/s</c> tira só as aspas de fora e deixa o resto como o usuário
/// digitou, inclusive aspas internas, <c>&amp;&amp;</c> e pipes. <c>/d</c> ignora
/// o AutoRun do registro. É o cmd, e não o PowerShell 5.1, porque o 5.1 não
/// conhece <c>&amp;&amp;</c> — e é o que a documentação de npm e dotnet assume.
/// </para>
/// <para>
/// Antes da linha, <c>chcp 65001</c>: o console escondido do processo nasce na
/// página OEM (850 no Brasil), e o output chegaria com acento quebrado. O console
/// é só deste processo; não muda nada fora dele. O <c>&amp;</c> tem a menor
/// precedência do cmd, então o exit code é o da linha do usuário.
/// </para>
/// <para>
/// <b>Linux/macOS:</b> <c>/bin/sh -c linha</c>, com a linha como um argumento só
/// — nada é concatenado nem escapado por nós. <c>/bin/sh</c> existe em todo
/// POSIX; bash não.
/// </para>
/// </remarks>
internal static class ShellCommandPlanner
{
    public static ShellLaunch For(bool isWindows, string command, Func<string, string?> environment)
    {
        if (isWindows)
        {
            var comSpec = environment("ComSpec");
            var shell = string.IsNullOrWhiteSpace(comSpec) ? "cmd.exe" : comSpec;

            return new ShellLaunch(shell, $"/d /s /c \"chcp 65001>nul & {command}\"", []);
        }

        return new ShellLaunch("/bin/sh", null, ["-c", command]);
    }

    public static ShellLaunch ForCurrentSystem(string command) =>
        For(OperatingSystem.IsWindows(), command, Environment.GetEnvironmentVariable);
}
