using System.Data;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using Npgsql;

namespace MyTaskApp.Infrastructure.PostgreSql;

/// <summary>
/// O banco da Consulta Inteligente (ADR-081), em três camadas: o usuário só
/// de leitura (conferido no catálogo), a sessão só leitura com tempo limite, e
/// cada consulta numa transação <c>READ ONLY</c> que termina em rollback.
/// </summary>
/// <remarks>
/// <para>
/// A consulta da IA nunca é examinada por palavras: vai para o servidor como
/// subconsulta de <c>SELECT * FROM (…) AS consulta LIMIT $1</c>, e o parâmetro
/// força o protocolo estendido. É o parser do PostgreSQL que recusa duas
/// instruções, DDL, DML, CTE que altera dados, <c>COPY</c>, <c>DO</c> e
/// <c>CALL</c> — nada disso cabe numa subconsulta.
/// </para>
/// <para>
/// Sem pool, sem logger do Npgsql e sem <c>IncludeErrorDetail</c>, como as
/// outras sessões do app. O log registra quantas linhas e quanto tempo, nunca
/// o texto da consulta nem valores.
/// </para>
/// </remarks>
internal sealed class NpgsqlConsultationDatabase(
    IPostgresPasswordReader passwords,
    DatabaseOperationsOptions options,
    ILogger<NpgsqlConsultationDatabase> logger) : IConsultationDatabase
{
    public async Task<ReadAccessCheck> CheckReadAccessAsync(
        DatabaseConnectionSnapshot connection,
        CancellationToken cancellationToken = default)
    {
        var rows = await ReadAsync(connection, PostgresQueries.ConsultationReadAccess, [], ConsultationLimits.Default, cancellationToken);

        if (rows.Count == 0)
        {
            throw new DomainException("O servidor não informou as permissões do usuário da conexão.");
        }

        var row = rows[0];
        var problems = new List<string>();

        if (row[0] is true)
        {
            problems.Add("é superusuário");
        }

        if (row[1] is true)
        {
            problems.Add("pode criar usuários");
        }

        if (row[2] is true)
        {
            problems.Add("pode criar bancos");
        }

        if (row[3] is true)
        {
            problems.Add("tem permissão de replicação");
        }

        if (row[4] is true)
        {
            problems.Add("ignora as regras de segurança de linha");
        }

        if (row[5] is string roles && roles.Length > 0)
        {
            problems.Add($"é membro de {roles}");
        }

        if (Convert.ToInt64(row[6], CultureInfo.InvariantCulture) is var writable and > 0)
        {
            problems.Add($"pode alterar {writable} tabela(s)");
        }

        if (row[7] is true)
        {
            problems.Add("pode criar objetos no banco");
        }

        if (Convert.ToInt64(row[8], CultureInfo.InvariantCulture) > 0)
        {
            problems.Add("pode abrir conexões para outros bancos (dblink/postgres_fdw)");
        }

        return new ReadAccessCheck((string)row[9]!, (string)row[10]!, problems);
    }

    public async Task<ConsultationDatabaseInfo> DescribeAsync(
        DatabaseConnectionSnapshot connection,
        CancellationToken cancellationToken = default)
    {
        await using var session = await OpenAsync(connection, ConsultationLimits.Default, cancellationToken);
        var info = await session.QueryAsync("SELECT current_database()::text, current_setting('server_version')", [], cancellationToken);
        var schemas = await session.QueryAsync(PostgresQueries.ConsultationSchemas, [], cancellationToken);

        return new ConsultationDatabaseInfo(
            (string)info[0][0]!,
            (string)info[0][1]!,
            [.. schemas.Select(row => (string)row[0]!)]);
    }

    public async Task<IReadOnlyList<ConsultationTable>> ListTablesAsync(
        DatabaseConnectionSnapshot connection,
        string? schema,
        CancellationToken cancellationToken = default)
    {
        var rows = await ReadAsync(
            connection,
            PostgresQueries.ConsultationTables,
            [string.IsNullOrWhiteSpace(schema) ? null : schema.Trim()],
            ConsultationLimits.Default,
            cancellationToken);

        return
        [
            .. rows.Select(row => new ConsultationTable(
                (string)row[0]!,
                (string)row[1]!,
                (string)row[2]!,
                Convert.ToInt64(row[3], CultureInfo.InvariantCulture),
                row[4] as string)),
        ];
    }

    public async Task<ConsultationTableDetail> DescribeTableAsync(
        DatabaseConnectionSnapshot connection,
        string schema,
        string table,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(schema) || string.IsNullOrWhiteSpace(table))
        {
            throw new DomainException("Informe o schema e a tabela.");
        }

        var qualified = $"{SqlQuoting.QuoteIdentifier(schema.Trim())}.{SqlQuoting.QuoteIdentifier(table.Trim())}";

        await using var session = await OpenAsync(connection, ConsultationLimits.Default, cancellationToken);
        var found = await session.QueryAsync(PostgresQueries.ConsultationTable, [qualified], cancellationToken);

        if (found.Count == 0)
        {
            throw new DomainException($"A tabela {schema}.{table} não existe ou o usuário da consulta não pode lê-la.");
        }

        var columns = await session.QueryAsync(PostgresQueries.ConsultationColumns, [qualified], cancellationToken);
        var constraints = await session.QueryAsync(PostgresQueries.ConsultationConstraints, [qualified], cancellationToken);

        return new ConsultationTableDetail(
            schema.Trim(),
            table.Trim(),
            found[0][0] as string,
            [.. columns.Select(row => new ConsultationColumn((string)row[0]!, (string)row[1]!, row[2] is true, row[3] as string, row[4] as string))],
            [
                .. constraints.Select(row => new ConsultationConstraint(
                    (string)row[0]!,
                    ConstraintKind((string)row[1]!),
                    (string)row[2]!,
                    (string)row[3]!,
                    (string)row[4]!,
                    row[5] as string)),
            ]);
    }

    public async Task<ConsultationQueryResult> QueryAsync(
        DatabaseConnectionSnapshot connection,
        string query,
        ConsultationLimits limits,
        CancellationToken cancellationToken = default)
    {
        var text = Normalize(query);
        var stopwatch = Stopwatch.StartNew();

        await using var session = await OpenAsync(connection, limits, cancellationToken);

        try
        {
            var result = await session.ReadLimitedAsync(Wrap(text), limits, cancellationToken);

            logger.LogInformation(
                "ConsultationQuery {ConnectionId} {Rows} {Truncated} {ElapsedMs}",
                connection.Id,
                result.Rows.Count,
                result.Truncated,
                stopwatch.ElapsedMilliseconds);

            return result with { ElapsedMilliseconds = stopwatch.ElapsedMilliseconds };
        }
        catch (PostgresException exception)
        {
            logger.LogInformation(
                "ConsultationQueryRefused {ConnectionId} {SqlState} {ElapsedMs}",
                connection.Id,
                exception.SqlState,
                stopwatch.ElapsedMilliseconds);

            throw Friendly(exception, session.Secret, limits);
        }
        catch (NpgsqlException exception)
        {
            logger.LogInformation("ConsultationQueryFailed {ConnectionId} {ElapsedMs}", connection.Id, stopwatch.ElapsedMilliseconds);
            throw exception.InnerException is TimeoutException
                ? new DomainException($"A consulta passou do tempo limite de {limits.TimeoutSeconds} s. Filtre mais ou use menos tabelas.")
                : NpgsqlPostgresSessionFactory.Presentable(exception, session.Secret);
        }
    }

    /// <summary>
    /// O texto como subconsulta. A quebra de linha antes do parêntese fecha um
    /// comentário <c>--</c> no fim do texto da IA, que de outro modo engoliria o
    /// fechamento e daria um erro confuso.
    /// </summary>
    internal static string Wrap(string query) => $"SELECT * FROM (\n{query}\n) AS consulta LIMIT $1";

    /// <summary>Sem o <c>;</c> final, que a IA costuma pôr e não cabe numa subconsulta.</summary>
    internal static string Normalize(string? query)
    {
        var text = query?.Trim() ?? string.Empty;

        while (text.EndsWith(';'))
        {
            text = text[..^1].TrimEnd();
        }

        return text.Length == 0
            ? throw new DomainException("Escreva a consulta.")
            : text.Length > 20_000
                ? throw new DomainException("A consulta passa de 20.000 caracteres.")
                : text;
    }

    /// <summary>
    /// O erro do servidor, dito para quem está lendo a resposta da IA. Só o
    /// texto principal e o SQLSTATE, mascarados: detalhe traz valor de linha.
    /// </summary>
    internal static DomainException Friendly(PostgresException exception, string? secret, ConsultationLimits limits)
    {
        var reason = exception.SqlState switch
        {
            "25006" => "A consulta tentou alterar dados; aqui só leitura é permitida.",
            "42501" => "O usuário da consulta não tem permissão para isso (só leitura).",
            "57014" => $"A consulta passou do tempo limite de {limits.TimeoutSeconds} s. Filtre mais ou use menos tabelas.",
            "42601" => "A consulta tem um erro de sintaxe, ou não é um SELECT único.",
            "0A000" => "Este tipo de comando não é aceito: só um SELECT.",
            _ => null,
        };

        var server = SensitiveText.Mask($"{exception.MessageText} ({exception.SqlState})", [secret]);
        return new DomainException(reason is null ? server : $"{reason} Servidor: {server}");
    }

    private async Task<IReadOnlyList<object?[]>> ReadAsync(
        DatabaseConnectionSnapshot connection,
        string sql,
        IReadOnlyList<object?> parameters,
        ConsultationLimits limits,
        CancellationToken cancellationToken)
    {
        await using var session = await OpenAsync(connection, limits, cancellationToken);
        return await session.QueryAsync(sql, parameters, cancellationToken);
    }

    private async Task<Session> OpenAsync(
        DatabaseConnectionSnapshot connection,
        ConsultationLimits limits,
        CancellationToken cancellationToken)
    {
        if (!connection.HasDatabase)
        {
            throw new DomainException($"{connection.Name} é só o servidor: escolha o banco da consulta.");
        }

        var secret = await passwords.ReadAsync(connection.SecretReference, cancellationToken);
        var builder = NpgsqlPostgresSessionFactory.Builder(connection, secret, options, readOnly: true, limits.TimeoutSeconds + 5);

        // Mesmo que o usuário escapasse da transação só leitura, a sessão
        // inteira nasce só leitura e com tempo limite no servidor.
        var timeout = (limits.TimeoutSeconds * 1000).ToString(CultureInfo.InvariantCulture);
        builder.Options = $"-c default_transaction_read_only=on -c statement_timeout={timeout} "
            + "-c lock_timeout=3000 -c idle_in_transaction_session_timeout=60000";
        builder.ApplicationName = "MyTaskApp Consulta";

        var npgsql = new NpgsqlConnection(builder.ConnectionString);

        try
        {
            await npgsql.OpenAsync(cancellationToken);
            return new Session(npgsql, secret);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
        {
            await npgsql.DisposeAsync();
            logger.LogWarning("ConsultationConnectFailed {ConnectionId} {SqlState}", connection.Id, (exception as PostgresException)?.SqlState);
            throw NpgsqlPostgresSessionFactory.Presentable(exception, secret);
        }
    }

    private static string ConstraintKind(string code) => code switch
    {
        "p" => "primary key",
        "f" => "foreign key",
        "u" => "unique",
        "c" => "check",
        "x" => "exclusion",
        _ => code,
    };

    private sealed class Session(NpgsqlConnection connection, string? secret) : IAsyncDisposable
    {
        public string? Secret => secret;

        public async Task<IReadOnlyList<object?[]>> QueryAsync(
            string sql,
            IReadOnlyList<object?> parameters,
            CancellationToken cancellationToken)
        {
            try
            {
                await using var transaction = await BeginReadOnlyAsync(cancellationToken);
                await using var command = Command(sql, transaction, parameters);

                var rows = new List<object?[]>();

                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                {
                    while (await reader.ReadAsync(cancellationToken))
                    {
                        var row = new object?[reader.FieldCount];

                        for (var index = 0; index < row.Length; index++)
                        {
                            row[index] = await reader.IsDBNullAsync(index, cancellationToken) ? null : reader.GetValue(index);
                        }

                        rows.Add(row);
                    }
                }

                await transaction.RollbackAsync(cancellationToken);
                return rows;
            }
            catch (NpgsqlException exception)
            {
                throw NpgsqlPostgresSessionFactory.Presentable(exception, secret);
            }
        }

        /// <summary>
        /// Lê no máximo <see cref="ConsultationLimits.MaxRows"/> linhas (pede uma
        /// a mais para saber se havia mais), cada célula cortada, e para quando
        /// o texto da resposta passa do limite.
        /// </summary>
        public async Task<ConsultationQueryResult> ReadLimitedAsync(
            string sql,
            ConsultationLimits limits,
            CancellationToken cancellationToken)
        {
            await using var transaction = await BeginReadOnlyAsync(cancellationToken);
            await using var command = Command(sql, transaction, [limits.MaxRows + 1]);

            var rows = new List<IReadOnlyList<object?>>();
            var truncated = false;
            var size = 0;
            List<ConsultationResultColumn> columns;

            await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
            {
                columns =
                [
                    .. Enumerable.Range(0, reader.FieldCount)
                        .Select(index => new ConsultationResultColumn(reader.GetName(index), reader.GetDataTypeName(index))),
                ];

                while (await reader.ReadAsync(cancellationToken))
                {
                    if (rows.Count == limits.MaxRows)
                    {
                        truncated = true;
                        break;
                    }

                    var row = new object?[reader.FieldCount];

                    for (var index = 0; index < row.Length; index++)
                    {
                        var value = await reader.IsDBNullAsync(index, cancellationToken) ? null : reader.GetValue(index);
                        var (cell, cut) = Cell(value, limits.MaxCellLength);
                        row[index] = cell;
                        truncated |= cut;
                        size += cell is string text ? text.Length : 12;
                    }

                    rows.Add(row);

                    if (size > limits.MaxResponseCharacters)
                    {
                        truncated = true;
                        break;
                    }
                }
            }

            await transaction.RollbackAsync(CancellationToken.None);
            return new ConsultationQueryResult(columns, rows, truncated, 0);
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();

        private async Task<NpgsqlTransaction> BeginReadOnlyAsync(CancellationToken cancellationToken)
        {
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

            await using var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction);
            await readOnly.ExecuteNonQueryAsync(cancellationToken);

            return transaction;
        }

        private NpgsqlCommand Command(string sql, NpgsqlTransaction transaction, IReadOnlyList<object?> parameters)
        {
            var command = new NpgsqlCommand(sql, connection, transaction);

            foreach (var parameter in parameters)
            {
                command.Parameters.Add(new NpgsqlParameter { Value = parameter ?? DBNull.Value });
            }

            return command;
        }
    }

    /// <summary>
    /// Um valor como o MCP o devolve: número e booleano como são, binário só
    /// com o tamanho, e o resto como texto cortado em <paramref name="max"/>.
    /// </summary>
    internal static (object? Value, bool Cut) Cell(object? value, int max)
    {
        switch (value)
        {
            case null:
                return (null, false);
            case bool or short or int or long or decimal or double or float:
                return (value, false);
            case byte[] bytes:
                return ($"<binário, {bytes.Length} bytes>", false);
            case DateTime dateTime:
                return (dateTime.ToString("O", CultureInfo.InvariantCulture), false);
            case DateTimeOffset offset:
                return (offset.ToString("O", CultureInfo.InvariantCulture), false);
            case DateOnly date:
                return (date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), false);
            case TimeOnly time:
                return (time.ToString("HH:mm:ss.FFFFFFF", CultureInfo.InvariantCulture), false);
            case Array array:
                return Text(Join(array), max);
            default:
                return Text(Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty, max);
        }
    }

    private static string Join(Array array)
    {
        var builder = new StringBuilder("{");

        foreach (var item in array)
        {
            if (builder.Length > 1)
            {
                builder.Append(',');
            }

            builder.Append(Cell(item, int.MaxValue).Value ?? "NULL");
        }

        return builder.Append('}').ToString();
    }

    private static (object? Value, bool Cut) Text(string text, int max) =>
        text.Length <= max ? (text, false) : (text[..max] + "…", true);
}
