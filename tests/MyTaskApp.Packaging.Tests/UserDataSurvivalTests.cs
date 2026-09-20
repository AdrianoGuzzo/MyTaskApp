using System.Text.RegularExpressions;

namespace MyTaskApp.Packaging.Tests;

/// <summary>
/// A promessa mais cara do instalador: atualizar ou desinstalar o MyTaskApp
/// nunca apaga as tarefas de ninguém. É a única falha desta entrega que o
/// usuário não teria como desfazer, então ela é cercada por todos os lados —
/// no Windows e no Linux.
/// </summary>
public class UserDataSurvivalTests
{
    private static readonly string WindowsScript =
        RepositoryFiles.Read(RepositoryFiles.WindowsInstallerScript);

    private static readonly string LinuxUninstall =
        RepositoryFiles.Read(RepositoryFiles.LinuxUninstallScript);

    private static readonly string LinuxInstall =
        RepositoryFiles.Read(RepositoryFiles.LinuxInstallScript);

    /// <summary>O trecho entre <c>[UninstallDelete]</c> e a próxima seção.</summary>
    private static string UninstallDeleteSection()
    {
        var match = Regex.Match(
            WindowsScript,
            @"^\[UninstallDelete\]\s*$(?<body>.*?)(^\[|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline);

        return match.Success ? match.Groups["body"].Value : string.Empty;
    }

    [Fact]
    public void TheUninstallerDeletesNothingByListing()
    {
        // Uma linha em [UninstallDelete] apontando para {userappdata} apagaria
        // o banco em silêncio, sem passar por nenhuma confirmação.
        var entries = RepositoryFiles
            .MeaningfulLines(UninstallDeleteSection(), ";")
            .ToArray();

        entries.Should().BeEmpty();
    }

    [Fact]
    public void ThereIsExactlyOnePlaceInTheWholeScriptThatCanDeleteUserData()
    {
        // Uma só chamada, com um nome que diz o que ela faz. Qualquer segunda
        // ocorrência é um caminho novo para perder dados.
        Regex.Matches(WindowsScript, @"\bDelTree\s*\(").Count.Should().Be(1);
    }

    [Fact]
    public void ThatPlaceIsReachedOnlyAfterAnExplicitDecision()
    {
        Regex.Matches(WindowsScript, @"\bRemoveUserData\s*\(\s*\)\s*;").Should().ContainSingle();

        // A chamada é guardada por ShouldRemoveUserData, que pergunta.
        WindowsScript.Should().Contain("if ShouldRemoveUserData() then");
        WindowsScript.Should().Contain("RemoveUserData()");
    }

    [Fact]
    public void ThePromptDefaultsToKeepingTheData()
    {
        // MB_DEFBUTTON2 põe o foco no "Não": um Enter distraído preserva.
        WindowsScript.Should().Contain("MB_YESNO or MB_DEFBUTTON2");
    }

    [Fact]
    public void ASilentUninstallNeverGuesses()
    {
        // Sem o parâmetro explícito, desinstalação automatizada preserva.
        WindowsScript.Should().Contain("{param:DELETEDATA|0}");
    }

    [Fact]
    public void TheInstallerNeverWritesInsideTheUserDataFolder()
    {
        // [Files] só pode copiar para {app}. Se algum arquivo do produto fosse
        // parar em {userappdata}, a desinstalação passaria a ter motivo para
        // mexer lá — e é assim que dados somem.
        var files = Regex.Match(
            WindowsScript,
            @"^\[Files\]\s*$(?<body>.*?)(^\[|\z)",
            RegexOptions.Multiline | RegexOptions.Singleline);

        files.Success.Should().BeTrue();

        foreach (var entry in RepositoryFiles.MeaningfulLines(files.Groups["body"].Value, ";"))
        {
            entry.Should().Contain("DestDir: \"{app}\"");
        }
    }

    [Fact]
    public void TheDatabaseIsNeverNamedByTheInstaller()
    {
        // O instalador não conhece o banco: não o copia, não o lê, não o apaga
        // e não roda migration nenhuma (ADR-005). Citar o arquivo seria o
        // primeiro passo para tratá-lo como artefato da aplicação.
        WindowsScript.Should().NotContain("mytaskapp.db");
        WindowsScript.Should().NotContain(".db");
        WindowsScript.ToLowerInvariant().Should().NotContain("sqlite");
    }

    [Fact]
    public void LinuxUninstallKeepsTheDataUnlessAskedTwice()
    {
        // --purge pede, e ainda confirma; sem ele, a pasta de dados nem é tocada.
        LinuxUninstall.Should().Contain("--purge");
        LinuxUninstall.Should().Contain("Apagar? [s/N]");

        var removals = Regex.Matches(LinuxUninstall, @"rm -rf ""\$data_dir""");
        removals.Should().ContainSingle();
    }

    [Fact]
    public void LinuxInstallNeverWritesToTheDataFolder()
    {
        // data_dir aparece no install.sh só para a mensagem final.
        var writes = RepositoryFiles
            .MeaningfulLines(LinuxInstall, "#")
            .Where(line => line.Contains("$data_dir", StringComparison.Ordinal))
            .Where(line =>
                line.StartsWith("cp ", StringComparison.Ordinal)
                || line.StartsWith("rm ", StringComparison.Ordinal)
                || line.StartsWith("mkdir ", StringComparison.Ordinal)
                || line.StartsWith("ln ", StringComparison.Ordinal));

        writes.Should().BeEmpty();
    }

    [Fact]
    public void BothPlatformsInstallOnlyUnderTheUsersOwnFolders()
    {
        // Nada de sudo, nada de /usr: instalação por usuário também é uma
        // escolha de segurança, não só de conveniência.
        LinuxInstall.Should().Contain("$HOME/.local");

        // Só o que executa. O comentário no topo do install.sh cita "sudo" e
        // "/usr" justamente para dizer que não os usa.
        var commands = RepositoryFiles.MeaningfulLines(LinuxInstall, "#").ToArray();

        commands.Should().NotContain(line => line.Contains("sudo", StringComparison.Ordinal));
        commands.Should().NotContain(line => line.Contains("/usr/", StringComparison.Ordinal));
    }
}
