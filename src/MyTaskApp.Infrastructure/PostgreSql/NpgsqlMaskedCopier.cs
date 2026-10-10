using System.Data;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using Npgsql;

namespace MyTaskApp.Infrastructure.PostgreSql;

/// <summary>
/// A cópia mascarada na consulta (ADR-058). O catálogo e a pré-visualização
/// passam pela sessão de leitura de sempre; a cópia em si usa duas conexões
/// do Npgsql: a origem, só leitura e numa transação só, e o destino, já julgado
/// pela guarda.
/// </summary>
internal sealed class NpgsqlMaskedCopier(
    IPostgresSessionFactory sessions,
    IPostgresPasswordReader passwords,
    DatabaseOperationsOptions options,
    ILogger<NpgsqlMaskedCopier> logger) : IPostgresMaskedCopier
{
    public async Task<SourceCatalog> ReadCatalogAsync(DatabaseConnectionSnapshot source, CancellationToken cancellationToken = default)
    {
        await using var session = await sessions.OpenAsync(source, null, cancellationToken);

        var columns = (await session.QueryAsync(PostgresQueries.CopyColumns, [], cancellationToken))
            .GroupBy(row => $"{Text(row[0])}.{Text(row[1])}", StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<SourceColumn>)group
                    .Select(row => new SourceColumn(Text(row[2]), Text(row[3]), PostgresServerInspector.Flag(row[4]), PostgresServerInspector.Flag(row[5])))
                    .ToList(),
                StringComparer.Ordinal);

        var tables = (await session.QueryAsync(PostgresQueries.CopyTables, [], cancellationToken))
            .Select(row => new SourceTable(
                Text(row[0]),
                Text(row[1]),
                PostgresServerInspector.Number(row[2]),
                PostgresServerInspector.Number(row[3]),
                columns.GetValueOrDefault($"{Text(row[0])}.{Text(row[1])}") ?? [],
                row[4] as string))
            .ToList();

        var keys = (await session.QueryAsync(PostgresQueries.KeyColumns, [], cancellationToken))
            .Select(row => new KeyColumn(Text(row[0]), Text(row[1]), Text(row[2]), Text(row[3]) switch
            {
                "p" => KeyRole.Primary,
                "u" => KeyRole.Unique,
                "f" => KeyRole.Foreign,
                _ => KeyRole.Referenced,
            }))
            .ToList();

        var links = (await session.QueryAsync(PostgresQueries.ForeignKeyTables, [], cancellationToken))
            .Select(row => new ForeignKeyLink(Text(row[0]), Text(row[1])))
            .ToList();

        var largeObjects = PostgresServerInspector.Number((await session.QueryAsync(PostgresQueries.LargeObjects, [], cancellationToken)).Single()[0]);

        var all = (await session.QueryAsync(PostgresQueries.Columns, [], cancellationToken))
            .Select(row => new ColumnInfo(Text(row[0]), Text(row[1]), Text(row[2]), Text(row[3]), row[4] as string))
            .ToList();

        return new SourceCatalog(tables, keys, all, largeObjects) { ForeignKeys = links };
    }

    public async Task<IReadOnlyList<MaskedPreview>> PreviewAsync(
        DatabaseConnectionSnapshot source,
        IReadOnlyList<MaskedTablePlan> tables,
        int rows,
        CancellationToken cancellationToken = default)
    {
        await using var session = await sessions.OpenAsync(source, null, cancellationToken);
        var previews = new List<MaskedPreview>();

        foreach (var table in tables.Where(table => table.HasMaskedColumns))
        {
            var masked = table.Columns.Where(column => column.IsMasked).ToList();
            var values = await session.QueryAsync(MaskedSelectBuilder.Preview(table, rows), [], cancellationToken);

            for (var index = 0; index < masked.Count; index++)
            {
                previews.Add(new MaskedPreview(
                    table.Schema,
                    table.Table,
                    masked[index].Name,
                    values.Select(row => row[index] as string).ToList()));
            }
        }

        return previews;
    }

    public async Task<IMaskedCopySession> OpenAsync(DatabaseConnectionSnapshot source, CancellationToken cancellationToken = default)
    {
        var secret = await passwords.ReadAsync(source.SecretReference, cancellationToken);
        var connection = new NpgsqlConnection(
            NpgsqlPostgresSessionFactory.Builder(source, secret, options, readOnly: true, CopyTimeoutSeconds).ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);

            // REPEATABLE READ e READ ONLY: a mesma foto do começo ao fim, e o servidor recusa qualquer escrita.
            var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

            await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
            {
                await readOnly.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var export = new NpgsqlCommand(PostgresQueries.ExportSnapshot, connection, transaction);
            var snapshot = PostgresServerInspector.Text(await export.ExecuteScalarAsync(cancellationToken));

            logger.LogInformation("MaskedCopyOpened {ConnectionId}", source.Id);
            return new Session(connection, transaction, snapshot, secret, passwords, options, logger);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
        {
            await connection.DisposeAsync();
            throw NpgsqlPostgresSessionFactory.Presentable(exception, secret);
        }
    }

    /// <summary>O COPY de uma tabela grande dura o que um dump dura, não o que uma consulta dura.</summary>
    private int CopyTimeoutSeconds => (int)Math.Min(int.MaxValue, options.DumpTimeout.TotalSeconds);

    private static string Text(object? value) => PostgresServerInspector.Text(value);

    private sealed class Session(
        NpgsqlConnection source,
        NpgsqlTransaction transaction,
        string snapshot,
        string? sourceSecret,
        IPostgresPasswordReader passwords,
        DatabaseOperationsOptions options,
        ILogger logger) : IMaskedCopySession
    {
        private const int BufferSize = 64 * 1024;

        private NpgsqlConnection? _destination;

        private string? _destinationSecret;

        public string SnapshotId { get; } = snapshot;

        public async Task ConnectDestinationAsync(
            DatabaseConnectionSnapshot destination,
            IReadOnlyCollection<string> protectedEndpoints,
            CancellationToken cancellationToken = default)
        {
            // A última barreira, como antes de um pg_restore: produção nunca recebe nada.
            PostgresProcessGuard.CheckDestination(destination, protectedEndpoints);

            _destinationSecret = await passwords.ReadAsync(destination.SecretReference, cancellationToken);
            var connection = new NpgsqlConnection(NpgsqlPostgresSessionFactory.Builder(
                destination, _destinationSecret, options, readOnly: false, (int)Math.Min(int.MaxValue, options.RestoreTimeout.TotalSeconds)).ConnectionString);

            try
            {
                await connection.OpenAsync(cancellationToken);
                _destination = connection;
            }
            catch (Exception exception) when (exception is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
            {
                await connection.DisposeAsync();
                throw NpgsqlPostgresSessionFactory.Presentable(exception, _destinationSecret);
            }
        }

        public async Task<long> CopyTableAsync(MaskedTablePlan table, Action<long>? rows, CancellationToken cancellationToken = default)
        {
            var destination = _destination ?? throw new InvalidOperationException("O destino da cópia mascarada não foi conectado.");
            long lines = 0;

            try
            {
                using var reader = await source.BeginTextExportAsync(MaskedSelectBuilder.CopyOut(table), cancellationToken);
                var writer = await destination.BeginTextImportAsync(MaskedSelectBuilder.CopyIn(table), cancellationToken);

                try
                {
                    // Repassa em blocos, sem interpretar as linhas: o formato texto do COPY é o mesmo dos dois lados.
                    var buffer = new char[BufferSize];
                    int read;

                    while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
                    {
                        await writer.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        lines += buffer.AsSpan(0, read).Count('\n');
                        rows?.Invoke(lines);
                    }
                }
                catch
                {
                    // Sem isto, o Dispose confirmaria o COPY pela metade.
                    await ((NpgsqlCopyTextWriter)writer).CancelAsync();
                    throw;
                }

                await writer.DisposeAsync();
                return lines;
            }
            catch (PostgresException exception)
            {
                logger.LogWarning("MaskedCopyTableFailed {Table} {SqlState}", table.QualifiedName, exception.SqlState);
                throw new DomainException(
                    $"{table.QualifiedName}: {NpgsqlPostgresSessionFactory.Presentable(exception, _destinationSecret ?? sourceSecret).Message}");
            }
            catch (NpgsqlException exception)
            {
                throw NpgsqlPostgresSessionFactory.Presentable(exception, _destinationSecret ?? sourceSecret);
            }
        }

        public async Task<int> CopySequencesAsync(CancellationToken cancellationToken = default)
        {
            var destination = _destination ?? throw new InvalidOperationException("O destino da cópia mascarada não foi conectado.");
            var values = new List<(string Schema, string Name, long Value)>();

            await using (var read = new NpgsqlCommand(PostgresQueries.SequenceValues, source, transaction))
            await using (var reader = await read.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    values.Add((reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
                }
            }

            foreach (var (schema, name, value) in values)
            {
                // A sequence pode não existir no destino (só dados): ela só é acertada se existir.
                await using var set = new NpgsqlCommand(
                    "SELECT setval(to_regclass(format('%I.%I', $1, $2)), $3, true) WHERE to_regclass(format('%I.%I', $1, $2)) IS NOT NULL",
                    destination);
                set.Parameters.Add(new NpgsqlParameter { Value = schema });
                set.Parameters.Add(new NpgsqlParameter { Value = name });
                set.Parameters.Add(new NpgsqlParameter { Value = value });
                await set.ExecuteNonQueryAsync(cancellationToken);
            }

            return values.Count;
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await transaction.RollbackAsync();
            }
            catch (Exception exception) when (exception is NpgsqlException or InvalidOperationException)
            {
                // A conexão já caiu: a transação de leitura morreu com ela.
            }

            await transaction.DisposeAsync();
            await source.DisposeAsync();

            if (_destination is not null)
            {
                await _destination.DisposeAsync();
            }
        }
    }
}
