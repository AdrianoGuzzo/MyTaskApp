using System.Text.RegularExpressions;

namespace MyTaskApp.Packaging.Tests;

/// <summary>
/// A segurança do CI como contrato (SEC-016): os portões que, apagados num
/// PR qualquer, não deixam nenhum teste vermelho, e o primeiro sinal seria um
/// pacote vulnerável ou uma action trocada já em produção.
/// </summary>
public class CiSecurityTests
{
    /// <summary>
    /// Uma tag é móvel: quem controla o repositório da action troca o código
    /// que roda aqui, com acesso aos secrets da release. Um SHA não muda. O
    /// comentário com a versão é o que o Dependabot atualiza junto.
    /// </summary>
    [Fact]
    public void EveryActionIsPinnedToAFullCommitSha()
    {
        var unpinned = RepositoryFiles.Workflows
            .SelectMany(path => RepositoryFiles.MeaningfulLines(RepositoryFiles.Read(path), "#")
                .Select(line => Regex.Match(line, @"^(?:-\s+)?uses:\s+(\S+)(.*)$"))
                .Where(match => match.Success && !match.Groups[1].Value.StartsWith("./", StringComparison.Ordinal))
                .Where(match =>
                    !Regex.IsMatch(match.Groups[1].Value, "@[0-9a-f]{40}$")
                    || !Regex.IsMatch(match.Groups[2].Value, @"^\s+# v\d+\.\d+\.\d+\s*$"))
                .Select(match => $"{Path.GetFileName(path)}: {match.Value}"));

        unpinned.Should().BeEmpty();
    }

    [Fact]
    public void TheCiFailsOnAVulnerablePackage()
    {
        var workflow = RepositoryFiles.Read(RepositoryFiles.CiWorkflow);
        var script = RepositoryFiles.Read(RepositoryFiles.VulnerablePackagesScript);

        workflow.Should().Contain("./scripts/check-vulnerable-packages.ps1");
        script.Should().Contain("package --vulnerable --include-transitive --no-restore --format json");
        script.Should().Contain("'MyTaskApp.slnx', 'MyTaskApp.Server.slnx'");
        script.Should().Contain("exit 1");
    }

    [Fact]
    public void CodeQlAnalyzesTheCSharpOfBothSolutions()
    {
        var workflow = RepositoryFiles.Read(RepositoryFiles.CodeQlWorkflow);

        workflow.Should().Contain("github/codeql-action/init@");
        workflow.Should().Contain("github/codeql-action/analyze@");
        workflow.Should().MatchRegex(@"language: csharp\s+build-mode: manual");
        workflow.Should().Contain("dotnet build MyTaskApp.slnx");
        workflow.Should().Contain("dotnet build MyTaskApp.Server.slnx");
    }

    [Theory]
    [InlineData("nuget")]
    [InlineData("github-actions")]
    public void DependabotWatchesTheEcosystem(string ecosystem)
    {
        var config = RepositoryFiles.Read(RepositoryFiles.DependabotConfig);

        config.Should().MatchRegex($@"(?m)^\s+- package-ecosystem: {ecosystem}\s*$");
        config.Should().Contain("update-types: [patch]");
    }
}
