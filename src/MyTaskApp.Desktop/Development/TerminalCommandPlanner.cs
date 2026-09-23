namespace MyTaskApp.Desktop.Development;

/// <summary>Um jeito de abrir um terminal numa pasta.</summary>
/// <param name="WorkingDirectory">Quando o terminal não aceita a pasta por argumento, ele nasce nela.</param>
public sealed record TerminalLaunch(string FileName, IReadOnlyList<string> Arguments, string? WorkingDirectory = null);

/// <summary>
/// Os terminais a tentar, em ordem, para abrir já dentro do worktree (ADR-027).
/// O primeiro que existir ganha. Puro: quem inicia o processo é o
/// <see cref="Composition.ShellLauncher"/>.
/// </summary>
public static class TerminalCommandPlanner
{
    public enum Platform
    {
        Windows,
        Linux,
        MacOs,
    }

    public static Platform Current =>
        OperatingSystem.IsWindows() ? Platform.Windows
        : OperatingSystem.IsMacOS() ? Platform.MacOs
        : Platform.Linux;

    public static IReadOnlyList<TerminalLaunch> Candidates(Platform platform, string path) => platform switch
    {
        // O Windows Terminal quando existe; o PowerShell sempre existe.
        Platform.Windows =>
        [
            new("wt.exe", ["-d", path]),
            new("powershell.exe", ["-NoExit"], path),
        ],

        Platform.MacOs => [new("open", ["-a", "Terminal", path])],

        // Não há um terminal padrão no Linux: tenta os das áreas de trabalho mais
        // comuns, e depois o apelido do Debian e o xterm.
        _ =>
        [
            new("gnome-terminal", [$"--working-directory={path}"]),
            new("konsole", ["--workdir", path]),
            new("xfce4-terminal", [$"--working-directory={path}"]),
            new("x-terminal-emulator", [], path),
            new("xterm", [], path),
        ],
    };
}
