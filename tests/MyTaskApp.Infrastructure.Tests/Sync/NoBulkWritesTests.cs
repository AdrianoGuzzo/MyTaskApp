using System.Text.RegularExpressions;

namespace MyTaskApp.Infrastructure.Tests.Sync;

/// <summary>
/// A captura da sincronização enxerga o que passa pelo change tracker
/// (ADR-060). <c>ExecuteDelete</c>, <c>ExecuteUpdate</c> e SQL solto gravam por
/// fora dele, e uma alteração gravada assim nunca chegaria aos outros
/// computadores. Quem precisar de um deles decide aqui, por escrito.
/// </summary>
public sealed partial class NoBulkWritesTests
{
    /// <summary>
    /// Os arquivos que podem: a aplicação do que veio do servidor, que não deve
    /// gerar operação, e o próprio servidor, que não tem captura.
    /// </summary>
    private static readonly string[] Allowed = [Path.Combine("Sync", "Apply"), "MyTaskApp.Server."];

    [Fact]
    public void TheProductCode_DoesNotWriteAroundTheChangeTracker()
    {
        var src = Path.Combine(RepositoryRoot(), "src");

        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !Allowed.Any(folder => file.Contains(folder, StringComparison.Ordinal)))
            .Where(file => BulkWrite().IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(src, file))
            .ToList();

        offenders.Should().BeEmpty("gravar por fora do change tracker não chega à sincronização");
    }

    [GeneratedRegex(@"\b(ExecuteDelete|ExecuteUpdate|ExecuteSql|ExecuteSqlRaw|ExecuteSqlInterpolated)(Async)?\s*\(")]
    private static partial Regex BulkWrite();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MyTaskApp.slnx")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("MyTaskApp.slnx não encontrado.");
    }
}
