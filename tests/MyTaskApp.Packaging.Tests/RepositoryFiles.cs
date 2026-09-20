namespace MyTaskApp.Packaging.Tests;

/// <summary>
/// Acha os arquivos de packaging a partir do binário de teste. O caminho sobe
/// até o <c>.slnx</c> em vez de contar pastas: assim o teste sobrevive a uma
/// mudança de TFM ou de configuração, que mudariam a profundidade de <c>bin</c>.
/// </summary>
internal static class RepositoryFiles
{
    private const string SolutionFile = "MyTaskApp.slnx";

    public static string Root { get; } = FindRoot();

    public static string WindowsInstallerScript => At("installer", "windows", "MyTaskApp.iss");

    public static string WindowsBuildScript => At("installer", "windows", "build.ps1");

    public static string LinuxBuildScript => At("installer", "linux", "build.sh");

    public static string LinuxInstallScript => At("installer", "linux", "payload", "install.sh");

    public static string LinuxUninstallScript => At("installer", "linux", "payload", "uninstall.sh");

    public static string LinuxDesktopEntry => At("installer", "linux", "mytaskapp.desktop.in");

    public static string BuildProperties => At("Directory.Build.props");

    public static string DesktopProject =>
        At("src", "MyTaskApp.Desktop", "MyTaskApp.Desktop.csproj");

    public static string At(params string[] segments) =>
        Path.Combine([Root, .. segments]);

    public static string Read(string path) => File.ReadAllText(path);

    /// <summary>
    /// As linhas úteis de um script: sem vazias e sem comentário. Um teste que
    /// procura texto proibido não pode ser satisfeito por um comentário que
    /// diz justamente para não fazer aquilo.
    /// </summary>
    public static IEnumerable<string> MeaningfulLines(string content, string commentPrefix)
    {
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();

            if (line.Length > 0 && !line.StartsWith(commentPrefix, StringComparison.Ordinal))
            {
                yield return line;
            }
        }
    }

    private static string FindRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, SolutionFile)))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            $"Não encontrei {SolutionFile} subindo a partir de {AppContext.BaseDirectory}.");
    }
}
