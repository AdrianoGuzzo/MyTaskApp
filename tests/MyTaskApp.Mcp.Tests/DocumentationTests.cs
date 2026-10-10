using MyTaskApp.Mcp.Hosting;

namespace MyTaskApp.Mcp.Tests;

/// <summary>
/// O guia (docs/mcp-server.md) acompanha o catálogo: uma ferramenta nova sem
/// documentação, ou a contagem desatualizada, quebra aqui.
/// </summary>
public class DocumentationTests
{
    private static readonly Lazy<string> Guide = new(() =>
        File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "mcp-server.md")));

    [Fact]
    public void EveryTool_IsInTheGuide()
    {
        McpCatalog.Tools
            .Select(tool => tool.Name)
            .Where(name => !Guide.Value.Contains($"`{name}`", StringComparison.Ordinal) && !CoveredBySlash(name))
            .Should().BeEmpty();
    }

    [Fact]
    public void TheGuide_CountsTheToolsRight() =>
        Guide.Value.Should().Contain($"São {McpCatalog.Tools.Count} ferramentas.");

    /// <summary>"`anonymization_profile_skipped_table_add` / `_remove`" documenta as duas.</summary>
    private static bool CoveredBySlash(string name)
    {
        var cut = name.LastIndexOf('_');
        return cut > 0 && Guide.Value.Contains($"/ `{name[cut..]}`", StringComparison.Ordinal);
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MyTaskApp.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Raiz do repositório não encontrada.");
    }
}
