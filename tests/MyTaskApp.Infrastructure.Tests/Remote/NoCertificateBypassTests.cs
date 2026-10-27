using System.Text.RegularExpressions;

namespace MyTaskApp.Infrastructure.Tests.Remote;

/// <summary>
/// RS-02 do modelo de ameaças da orquestração remota: o certificado do
/// servidor é validado pelo sistema, e nenhum código do produto troca essa
/// validação. Um "aceitar qualquer certificado" deixado para depurar entregaria
/// o token e os pedidos assinados a quem estivesse no meio do caminho.
/// </summary>
public sealed partial class NoCertificateBypassTests
{
    [Fact]
    public void TheProductCode_NeverReplacesTheCertificateValidation()
    {
        var src = Path.Combine(RepositoryRoot(), "src");

        var offenders = Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories)
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                && !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .Where(file => CertificateBypass().IsMatch(File.ReadAllText(file)))
            .Select(file => Path.GetRelativePath(src, file))
            .ToList();

        offenders.Should().BeEmpty("a validação do certificado é a do sistema, sem opção de ignorar");
    }

    [GeneratedRegex(@"\b(ServerCertificateCustomValidationCallback|ServerCertificateValidationCallback|RemoteCertificateValidationCallback|DangerousAcceptAnyServerCertificateValidator|CheckCertificateRevocationList\s*=\s*false)\b")]
    private static partial Regex CertificateBypass();

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
