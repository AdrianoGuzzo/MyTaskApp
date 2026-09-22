using MyTaskApp.Desktop.Composition;

namespace MyTaskApp.Packaging.Tests;

/// <summary>
/// O instalador não é testável por unidade — mas as promessas que ele faz são.
/// Cada teste aqui trava uma decisão do ADR-018 que, se alguém desfizer sem
/// perceber, só apareceria na máquina de um usuário: apagando o banco dele,
/// pedindo UAC à toa ou criando uma segunda instalação ao lado da primeira.
/// </summary>
public class WindowsInstallerContractTests
{
    private static readonly string Script =
        RepositoryFiles.Read(RepositoryFiles.WindowsInstallerScript);

    /// <summary>Sem comentários: a diretiva precisa estar valendo, não citada.</summary>
    private static readonly string[] Directives =
        [.. RepositoryFiles.MeaningfulLines(Script, ";")];

    private static bool HasDirective(string text) =>
        Directives.Any(line => line.Contains(text, StringComparison.OrdinalIgnoreCase));

    [Fact]
    public void TheProductIdentityIsFixed_SoANewVersionUpgradesInsteadOfInstallingBeside()
    {
        HasDirective("AppId={{").Should().BeTrue();
    }

    [Fact]
    public void InstallingDoesNotAskForAdministrator()
    {
        // Instalação por usuário é o padrão; elevar continua possível, mas só
        // quando alguém pede (§ segurança do briefing).
        HasDirective("PrivilegesRequired=lowest").Should().BeTrue();
        HasDirective("PrivilegesRequiredOverridesAllowed=").Should().BeTrue();
    }

    [Fact]
    public void TheInstallFolderIsChosenByWindows_NotHardcoded()
    {
        HasDirective("DefaultDirName={autopf}").Should().BeTrue();

        // Um caminho absoluto aqui quebraria instalação por usuário e em
        // máquinas com o Windows em outro volume.
        Directives.Should().NotContain(line => line.Contains(@"C:\", StringComparison.Ordinal));
    }

    [Fact]
    public void TheInstallerDetectsARunningApp_UsingTheMutexTheAppActuallyCreates()
    {
        // O acordo entre código e instalador. Renomear a constante sem editar o
        // .iss faria o upgrade trocar binários com o app aberto.
        HasDirective($"#define AppMutexName   \"{SingleInstance.MutexName}\"").Should().BeTrue();
        HasDirective("AppMutex={#AppMutexName}").Should().BeTrue();
    }

    [Fact]
    public void TwoSetupsCannotRunAtOnce()
    {
        HasDirective("SetupMutex=").Should().BeTrue();
    }

    [Fact]
    public void TheInstallerAlwaysKeepsItsOwnLog()
    {
        // "Não instalou e não sei por quê" é exatamente a situação em que
        // ninguém lembrou de passar /LOG antes.
        HasDirective("SetupLogging=yes").Should().BeTrue();
    }

    [Fact]
    public void InstallerLogsDoNotLandInTheApplicationLogFolder()
    {
        // Logs da aplicação vivem em {userappdata} (Roaming); os do instalador
        // em {localappdata}. Subárvores diferentes, de propósito.
        Script.Should().Contain(@"{localappdata}\MyTaskApp\installer\logs");
        Script.Should().NotContain(@"{userappdata}\MyTaskApp\logs");
    }

    [Fact]
    public void TheVersionIsInjected_NeverTypedIntoThisFile()
    {
        HasDirective("AppVersion={#AppVersion}").Should().BeTrue();

        // Uma versão literal aqui viraria a segunda fonte da verdade que o
        // briefing proíbe.
        Directives.Should().NotContain(line =>
            System.Text.RegularExpressions.Regex.IsMatch(line, @"^AppVersion=\d"));
    }

    [Fact]
    public void CompilingWithoutAVersionFails_InsteadOfShippingADefault()
    {
        Script.Should().Contain("#error");
    }

    [Fact]
    public void TheProductLooksLikeAProduct_InAddRemovePrograms()
    {
        HasDirective("AppPublisher=").Should().BeTrue();
        HasDirective("VersionInfoVersion=").Should().BeTrue();
        HasDirective("UninstallDisplayIcon=").Should().BeTrue();
        HasDirective("SetupIconFile=").Should().BeTrue();
    }

    [Fact]
    public void TheWizardIsShort()
    {
        // Bem-vindo, grupo do Menu Iniciar e "pronto para instalar" não
        // acrescentam nada: sobram Diretório, Opções, Instalando e Concluído.
        HasDirective("DisableWelcomePage=yes").Should().BeTrue();
        HasDirective("DisableProgramGroupPage=yes").Should().BeTrue();
        HasDirective("DisableReadyPage=yes").Should().BeTrue();
        HasDirective("WizardStyle=modern").Should().BeTrue();
    }

    [Theory]
    [InlineData("assets\\MyTaskApp.ico")]
    [InlineData("assets\\wizard-large.bmp")]
    [InlineData("assets\\wizard-small.bmp")]
    public void EveryArtworkTheScriptReferences_ExistsOnDisk(string relativePath)
    {
        // O ISCC só reclamaria na hora de compilar a release.
        File.Exists(RepositoryFiles.At("installer", "windows", relativePath))
            .Should().BeTrue();
    }

    [Fact]
    public void TheDesktopShortcutIsOptional()
    {
        HasDirective("Name: \"desktopicon\"").Should().BeTrue();
        HasDirective("Flags: unchecked").Should().BeTrue();
    }

    [Fact]
    public void TheAppCanBeLaunchedAfterInstalling_ButNeverDuringASilentOne()
    {
        // Abrir uma janela no meio de uma implantação automatizada é o oposto
        // do que "silencioso" promete.
        HasDirective("skipifsilent").Should().BeTrue();
    }

    [Fact]
    public void StartingWithWindowsIsOffered_AndTheBoxComesChecked()
    {
        // Ao contrário do atalho da área de trabalho, e de propósito: um app de
        // lembretes só lembra se estiver vivo às 15:00 (ADR-016, ADR-023).
        var task = Directives.Should()
            .ContainSingle(line => line.Contains("Name: \"startupicon\"", StringComparison.Ordinal))
            .Subject;

        task.Contains("unchecked", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public void StartingWithWindowsIsRegisteredForThisUser_NeverForTheWholeMachine()
    {
        // O caminho é contrato com o app, que lê e escreve a mesma entrada.
        HasDirective($"#define AppRunKey      \"{WindowsStartupRegistration.RunKey}\"")
            .Should().BeTrue();

        // HKA viraria HKLM numa instalação /ALLUSERS, e aí o app — que roda sem
        // elevação — nunca conseguiria desligar a própria opção pelo menu.
        var runEntries = Directives
            .Where(line => line.Contains("{#AppRunKey}", StringComparison.Ordinal)
                && line.StartsWith("Root:", StringComparison.Ordinal))
            .ToArray();

        // Uma para ligar, uma para apagar quando a caixa vem desmarcada.
        runEntries.Should().HaveCount(2);
        runEntries.Should().OnlyContain(line => line.StartsWith("Root: HKCU;", StringComparison.Ordinal));
    }

    [Fact]
    public void TheStartupCommandUsesTheSameFlagTheAppParses()
    {
        // Gêmeo do acordo do AppMutexName: renomear a constante sem editar o
        // .iss faria o app subir no login com a janela na cara do usuário.
        HasDirective($"#define StartupFlag    \"{LaunchOptions.StartupFlag}\"").Should().BeTrue();
        HasDirective("{#StartupFlag}").Should().BeTrue();
    }

    [Fact]
    public void TheStartupEntryUsesTheSameValueNameTheAppReadsAndWrites()
    {
        // O app e o instalador precisam mexer na mesma entrada — duas com nomes
        // diferentes seriam dois inícios automáticos.
        WindowsStartupRegistration.ValueName.Should().Be("MyTaskApp");
        HasDirective($"ValueName: \"{{#AppName}}\"").Should().BeTrue();
    }

    [Fact]
    public void UncheckingStartupRemovesTheValue_InsteadOfLeavingItBehind()
    {
        // Sem esta linha, desmarcar a caixa numa atualização não faria nada e o
        // app continuaria subindo no login.
        Directives.Should().ContainSingle(line =>
            line.Contains("deletevalue", StringComparison.Ordinal)
            && line.Contains("Tasks: not startupicon", StringComparison.Ordinal));
    }

    [Fact]
    public void UpgradingAsksTheRegistry_InsteadOfReimposingThePreviousChoice()
    {
        // UsePreviousTasks sozinho desfaria, em silêncio, um "desliga isso"
        // feito pelo menu do app depois da instalação.
        HasDirective("procedure PreselectStartup();").Should().BeTrue();
        HasDirective("StartupIsEnabled()").Should().BeTrue();

        // E na página, não antes dela: o UsePreviousTasks age até a página
        // aparecer, então escrever em InitializeWizard seria escrever para ser
        // sobrescrito.
        HasDirective("if CurPageID = wpSelectTasks then").Should().BeTrue();
    }

    [Fact]
    public void UninstallingAlwaysClearsTheRunValue_EvenWhenTheAppWroteIt()
    {
        // O valor pode não ter vindo do instalador — e aí não há registro de
        // desinstalação para o uninsdeletevalue apagar.
        HasDirective($"RegDeleteValue(HKEY_CURRENT_USER, '{{#AppRunKey}}', '{{#AppName}}')")
            .Should().BeTrue();
    }

    [Fact]
    public void LaunchingRightAfterInstalling_ShowsTheWindow()
    {
        // Quem acabou de clicar em Instalar quer ver o app, não procurá-lo na
        // bandeja. O flag é para o login do Windows, não para este momento.
        var run = Directives.Should()
            .ContainSingle(line => line.Contains("{cm:LaunchApp}", StringComparison.Ordinal))
            .Subject;

        run.Contains("{#StartupFlag}", StringComparison.Ordinal).Should().BeFalse();
    }
}
