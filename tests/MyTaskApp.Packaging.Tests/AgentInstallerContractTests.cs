using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Packaging.Tests;

/// <summary>
/// O agente sem tela (ADR-085, T-16) vai junto com o app: publicado na mesma
/// pasta, fechado pelo instalador como o app, e tirado do logon ao desinstalar.
/// Quem o põe no logon é o próprio app, ao "Aceitar ser controlada".
/// </summary>
public class AgentInstallerContractTests
{
    private static readonly string Iss = RepositoryFiles.Read(RepositoryFiles.WindowsInstallerScript);

    private static readonly string[] IssLines = [.. RepositoryFiles.MeaningfulLines(Iss, ";")];

    private static bool IssHas(string text) =>
        IssLines.Any(line => line.Contains(text, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void BothBuildScripts_PublishTheAgentFirst_IntoTheSameFolder()
    {
        foreach (var script in new[] { RepositoryFiles.WindowsBuildScript, RepositoryFiles.LinuxBuildScript })
        {
            var text = RepositoryFiles.Read(script);
            var agent = text.IndexOf("MyTaskApp.Agent.csproj", StringComparison.Ordinal);
            var agentPublish = text.IndexOf("dotnet publish", agent, StringComparison.Ordinal);
            var desktopPublish = text.IndexOf("dotnet publish", agentPublish + 1, StringComparison.Ordinal);

            agent.Should().BePositive($"{script} publica o agente");
            desktopPublish.Should().BeGreaterThan(agentPublish, "o desktop por cima: o que os dois trazem fica na versão dele");
            text.Should().Contain("MyTaskApp.Agent", "e confere que o agente saiu do publish");
        }
    }

    /// <summary>
    /// A janela nativa do terminal do agente (ADR-098) vai na mesma pasta,
    /// depois do agente e antes do desktop, e o build confere que saiu.
    /// </summary>
    [Fact]
    public void BothBuildScripts_PublishTheTerminalWindow_BeforeTheDesktop()
    {
        foreach (var (script, agentPublish, attachPublish, desktopPublish, produced) in new[]
        {
            (RepositoryFiles.WindowsBuildScript, "dotnet publish $agentProject", "dotnet publish $terminalAttachProject", "dotnet publish $desktopProject", "'MyTaskApp.TerminalAttach.exe'"),
            (RepositoryFiles.LinuxBuildScript, "dotnet publish \"$agent_project\"", "dotnet publish \"$terminal_attach_project\"", "dotnet publish \"$desktop_project\"", "$publish_dir/MyTaskApp.TerminalAttach"),
        })
        {
            var text = RepositoryFiles.Read(script);
            var agent = text.IndexOf(agentPublish, StringComparison.Ordinal);
            var attach = text.IndexOf(attachPublish, StringComparison.Ordinal);
            var desktop = text.IndexOf(desktopPublish, StringComparison.Ordinal);

            agent.Should().BePositive($"{script} publica o agente");
            text.Should().Contain("MyTaskApp.TerminalAttach.csproj", $"{script} publica a janela do terminal");
            attach.Should().BeGreaterThan(agent);
            desktop.Should().BeGreaterThan(attach, "o desktop por cima, como com o agente");
            text.Should().Contain(produced, "e confere que a janela saiu do publish");
        }

        RepositoryFiles.Read(RepositoryFiles.LinuxInstallScript).Should().Contain("\"$install_dir/MyTaskApp.TerminalAttach\"", "o install.sh deixa a janela executável");
        IssHas("/IM {#TerminalAttachExeName}").Should().BeTrue("uma janela aberta não segura a atualização");
    }

    [Fact]
    public void TheInstaller_KnowsTheAgentByTheNamesTheCodeUses()
    {
        IssHas($"#define AgentMutexName    \"{AgentProcess.MutexName}\"").Should().BeTrue();
        IssHas($"#define AgentRunValueName \"{AgentProcess.RunValueName}\"").Should().BeTrue();
        IssHas($"#define AgentExeName      \"{Path.GetFileNameWithoutExtension(AgentProcess.ExecutableName)}.exe\"").Should().BeTrue();
    }

    [Fact]
    public void ARunningAgent_IsClosedLikeTheApp_BeforeAnyFileChanges()
    {
        IssHas("CheckForMutexes('{#AppMutexName},{#AgentMutexName}')").Should().BeTrue();
        IssHas("/IM {#AgentExeName}").Should().BeTrue();
    }

    [Fact]
    public void AnUpdate_BringsTheAgentBack_OnlyIfTheAppHadTurnedItOn()
    {
        IssHas("Filename: \"{app}\\{#AgentExeName}\"; Flags: nowait runasoriginaluser; Check: AgentStartupIsEnabled").Should().BeTrue();
        IssHas("RegQueryStringValue(HKEY_CURRENT_USER, '{#AppRunKey}', '{#AgentRunValueName}', Command)").Should().BeTrue();

        // Quem liga é o app: o instalador nunca grava o agente no Run.
        IssLines.Should().NotContain(line => line.StartsWith("Root: HKCU", StringComparison.Ordinal)
            && line.Contains("AgentRunValueName", StringComparison.Ordinal));
    }

    [Fact]
    public void Uninstalling_TakesTheAgentOutOfTheLogon_OnBothSystems()
    {
        IssHas("RegDeleteValue(HKEY_CURRENT_USER, '{#AppRunKey}', '{#AgentRunValueName}')").Should().BeTrue();

        var uninstall = RepositoryFiles.Read(RepositoryFiles.LinuxUninstallScript);
        uninstall.Should().Contain($"systemctl --user disable --now {AgentProcess.SystemdUnitName}");
        uninstall.IndexOf(AgentProcess.SystemdUnitName, StringComparison.Ordinal)
            .Should().BeLessThan(uninstall.IndexOf("rm -rf \"$install_dir\"", StringComparison.Ordinal), "o agente para antes de os binários sumirem");
    }

    [Fact]
    public void TheLinuxInstall_MakesTheAgentRunnable_AndRestartsItOnlyIfItWasOn()
    {
        var install = RepositoryFiles.Read(RepositoryFiles.LinuxInstallScript);

        install.Should().Contain("chmod +x \"$install_dir/MyTaskApp\" \"$install_dir/MyTaskApp.Agent\"");
        install.Should().Contain($"systemctl --user try-restart {AgentProcess.SystemdUnitName}");
        install.Should().NotContain("systemctl --user enable", "quem liga é o app");
    }
}
