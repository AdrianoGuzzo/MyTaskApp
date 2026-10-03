namespace MyTaskApp.Desktop.Development;

/// <summary>Como instalar o GitHub CLI e entrar na conta, no sistema em que o app está rodando.</summary>
public sealed record GhInstallInstructions(
    string SystemName,
    string Explanation,
    IReadOnlyList<InstallCommand> Commands,
    Uri OfficialSite)
{
    /// <summary>O mesmo nos três sistemas: abre o navegador para entrar no GitHub.</summary>
    public InstallCommand Login { get; } = new("Entrar na sua conta do GitHub", "gh auth login");

    /// <summary>A mesma pergunta que o app faz ao clicar em "Verificar novamente".</summary>
    public InstallCommand Verify { get; } = new("Conferir a instalação e o login", "gh auth status");
}

/// <summary>
/// As instruções de instalação do GitHub CLI, por sistema (ADR-047). Como no
/// Git, o app não instala nada: mostra o comando, e quem usa decide rodá-lo.
/// </summary>
internal static class GhInstallGuide
{
    public static readonly Uri OfficialSite = new("https://cli.github.com/");

    public static GhInstallInstructions ForCurrentSystem()
    {
        if (OperatingSystem.IsWindows())
        {
            return ForWindows();
        }

        if (OperatingSystem.IsMacOS())
        {
            return ForMacOs();
        }

        return ForLinux(GitInstallGuide.ReadOsRelease());
    }

    public static GhInstallInstructions ForWindows() =>
        new(
            "Windows",
            "Abra o PowerShell ou o Prompt de Comando e execute o comando abaixo. Depois, feche e abra "
            + "novamente o terminal, entre na sua conta com gh auth login e clique em \"Verificar novamente\".",
            [new InstallCommand("Instalar com o winget (recomendado)", "winget install --id GitHub.cli -e --source winget")],
            OfficialSite);

    public static GhInstallInstructions ForMacOs() =>
        new(
            "macOS",
            "No Terminal, instale pelo Homebrew, entre na sua conta com gh auth login e clique em \"Verificar novamente\".",
            [new InstallCommand("Homebrew", "brew install gh")],
            OfficialSite);

    /// <summary>
    /// Pelo <c>os-release</c>, como no Git. Distribuição antiga pode não ter o
    /// pacote: o site oficial explica o repositório do próprio GitHub.
    /// </summary>
    public static GhInstallInstructions ForLinux(string? osRelease)
    {
        var (name, family) = GitInstallGuide.ParseOsRelease(osRelease);

        InstallCommand apt = new("Debian / Ubuntu", "sudo apt install gh");
        InstallCommand dnf = new("Fedora / RHEL", "sudo dnf install gh");
        InstallCommand pacman = new("Arch Linux", "sudo pacman -S github-cli");

        IReadOnlyList<InstallCommand> commands =
            family.Overlaps(["debian", "ubuntu"]) ? [apt]
            : family.Overlaps(["fedora", "rhel", "centos"]) ? [dnf]
            : family.Overlaps(["arch"]) ? [pacman]
            : [apt, dnf, pacman];

        return new GhInstallInstructions(
            name ?? "Linux",
            "Num terminal, instale pelo gerenciador de pacotes da distribuição, entre na sua conta com "
            + "gh auth login e clique em \"Verificar novamente\". Se o pacote não existir, o site oficial "
            + "mostra como adicionar o repositório do GitHub.",
            commands,
            OfficialSite);
    }
}
