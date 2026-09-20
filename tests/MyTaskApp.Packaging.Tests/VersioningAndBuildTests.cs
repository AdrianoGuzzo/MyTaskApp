using System.Text.RegularExpressions;

namespace MyTaskApp.Packaging.Tests;

/// <summary>
/// "Evite manter versões duplicadas manualmente em vários arquivos": aqui isso
/// vira build quebrada em vez de recomendação. Subir de versão tem de ser uma
/// edição só.
/// </summary>
public class VersioningAndBuildTests
{
    private static readonly string BuildProperties =
        RepositoryFiles.Read(RepositoryFiles.BuildProperties);

    private static readonly string WindowsBuild =
        RepositoryFiles.Read(RepositoryFiles.WindowsBuildScript);

    private static readonly string LinuxBuild =
        RepositoryFiles.Read(RepositoryFiles.LinuxBuildScript);

    [Fact]
    public void TheVersionLivesInExactlyOneFile()
    {
        Regex.Matches(BuildProperties, @"<VersionPrefix>").Should().ContainSingle();
    }

    [Theory]
    [InlineData("Product")]
    [InlineData("Company")]
    [InlineData("Copyright")]
    public void TheProductIdentityIsDeclaredOnce_AndFlowsIntoEveryAssembly(string property)
    {
        BuildProperties.Should().Contain($"<{property}>");
    }

    [Theory]
    [InlineData("installer/windows/build.ps1")]
    [InlineData("installer/linux/build.sh")]
    public void EveryPackagingScriptAsksMsBuildForTheVersion(string script)
    {
        var content = RepositoryFiles.Read(RepositoryFiles.At(script.Split('/')));

        content.Should().Contain("-getProperty:Version");
    }

    [Fact]
    public void NoPackagingScriptCarriesAVersionOfItsOwn()
    {
        foreach (var script in new[] { WindowsBuild, LinuxBuild })
        {
            // Um "1.0.0" literal num script de build é a duplicata que o
            // briefing manda evitar.
            Regex.IsMatch(script, @"=\s*['""]?\d+\.\d+\.\d+['""]?\s*$", RegexOptions.Multiline)
                .Should().BeFalse();
        }
    }

    [Fact]
    public void TheWindowsBuildHandsTheVersionToTheInstaller()
    {
        WindowsBuild.Should().Contain("/DAppVersion=$version");
        WindowsBuild.Should().Contain("/DAppVersionFull=$versionFull");
    }

    [Fact]
    public void TheAppIconIsWiredIntoTheExecutable()
    {
        // Sem ApplicationIcon o .exe sai sem ícone, e o atalho do Menu Iniciar
        // vira um retângulo branco.
        var project = RepositoryFiles.Read(RepositoryFiles.DesktopProject);

        project.Should().Contain("<ApplicationIcon>");
        File.Exists(RepositoryFiles.At("src", "MyTaskApp.Desktop", "Assets", "app.ico"))
            .Should().BeTrue();
    }

    [Fact]
    public void ThePublishedAppStillShipsItsConfiguration()
    {
        // appsettings.json é carregado com optional:false. Se o publish parar
        // de copiá-lo, o app morre na primeira linha do Main — na máquina do
        // usuário, depois de instalado.
        var project = RepositoryFiles.Read(RepositoryFiles.DesktopProject);

        project.Should().Contain("appsettings.json");
        project.Should().Contain("<CopyToOutputDirectory>PreserveNewest</CopyToOutputDirectory>");
    }

    [Theory]
    [InlineData("--self-contained true")]
    [InlineData("PublishTrimmed=false")]
    public void BothPlatformsPublishTheSameWay(string expected)
    {
        // Self-contained: o usuário não precisa instalar runtime nenhum.
        // Sem trimming: EF Core e Avalonia dependem de reflexão.
        WindowsBuild.Should().Contain(expected);
        LinuxBuild.Should().Contain(expected);
    }

    [Fact]
    public void NoBuildScriptTurnsOnInvariantGlobalization()
    {
        // ADR-002: os ids IANA de fuso precisam do ICU. Ligar isso para
        // economizar alguns megabytes quebraria "America/Sao_Paulo".
        WindowsBuild.Should().NotContain("InvariantGlobalization=true");
        LinuxBuild.Should().NotContain("InvariantGlobalization=true");
        RepositoryFiles.Read(RepositoryFiles.BuildProperties)
            .Should().Contain("<InvariantGlobalization>false</InvariantGlobalization>");
    }

    [Fact]
    public void TheBuildRefusesToShipWithoutTheFileTheAppNeedsToStart()
    {
        WindowsBuild.Should().Contain("appsettings.json");
        LinuxBuild.Should().Contain("appsettings.json");
    }

    [Fact]
    public void TheLinuxDesktopEntryIsComplete()
    {
        var entry = RepositoryFiles.Read(RepositoryFiles.LinuxDesktopEntry);

        foreach (var key in new[] { "Type=Application", "Name=MyTaskApp", "Exec=", "Icon=", "Categories=" })
        {
            entry.Should().Contain(key);
        }

        // O Exec é substituído na instalação pelo caminho real do binário.
        entry.Should().Contain("Exec=@EXEC@");
        entry.Should().Contain("@VERSION@");
    }
}
