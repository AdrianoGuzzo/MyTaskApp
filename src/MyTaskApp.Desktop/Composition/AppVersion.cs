using System.Reflection;

namespace MyTaskApp.Desktop.Composition;

/// <summary>
/// Qual código está instalado: versão, commit e data da build (ADR-044).
/// </summary>
/// <remarks>
/// Nada aqui é digitado à mão. A versão é a do <c>VersionPrefix</c> que o
/// Release Please sobe; o SDK cola o SHA do commit no
/// <c>InformationalVersion</c> (<c>1.1.0+a82f91c…</c>); a data vem do
/// <c>AssemblyMetadata("BuildDate")</c> do csproj. Com o SHA, "qual versão o
/// cliente tem" vira "qual commit" sem consultar ninguém.
/// </remarks>
public sealed record AppVersion(string Version, string? FullCommit, string? BuildDate)
{
    private const int ShortCommitLength = 7;

    public static AppVersion Current { get; } = FromAssembly(typeof(AppVersion).Assembly);

    /// <summary>O SHA curto, o mesmo que o GitHub mostra ao lado de cada commit.</summary>
    public string? Commit =>
        FullCommit is { Length: > ShortCommitLength } sha ? sha[..ShortCommitLength] : FullCommit;

    /// <summary><c>1.1.0 · a82f91c · 2026-10-02</c> — o que cabe numa linha de menu.</summary>
    public string Display =>
        string.Join(" · ", new[] { Version, Commit, BuildDate }.Where(part => part is not null));

    /// <summary>
    /// O que vai para a área de transferência: o suficiente para um relato de
    /// problema apontar o commit exato, com o SHA inteiro.
    /// </summary>
    public string Details =>
        $"""
        Version: {Version}
        Commit: {FullCommit ?? "desconhecido"}
        Build: {BuildDate ?? "desconhecida"}
        """;

    public static AppVersion FromAssembly(Assembly assembly)
    {
        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        var buildDate = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == "BuildDate")?.Value;

        return Parse(informational, buildDate);
    }

    /// <summary>
    /// <c>1.1.0+sha</c> → versão e commit. Sem <c>+</c> (build fora de um
    /// repositório Git), sobra só a versão.
    /// </summary>
    public static AppVersion Parse(string? informationalVersion, string? buildDate)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return new AppVersion("desconhecida", null, Blank(buildDate));
        }

        var plus = informationalVersion.IndexOf('+', StringComparison.Ordinal);

        return plus < 0
            ? new AppVersion(informationalVersion, null, Blank(buildDate))
            : new AppVersion(
                informationalVersion[..plus],
                Blank(informationalVersion[(plus + 1)..]),
                Blank(buildDate));
    }

    private static string? Blank(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
