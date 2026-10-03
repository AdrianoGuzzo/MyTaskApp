using System.Text.Json;
using System.Text.RegularExpressions;

namespace MyTaskApp.Packaging.Tests;

/// <summary>
/// O processo de release (ADR-044) como contrato: as regras que, quebradas,
/// não dão erro nenhum na hora — só aparecem no dia em que um instalador sai
/// com a versão errada, ou substitui outro em silêncio.
/// </summary>
public class ReleaseProcessTests
{
    private static readonly string BuildProperties =
        RepositoryFiles.Read(RepositoryFiles.BuildProperties);

    private static readonly string ReleaseWorkflow =
        RepositoryFiles.Read(RepositoryFiles.ReleaseWorkflow);

    private static readonly JsonElement Config =
        JsonDocument.Parse(RepositoryFiles.Read(RepositoryFiles.ReleasePleaseConfig)).RootElement;

    private static string VersionPrefix =>
        Regex.Match(BuildProperties, @"<VersionPrefix>([^<]+)</VersionPrefix>").Groups[1].Value;

    /// <summary>
    /// O Release Please calcula a próxima versão pelo manifest e escreve o
    /// número no props. Um dos dois editado à mão faz o instalador e a release
    /// discordarem — o <c>release.yml</c> pararia, mas só depois da tag criada.
    /// </summary>
    [Fact]
    public void TheManifestAndTheBuildAgreeOnTheVersion()
    {
        var manifest = JsonDocument.Parse(RepositoryFiles.Read(RepositoryFiles.ReleasePleaseManifest));

        manifest.RootElement.GetProperty(".").GetString().Should().Be(VersionPrefix);
    }

    /// <summary>Sem a anotação, o bot sobe o manifest e esquece o assembly.</summary>
    [Fact]
    public void TheVersionLineCarriesTheReleasePleaseMarker()
    {
        BuildProperties.Should().MatchRegex(
            @"<VersionPrefix>\d+\.\d+\.\d+</VersionPrefix>\s*<!--\s*x-release-please-version\s*-->");

        Config.GetProperty("packages").GetProperty(".").GetProperty("extra-files")
            .EnumerateArray().Select(file => file.GetString())
            .Should().Contain("Directory.Build.props");
    }

    [Fact]
    public void TagsAreVMajorMinorPatch()
    {
        Config.GetProperty("include-v-in-tag").GetBoolean().Should().BeTrue();
        Config.GetProperty("include-component-in-tag").GetBoolean().Should().BeFalse();
    }

    /// <summary>
    /// Rascunho: alguém roda o teste de fumaça antes de o mundo baixar. E a
    /// tag nasce junto, senão a release seguinte não acha a anterior e o
    /// changelog vira o histórico inteiro.
    /// </summary>
    [Fact]
    public void ReleasesStartAsDraftsWithTheirTagAlreadyCreated()
    {
        Config.GetProperty("draft").GetBoolean().Should().BeTrue();
        Config.GetProperty("force-tag-creation").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("feat", "Added")]
    [InlineData("fix", "Fixed")]
    [InlineData("perf", "Performance")]
    [InlineData("refactor", "Changed")]
    public void TheChangelogUsesTheAgreedSections(string type, string section)
    {
        var entry = Config.GetProperty("changelog-sections").EnumerateArray()
            .Single(candidate => candidate.GetProperty("type").GetString() == type);

        entry.GetProperty("section").GetString().Should().Be(section);
        entry.TryGetProperty("hidden", out var hidden).Should().BeFalse(
            "uma seção oculta não aparece no changelog nem gera versão");
        hidden.ValueKind.Should().Be(JsonValueKind.Undefined);
    }

    [Fact]
    public void TheReleaseRefusesATagThatDisagreesWithTheCode()
    {
        ReleaseWorkflow.Should().Contain("-getProperty:Version");
        ReleaseWorkflow.Should().Contain("${TAG#v}");
        ReleaseWorkflow.Should().Contain("^v[0-9]+\\.[0-9]+\\.[0-9]+$");
    }

    /// <summary>
    /// Cliente A tem o 1.4.0, cliente B o 1.4.1: o arquivo que cada um baixou
    /// não pode mudar depois. Publicada é imutável, e o upload falha em vez
    /// de substituir.
    /// </summary>
    [Fact]
    public void ThePipelineNeverOverwritesAPublishedArtifact()
    {
        RepositoryFiles.MeaningfulLines(ReleaseWorkflow, "#")
            .Should().NotContain(line => line.Contains("--clobber"));

        ReleaseWorkflow.Should().Contain("isDraft");
        ReleaseWorkflow.Should().Contain("gh release upload");
    }

    [Fact]
    public void EveryReleaseShipsChecksums()
    {
        ReleaseWorkflow.Should().Contain("sha256sum -- * > SHA256SUMS");
        ReleaseWorkflow.Should().Contain("sha256sum -c SHA256SUMS");
    }

    /// <summary>O build sai do commit da tag, e de nenhum outro.</summary>
    [Fact]
    public void EveryBuildJobChecksOutTheTag()
    {
        var checkouts = Regex.Matches(ReleaseWorkflow, @"uses: actions/checkout@v\d+\s+with:\s+ref: (.+)");

        checkouts.Should().HaveCount(3);
        checkouts.Select(match => match.Groups[1].Value.Trim())
            .Should().AllBe("refs/tags/${{ inputs.tag }}");
    }

    /// <summary>
    /// Tag criada pelo Release Please com o GITHUB_TOKEN não dispara workflow
    /// de push de tag: o build só acontece porque o release-please.yml chama o
    /// release.yml direto.
    /// </summary>
    [Fact]
    public void TheReleaseBuildRunsRightAfterTheTagIsCreated()
    {
        var releasePlease = RepositoryFiles.Read(RepositoryFiles.ReleasePleaseWorkflow);

        releasePlease.Should().Contain("googleapis/release-please-action@v4");
        releasePlease.Should().Contain("uses: ./.github/workflows/release.yml");
        releasePlease.Should().Contain("release_created == 'true'");
        ReleaseWorkflow.Should().Contain("workflow_call:");
    }

    [Theory]
    [InlineData("MyTaskApp-$VERSION-win-x64-setup.exe")]
    [InlineData("MyTaskApp-$VERSION-linux-x64.tar.gz")]
    public void ArtifactNamesFollowProductVersionRuntime(string expected)
    {
        ReleaseWorkflow.Should().Contain(expected);
    }

    [Fact]
    public void TheInstallerFileNameIsBuiltFromVersionAndRuntime()
    {
        RepositoryFiles.Read(RepositoryFiles.WindowsInstallerScript)
            .Should().Contain("OutputBaseFilename=MyTaskApp-{#AppVersion}-{#AppRuntime}-setup");

        RepositoryFiles.Read(RepositoryFiles.WindowsBuildScript)
            .Should().Contain("\"MyTaskApp-$version-$Runtime-setup.exe\"");

        RepositoryFiles.Read(RepositoryFiles.LinuxBuildScript)
            .Should().Contain("MyTaskApp-$version-$runtime.tar.gz");
    }

    /// <summary>
    /// Merge é squash: o título do PR vira o commit que o Release Please lê.
    /// Sem o portão, um título fora do padrão some do changelog calado.
    /// </summary>
    [Fact]
    public void PullRequestTitlesAreCheckedAgainstConventionalCommits()
    {
        var workflow = RepositoryFiles.Read(RepositoryFiles.PullRequestTitleWorkflow);

        workflow.Should().Contain("amannn/action-semantic-pull-request");

        foreach (var type in new[] { "feat", "fix", "perf", "refactor" })
        {
            workflow.Should().MatchRegex($@"(?m)^\s+{type}\s*$");
        }
    }
}
