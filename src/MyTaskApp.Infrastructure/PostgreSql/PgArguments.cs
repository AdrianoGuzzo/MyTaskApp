using System.Globalization;
using System.Text.RegularExpressions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Infrastructure.PostgreSql;

/// <summary>
/// Os argumentos de cada ferramenta, um a um (ADR-056). <b>Nenhum leva senha</b>:
/// ela vai por <c>PGPASSWORD</c>, no ambiente do processo. E todos levam
/// <c>--no-password</c>, para uma autenticação recusada falhar na hora em vez
/// de esperar alguém digitar.
/// </summary>
internal static partial class PgArguments
{
    public const string MaintenanceDatabase = "postgres";

    public static IReadOnlyList<string> Version() => ["--version"];

    /// <summary>
    /// Formato diretório: o único em que o <c>pg_dump</c> trabalha em paralelo
    /// (<c>--jobs</c>), e o <c>pg_restore</c> lê em paralelo também. Com
    /// <see cref="PgDumpRequest.Snapshot"/>, lê a foto exportada pela cópia
    /// mascarada (ADR-058): a estrutura do mesmo instante dos dados.
    /// </summary>
    public static IReadOnlyList<string> Dump(PgDumpRequest request, int lockWaitTimeoutSeconds)
    {
        var arguments = new List<string>(Connection(request.Connection))
        {
            "--dbname", DatabaseOf(request.Connection),
            "--format=directory",
            "--file", request.OutputDirectory,
            "--jobs", Math.Max(1, request.Jobs).ToString(CultureInfo.InvariantCulture),
            "--verbose",
            "--no-password",
            $"--lock-wait-timeout={lockWaitTimeoutSeconds.ToString(CultureInfo.InvariantCulture)}s",
            "--no-subscriptions",
            "--no-publications",
        };

        arguments.AddRange(Sections(request.IncludeSchema, request.IncludeData));

        if (request.Snapshot is { } snapshot)
        {
            // Vem do servidor (pg_export_snapshot), mas vai como argumento: só o formato conhecido.
            if (!SnapshotPattern().IsMatch(snapshot))
            {
                throw new DomainException("Snapshot do PostgreSQL inválido.");
            }

            arguments.Add($"--snapshot={snapshot}");
        }

        return arguments;
    }

    /// <summary>
    /// Sem dono nem permissões de produção (os roles podem nem existir no
    /// destino), sem <c>SECURITY LABEL</c>, e parando no primeiro erro.
    /// <c>--single-transaction</c> não combina com <c>--jobs</c>.
    /// </summary>
    public static IReadOnlyList<string> Restore(PgRestoreRequest request)
    {
        var arguments = new List<string>(Connection(request.Target))
        {
            "--dbname", DatabaseOf(request.Target),
            "--no-owner",
            "--no-privileges",
            "--no-security-labels",
            "--exit-on-error",
            "--jobs", Math.Max(1, request.Jobs).ToString(CultureInfo.InvariantCulture),
            "--verbose",
            "--no-password",
        };

        arguments.AddRange(request.Section switch
        {
            RestoreSection.PreData => ["--section=pre-data"],
            RestoreSection.PostData => ["--section=post-data"],
            _ => Sections(request.IncludeSchema, request.IncludeData),
        });
        arguments.Add(request.ArchiveDirectory);
        return arguments;
    }

    public static IReadOnlyList<string> ListArchive(string archiveDirectory) => ["--list", archiveDirectory];

    /// <summary>A partir do <c>template0</c>, em UTF-8: um banco limpo, sem o que alguém tenha posto no template1.</summary>
    public static IReadOnlyList<string> CreateDatabase(DatabaseConnectionSnapshot target) =>
    [
        .. Connection(target),
        $"--maintenance-db={MaintenanceDatabase}",
        "--template=template0",
        "--encoding=UTF8",
        "--no-password",
        DatabaseOf(target),
    ];

    /// <summary><c>--force</c> derruba as conexões abertas no destino (PostgreSQL 13+).</summary>
    public static IReadOnlyList<string> DropDatabase(DatabaseConnectionSnapshot target, bool force)
    {
        var arguments = new List<string>(Connection(target))
        {
            $"--maintenance-db={MaintenanceDatabase}",
            "--if-exists",
            "--no-password",
        };

        if (force)
        {
            arguments.Add("--force");
        }

        arguments.Add(DatabaseOf(target));
        return arguments;
    }

    /// <summary>
    /// O banco em que a ferramenta age. Uma conexão só de servidor (ADR-057)
    /// chega aqui já resolvida pelo plano; sem banco, nada roda — o libpq
    /// cairia no banco com o nome do usuário, longe da política.
    /// </summary>
    internal static string DatabaseOf(DatabaseConnectionSnapshot connection) =>
        connection.Database ?? throw new DatabaseSecurityException(new SecurityDecision(
            [new SecurityViolation(SecurityViolationCode.DatabaseNotChosen, $"{connection.Name} é só o servidor: nenhum banco foi escolhido.")]));

    private static IEnumerable<string> Connection(DatabaseConnectionSnapshot connection) =>
    [
        "--host", connection.Host,
        "--port", connection.Port.ToString(CultureInfo.InvariantCulture),
        "--username", connection.Username,
    ];

    private static IEnumerable<string> Sections(bool includeSchema, bool includeData) =>
        (includeSchema, includeData) switch
        {
            (true, false) => ["--schema-only"],
            (false, true) => ["--data-only"],
            _ => [],
        };

    // 00000003-0000001B-1
    [GeneratedRegex("^[0-9A-Fa-f]{1,16}-[0-9A-Fa-f]{1,16}(-[0-9]{1,10})?$", RegexOptions.CultureInvariant)]
    private static partial Regex SnapshotPattern();
}
