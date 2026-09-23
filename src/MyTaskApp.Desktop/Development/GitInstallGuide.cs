namespace MyTaskApp.Desktop.Development;

/// <summary>Um comando para o usuário copiar e rodar — o app nunca roda por ele.</summary>
public sealed record InstallCommand(string Label, string Command);

/// <summary>Como instalar o Git no sistema em que o app está rodando.</summary>
public sealed record GitInstallInstructions(
    string SystemName,
    string Explanation,
    IReadOnlyList<InstallCommand> Commands,
    Uri OfficialSite)
{
    /// <summary>O mesmo nos três sistemas: é a pergunta que "Verificar novamente" repete.</summary>
    public InstallCommand Verify { get; } = new("Conferir a instalação", "git --version");
}

/// <summary>
/// As instruções de instalação do Git, por sistema (ADR-027). O app não instala
/// nada: instalar pede privilégio e é decisão de quem administra a máquina.
/// Puro, com o <c>/etc/os-release</c> recebido como texto, para os testes
/// montarem qualquer distribuição.
/// </summary>
internal static class GitInstallGuide
{
    public static GitInstallInstructions ForCurrentSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return ForWindows();
        }

        if (OperatingSystem.IsMacOS())
        {
            return ForMacOs();
        }

        return ForLinux(ReadOsRelease());
    }

    public static GitInstallInstructions ForWindows() =>
        new(
            "Windows",
            "Abra o PowerShell ou o Prompt de Comando como usuário e execute o comando abaixo. "
            + "Depois da instalação, feche e abra novamente o terminal e confira com git --version.",
            [new InstallCommand("Instalar com o winget (recomendado)", "winget install --id Git.Git -e --source winget")],
            new Uri("https://git-scm.com/downloads/win"));

    public static GitInstallInstructions ForMacOs() =>
        new(
            "macOS",
            "No Terminal, instale as ferramentas de linha de comando da Apple, que trazem o Git, "
            + "ou use o Homebrew. Depois confira com git --version.",
            [
                new InstallCommand("Ferramentas de linha de comando", "xcode-select --install"),
                new InstallCommand("Homebrew", "brew install git"),
            ],
            new Uri("https://git-scm.com/downloads/mac"));

    /// <summary>
    /// Escolhe o gerenciador de pacotes pelo <c>ID</c> e pelo <c>ID_LIKE</c> do
    /// <c>os-release</c>: um Linux Mint diz <c>ID_LIKE="ubuntu debian"</c>. Sem
    /// reconhecer, mostra os três.
    /// </summary>
    public static GitInstallInstructions ForLinux(string? osRelease)
    {
        var (name, family) = ParseOsRelease(osRelease);

        InstallCommand apt = new("Debian / Ubuntu", "sudo apt install git");
        InstallCommand dnf = new("Fedora / RHEL", "sudo dnf install git");
        InstallCommand pacman = new("Arch Linux", "sudo pacman -S git");

        IReadOnlyList<InstallCommand> commands =
            family.Overlaps(["debian", "ubuntu"]) ? [apt]
            : family.Overlaps(["fedora", "rhel", "centos"]) ? [dnf]
            : family.Overlaps(["arch"]) ? [pacman]
            : [apt, dnf, pacman];

        return new GitInstallInstructions(
            name ?? "Linux",
            "Num terminal, instale o Git pelo gerenciador de pacotes da distribuição e confira com git --version.",
            commands,
            new Uri("https://git-scm.com/downloads/linux"));
    }

    private static (string? Name, HashSet<string> Family) ParseOsRelease(string? osRelease)
    {
        string? name = null;
        var family = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in (osRelease ?? string.Empty).Split('\n', StringSplitOptions.TrimEntries))
        {
            var equals = line.IndexOf('=', StringComparison.Ordinal);

            if (equals <= 0)
            {
                continue;
            }

            var key = line[..equals];
            var value = line[(equals + 1)..].Trim('"', '\'');

            switch (key)
            {
                case "ID" or "ID_LIKE":
                    family.UnionWith(value.Split(' ', StringSplitOptions.RemoveEmptyEntries));
                    break;
                case "PRETTY_NAME":
                    name = value;
                    break;
                case "NAME":
                    name ??= value;
                    break;
            }
        }

        return (name, family);
    }

    private static string? ReadOsRelease()
    {
        foreach (var path in (string[])["/etc/os-release", "/usr/lib/os-release"])
        {
            try
            {
                if (File.Exists(path))
                {
                    return File.ReadAllText(path);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return null;
    }
}
