using Microsoft.Extensions.Logging;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.PostgreSql;

/// <summary>
/// Acha <c>psql</c>, <c>pg_dump</c>, <c>pg_restore</c>, <c>pg_isready</c>,
/// <c>createdb</c> e <c>dropdb</c> — PATH e pastas de instalação conhecidas —
/// e pergunta a versão de cada um (ADR-056).
/// </summary>
/// <remarks>
/// <para>
/// Não supõe servidor local: as ferramentas cliente bastam para um banco
/// remoto, e é isso que o instalador "Command Line Tools" do PostgreSQL ou o
/// pacote <c>postgresql-client</c> instalam.
/// </para>
/// <para>
/// <b>Um conjunto por vez.</b> Com o PostgreSQL 14 no PATH e o 17 instalado ao
/// lado, misturar <c>pg_dump</c> 17 com <c>pg_restore</c> 14 dá um arquivo
/// que o restore não lê. Por isso cada pasta com <c>pg_dump</c> vira um
/// conjunto, e as outras ferramentas vêm dela quando existem lá. O diagnóstico
/// mostra o mais novo; a cópia usa o menor que lê a origem
/// (<see cref="PostgresClientTools.ForSource"/>).
/// </para>
/// </remarks>
internal sealed class PostgresToolLocator(
    Func<string, EnvironmentVariableTarget, string?> environment,
    Func<string, bool> fileExists,
    Func<string, IEnumerable<string>> subdirectories,
    bool isWindows,
    IProcessRunner runner,
    DatabaseOperationsOptions options,
    ILogger<PostgresToolLocator> logger) : IPostgresToolLocator
{
    private readonly ExecutableLocator _locator = new(environment, fileExists, isWindows);

    private readonly SemaphoreSlim _gate = new(1, 1);

    private PostgresClientTools? _cached;

    public static PostgresToolLocator ForCurrentSystem(
        IProcessRunner runner,
        DatabaseOperationsOptions options,
        ILogger<PostgresToolLocator> logger) =>
        new(
            Environment.GetEnvironmentVariable,
            File.Exists,
            directory => Directory.Exists(directory) ? Directory.EnumerateDirectories(directory) : [],
            OperatingSystem.IsWindows(),
            runner,
            options,
            logger);

    public async Task<PostgresClientTools> DetectAsync(bool refresh, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (!refresh && _cached is not null)
            {
                return _cached;
            }

            _cached = await DetectNowAsync(cancellationToken);
            logger.LogInformation(
                "PostgresToolsDetected {Found} {Missing}",
                _cached.Tools.Count(tool => tool.Found),
                string.Join(",", _cached.Missing));
            return _cached;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<PostgresClientTools> DetectNowAsync(CancellationToken cancellationToken)
    {
        var known = KnownDirectories().ToList();
        var versions = new Dictionary<string, (PostgresVersion? Version, string? Text)>(isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        // Cada pasta com um pg_dump de versão legível é um conjunto: as outras ferramentas vêm dela.
        var folders = new List<(string Directory, PostgresVersion Version)>();
        var comparer = isWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

        foreach (var dump in _locator.LocateAll(FileNames(PostgresTool.PgDump), known))
        {
            var version = await VersionOfAsync(dump, versions, cancellationToken);

            if (version.Version is { } found
                && Path.GetDirectoryName(dump) is { } directory
                && !folders.Any(folder => comparer.Equals(folder.Directory, directory)))
            {
                folders.Add((directory, found));
            }
        }

        var sets = new List<IReadOnlyList<PostgresToolStatus>>();

        // OrderByDescending é estável: entre versões iguais, fica a primeira achada.
        foreach (var (directory, _) in folders.OrderByDescending(folder => folder.Version))
        {
            sets.Add(await SetFromAsync(directory, known, versions, cancellationToken));
        }

        var newest = sets.Count > 0 ? sets[0] : await SetFromAsync(null, known, versions, cancellationToken);
        return new PostgresClientTools(newest, PostgresInstallGuides.For(isWindows, environment)) { Sets = sets };
    }

    private async Task<IReadOnlyList<PostgresToolStatus>> SetFromAsync(
        string? preferred,
        List<string> known,
        Dictionary<string, (PostgresVersion? Version, string? Text)> versions,
        CancellationToken cancellationToken)
    {
        var tools = new List<PostgresToolStatus>();

        foreach (var tool in Enum.GetValues<PostgresTool>())
        {
            var names = FileNames(tool);
            var path = preferred is null ? null : names.Select(name => Path.Combine(preferred, name)).FirstOrDefault(fileExists);
            path ??= _locator.Locate(names, known);

            if (path is null)
            {
                tools.Add(new PostgresToolStatus(tool, PostgresToolNames.Of(tool), null, null, null));
                continue;
            }

            var version = await VersionOfAsync(path, versions, cancellationToken);
            tools.Add(new PostgresToolStatus(tool, PostgresToolNames.Of(tool), path, version.Version, version.Text));
        }

        return tools;
    }

    private async Task<(PostgresVersion? Version, string? Text)> VersionOfAsync(
        string path,
        Dictionary<string, (PostgresVersion?, string?)> versions,
        CancellationToken cancellationToken)
    {
        if (versions.TryGetValue(path, out var known))
        {
            return known;
        }

        (PostgresVersion?, string?) answer;

        try
        {
            var result = await runner.RunAsync(new ProcessRequest(path, ["--version"], options.VersionTimeout), cancellationToken);
            var text = result.StandardOutput.Trim();
            answer = result.ExitCode == 0 ? (PostgresVersion.TryParse(text), text) : (null, null);
        }
        catch (ProcessStartException)
        {
            // Existe mas não roda (arquitetura errada, sem permissão): versão desconhecida.
            answer = (null, null);
        }

        versions[path] = answer;
        return answer;
    }

    private string[] FileNames(PostgresTool tool) =>
        [isWindows ? PostgresToolNames.Of(tool) + ".exe" : PostgresToolNames.Of(tool)];

    /// <summary>As pastas dos instaladores, das versões maiores para as menores.</summary>
    private IEnumerable<string> KnownDirectories()
    {
        if (isWindows)
        {
            foreach (var variable in (string[])["ProgramFiles", "ProgramW6432", "ProgramFiles(x86)"])
            {
                if (_locator.Variable(variable) is not { } programFiles)
                {
                    continue;
                }

                // O instalador da EDB: "PostgreSQL\17\bin", inclusive só com "Command Line Tools".
                foreach (var version in ByVersion(subdirectories(Path.Combine(programFiles, "PostgreSQL"))))
                {
                    yield return Path.Combine(version, "bin");
                }

                // O pgAdmin traz as ferramentas cliente junto.
                yield return Path.Combine(programFiles, "pgAdmin 4", "runtime");
            }

            if (_locator.Variable("LOCALAPPDATA") is { } local)
            {
                yield return Path.Combine(local, "Programs", "pgAdmin 4", "runtime");
            }

            yield break;
        }

        // Debian/Ubuntu: /usr/lib/postgresql/17/bin; o /usr/bin é o pg_wrapper.
        foreach (var version in ByVersion(subdirectories("/usr/lib/postgresql")))
        {
            yield return Path.Combine(version, "bin");
        }

        // RHEL/Fedora (PGDG): /usr/pgsql-17/bin.
        foreach (var version in ByVersion(subdirectories("/usr").Where(directory =>
                     Path.GetFileName(directory).StartsWith("pgsql-", StringComparison.Ordinal))))
        {
            yield return Path.Combine(version, "bin");
        }

        yield return "/usr/local/pgsql/bin";
        yield return "/usr/bin";
        yield return "/usr/local/bin";
        yield return "/opt/homebrew/opt/libpq/bin";
        yield return "/opt/homebrew/bin";
        yield return "/Applications/Postgres.app/Contents/Versions/latest/bin";
    }

    /// <summary>"17", "pgsql-16", "9.6": a maior primeiro, pelo número que o nome trouxer.</summary>
    private static IEnumerable<string> ByVersion(IEnumerable<string> directories) =>
        directories.OrderByDescending(directory => PostgresVersion.TryParse(Path.GetFileName(directory)) ?? new PostgresVersion(0, 0));
}

/// <summary>
/// Como instalar as ferramentas cliente, por sistema (ADR-056). Só
/// instruções: o app nunca instala nada sozinho (ADR-027).
/// </summary>
internal static class PostgresInstallGuides
{
    public static PostgresInstallGuide For(bool isWindows, Func<string, EnvironmentVariableTarget, string?> environment)
    {
        if (isWindows)
        {
            return new PostgresInstallGuide(
                "Windows",
                [
                    "Instale as ferramentas cliente do PostgreSQL 17 ou mais novo: winget install PostgreSQL.PostgreSQL.17",
                    "Ou baixe o instalador da EDB (postgresql.org/download/windows) e marque só \"Command Line Tools\".",
                    "Se instalou em outra pasta, acrescente a pasta bin ao PATH (ex.: C:\\Program Files\\PostgreSQL\\17\\bin).",
                    "Depois clique em \"Verificar novamente\" — não precisa reiniciar o app.",
                ],
                ["where.exe psql", "where.exe pg_dump", "where.exe pg_restore", "where.exe pg_isready", "psql --version", "pg_dump --version", "pg_restore --version", "pg_isready --version"]);
        }

        var redHat = File.Exists("/etc/redhat-release") || environment("ID_LIKE", EnvironmentVariableTarget.Process)?.Contains("rhel", StringComparison.Ordinal) == true;

        return new PostgresInstallGuide(
            "Linux",
            redHat
                ?
                [
                    "Adicione o repositório PGDG: sudo dnf install -y https://download.postgresql.org/pub/repos/yum/reporpms/EL-9-x86_64/pgdg-redhat-repo-latest.noarch.rpm",
                    "Instale o cliente: sudo dnf install -y postgresql17",
                    "As ferramentas ficam em /usr/pgsql-17/bin; o app procura lá sozinho.",
                ]
                :
                [
                    "Adicione o repositório PGDG: sudo apt install -y postgresql-common && sudo /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh",
                    "Instale o cliente: sudo apt install -y postgresql-client-17",
                    "As ferramentas ficam em /usr/lib/postgresql/17/bin; o app procura lá sozinho.",
                ],
            ["which psql", "which pg_dump", "which pg_restore", "which pg_isready", "psql --version", "pg_dump --version", "pg_restore --version", "pg_isready --version"]);
    }
}
