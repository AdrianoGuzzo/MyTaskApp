using System.Globalization;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Infrastructure.PostgreSql;

/// <summary>
/// As consultas de leitura ao servidor (ADR-056), pelo catálogo de
/// <see cref="PostgresQueries"/>. Nenhum método devolve dado de linha.
/// </summary>
internal sealed class PostgresServerInspector(
    IPostgresSessionFactory sessions,
    ILogger<PostgresServerInspector> logger) : IPostgresServerInspector
{
    public async Task<ServerDiagnostics> TestAsync(
        DatabaseConnectionSnapshot connection,
        SecretText? password = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            // Só o servidor (ADR-057): testa pelo banco de manutenção, sem
            // listar schemas e tabelas que seriam dele, não de um banco seu.
            var serverOnly = !connection.HasDatabase;
            await using var session = await sessions.OpenAsync(Maintenance(connection), password, cancellationToken);

            var info = (await session.QueryAsync(PostgresQueries.ServerInfo, [], cancellationToken)).Single();
            var schemas = serverOnly
                ? []
                : (await session.QueryAsync(PostgresQueries.Schemas, [], cancellationToken)).Select(row => Text(row[0])).ToList();
            var tables = serverOnly ? [] : await session.QueryAsync(PostgresQueries.Tables, [], cancellationToken);
            var privileges = (await session.QueryAsync(PostgresQueries.Privileges, [], cancellationToken)).FirstOrDefault();
            var versionText = Text(info[0]);

            return new ServerDiagnostics(
                true,
                null,
                versionText,
                PostgresVersion.TryParse(versionText),
                Text(info[1]),
                Text(info[2]),
                Number(info[3]),
                schemas,
                tables.Count,
                privileges is null
                    ? ServerPrivileges.None
                    : new ServerPrivileges(Flag(privileges[0]), Flag(privileges[1]), Flag(privileges[3]), (int)Number(privileges[4]), (int)Number(privileges[2])));
        }
        catch (DomainException exception)
        {
            logger.LogInformation("PostgresTestFailed {ConnectionId}", connection.Id);
            return ServerDiagnostics.Failed(exception.Message);
        }
    }

    public async Task<IReadOnlyList<string>> ListDatabasesAsync(
        DatabaseConnectionSnapshot connection,
        CancellationToken cancellationToken = default)
    {
        await using var session = await sessions.OpenAsync(
            connection with { Database = PgArguments.MaintenanceDatabase }, null, cancellationToken);
        return (await session.QueryAsync(PostgresQueries.Databases, [], cancellationToken))
            .Select(row => Text(row[0]))
            .ToList();
    }

    public async Task<IReadOnlyList<ColumnInfo>> ListColumnsAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default)
    {
        await using var session = await sessions.OpenAsync(connection, null, cancellationToken);
        return (await session.QueryAsync(PostgresQueries.Columns, [], cancellationToken))
            .Select(row => new ColumnInfo(Text(row[0]), Text(row[1]), Text(row[2]), Text(row[3]), row[4] as string))
            .ToList();
    }

    public async Task<IReadOnlyList<TableInfo>> ListTablesAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default)
    {
        await using var session = await sessions.OpenAsync(connection, null, cancellationToken);
        return (await session.QueryAsync(PostgresQueries.Tables, [], cancellationToken))
            .Select(row => new TableInfo(Text(row[0]), Text(row[1]), Number(row[2]), Number(row[3])))
            .ToList();
    }

    public async Task<DatabaseStructure> GetStructureAsync(DatabaseConnectionSnapshot connection, CancellationToken cancellationToken = default)
    {
        await using var session = await sessions.OpenAsync(connection, null, cancellationToken);

        var schemas = (await session.QueryAsync(PostgresQueries.Schemas, [], cancellationToken)).Select(row => Text(row[0])).ToList();
        var tables = (await session.QueryAsync(PostgresQueries.Tables, [], cancellationToken)).Select(row => $"{Text(row[0])}.{Text(row[1])}").ToList();
        var constraints = (await session.QueryAsync(PostgresQueries.ConstraintsByType, [], cancellationToken))
            .ToDictionary(row => Text(row[0]), row => (int)Number(row[1]), StringComparer.Ordinal);
        var indexes = (int)Number((await session.QueryAsync(PostgresQueries.IndexCount, [], cancellationToken)).Single()[0]);
        var sequences = (int)Number((await session.QueryAsync(PostgresQueries.SequenceCount, [], cancellationToken)).Single()[0]);

        return new DatabaseStructure(schemas, tables, constraints, indexes, sequences);
    }

    public async Task<IReadOnlyList<RowCount>> CountRowsAsync(
        DatabaseConnectionSnapshot connection,
        IReadOnlyList<string> tables,
        long exactLimit,
        CancellationToken cancellationToken = default)
    {
        await using var session = await sessions.OpenAsync(connection, null, cancellationToken);
        var counts = new List<RowCount>();

        foreach (var table in tables)
        {
            var quoted = PostgresQueries.QuoteTable(table);
            var estimateRows = await session.QueryAsync(PostgresQueries.EstimatedRows, [quoted], cancellationToken);

            if (estimateRows.Count == 0)
            {
                // A tabela não existe deste lado: quem compara vê a ausência.
                continue;
            }

            var estimate = Number(estimateRows[0][0]);

            counts.Add(estimate > exactLimit
                ? new RowCount(table, estimate, IsEstimate: true)
                : new RowCount(table, Number((await session.QueryAsync(PostgresQueries.CountRows(table), [], cancellationToken)).Single()[0]), IsEstimate: false));
        }

        return counts;
    }

    public async Task<ColumnFingerprint> FingerprintAsync(
        DatabaseConnectionSnapshot connection,
        ColumnReference column,
        string salt,
        int limit,
        CancellationToken cancellationToken = default)
    {
        await using var session = await sessions.OpenAsync(connection, null, cancellationToken);

        var table = $"{MaskingScriptBuilder.QuoteIdentifier(column.Schema)}.{MaskingScriptBuilder.QuoteIdentifier(column.Table)}";
        var key = (await session.QueryAsync(PostgresQueries.PrimaryKey, [table], cancellationToken)).Select(row => Text(row[0])).ToList();

        if (key.Count == 0)
        {
            return ColumnFingerprint.Empty(column);
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);

        foreach (var row in await session.QueryAsync(PostgresQueries.Fingerprint(column, key, limit), [salt], cancellationToken))
        {
            values[Text(row[0])] = row[1] as string;
        }

        return new ColumnFingerprint(column, values);
    }

    private static DatabaseConnectionSnapshot Maintenance(DatabaseConnectionSnapshot connection) =>
        connection.HasDatabase ? connection : connection with { Database = PgArguments.MaintenanceDatabase };

    internal static string Text(object? value) => Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;

    internal static long Number(object? value) => value is null ? 0 : Convert.ToInt64(value, CultureInfo.InvariantCulture);

    internal static bool Flag(object? value) => value is true;
}

/// <summary>O PostgreSQL Anonymizer pelo catálogo (ADR-056). Nenhuma função do anon é chamada aqui.</summary>
internal sealed class PostgresAnonymizerInspector(IPostgresSessionFactory sessions) : IPostgresAnonymizerInspector
{
    public async Task<AnonymizerStatus> GetStatusAsync(
        DatabaseConnectionSnapshot connection,
        string policyName,
        CancellationToken cancellationToken = default)
    {
        await using var session = await sessions.OpenAsync(connection, null, cancellationToken);

        var available = (await session.QueryAsync(PostgresQueries.AnonymizerAvailable, [], cancellationToken)).FirstOrDefault();

        if (available is null)
        {
            return AnonymizerStatus.Unavailable;
        }

        var installed = available[1] as string;

        if (installed is null)
        {
            return AnonymizerStatus.Unavailable with { Available = true, AvailableVersion = available[0] as string };
        }

        var transparent = PostgresServerInspector.Text((await session.QueryAsync(PostgresQueries.TransparentMasking, [], cancellationToken)).Single()[0]);
        var masked = PostgresServerInspector.Flag((await session.QueryAsync(PostgresQueries.RoleIsMasked, [policyName], cancellationToken)).Single()[0]);
        var functions = PostgresServerInspector.Flag((await session.QueryAsync(PostgresQueries.AnonymizerFunctions, [], cancellationToken)).Single()[0]);
        var rules = (await session.QueryAsync(PostgresQueries.MaskingRules, [policyName], cancellationToken))
            .Select(row => new ServerMaskingRule(
                PostgresServerInspector.Text(row[0]),
                PostgresServerInspector.Text(row[1]),
                PostgresServerInspector.Text(row[2]),
                PostgresServerInspector.Text(row[3])))
            .ToList();

        return new AnonymizerStatus(
            true,
            available[0] as string,
            true,
            installed,
            transparent is "on" or "true" or "1",
            masked,
            functions,
            rules);
    }
}
