using System.Data;
using Microsoft.Extensions.Logging;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using Npgsql;

namespace MyTaskApp.Infrastructure.PostgreSql;

/// <summary>Uma sessão de leitura num servidor. As linhas voltam como valores soltos; quem lê sabe o que pediu.</summary>
internal interface IPostgresSession : IAsyncDisposable
{
    Task<IReadOnlyList<object?[]>> QueryAsync(string sql, IReadOnlyList<object?> parameters, CancellationToken cancellationToken = default);
}

internal interface IPostgresSessionFactory
{
    /// <summary>Abre ou lança <see cref="DomainException"/> com a mensagem do servidor, mascarada.</summary>
    Task<IPostgresSession> OpenAsync(DatabaseConnectionSnapshot connection, SecretText? password, CancellationToken cancellationToken = default);
}

/// <summary>
/// O Npgsql atrás de uma porta fina (ADR-056): toda a lógica fica nos
/// inspetores, testados com uma sessão falsa; aqui só abrir, consultar e fechar.
/// </summary>
/// <remarks>
/// <para>
/// <b>Só leitura, duas vezes.</b> A sessão nasce com
/// <c>default_transaction_read_only=on</c> e cada consulta roda numa
/// transação <c>READ ONLY</c> que termina em rollback. Mesmo que uma SQL de
/// escrita entrasse aqui, o servidor a recusaria.
/// </para>
/// <para>
/// Sem pool (nenhuma conexão fica aberta em produção depois do uso), sem
/// logger do Npgsql e sem <c>IncludeErrorDetail</c>: o detalhe de um erro de
/// constraint traz o valor da linha, e dado pessoal não vai para log nem tela.
/// </para>
/// </remarks>
internal sealed class NpgsqlPostgresSessionFactory(
    IPostgresPasswordReader passwords,
    DatabaseOperationsOptions options,
    ILogger<NpgsqlPostgresSessionFactory> logger) : IPostgresSessionFactory
{
    public async Task<IPostgresSession> OpenAsync(
        DatabaseConnectionSnapshot connection,
        SecretText? password,
        CancellationToken cancellationToken = default)
    {
        var secret = password?.Reveal() ?? await passwords.ReadAsync(connection.SecretReference, cancellationToken);
        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = connection.Host,
            Port = connection.Port,
            Database = PgArguments.DatabaseOf(connection),
            Username = connection.Username,
            Password = secret,
            SslMode = SslMode(connection.SslMode),
            Timeout = options.ConnectTimeoutSeconds,
            CommandTimeout = options.QueryTimeoutSeconds,
            Pooling = false,
            ApplicationName = "MyTaskApp",
            Options = "-c default_transaction_read_only=on",
            IncludeErrorDetail = false,
        };

        var npgsql = new NpgsqlConnection(builder.ConnectionString);

        try
        {
            await npgsql.OpenAsync(cancellationToken);
            return new Session(npgsql, secret);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
        {
            await npgsql.DisposeAsync();
            logger.LogWarning("PostgresConnectFailed {ConnectionId} {SqlState}", connection.Id, (exception as PostgresException)?.SqlState);
            throw Presentable(exception, secret);
        }
    }

    internal static DomainException Presentable(Exception exception, string? secret)
    {
        // Só o texto principal e o SQLSTATE: Detail e Where podem trazer valores de linha.
        var text = exception is PostgresException postgres
            ? $"{postgres.MessageText} ({postgres.SqlState})"
            : exception.InnerException?.Message ?? exception.Message;

        return new DomainException(SensitiveText.Mask(text, [secret]));
    }

    private static Npgsql.SslMode SslMode(DatabaseSslMode mode) => mode switch
    {
        DatabaseSslMode.Require => Npgsql.SslMode.Require,
        DatabaseSslMode.VerifyFull => Npgsql.SslMode.VerifyFull,
        DatabaseSslMode.Disable => Npgsql.SslMode.Disable,
        _ => Npgsql.SslMode.Prefer,
    };

    private sealed class Session(NpgsqlConnection connection, string? secret) : IPostgresSession
    {
        public async Task<IReadOnlyList<object?[]>> QueryAsync(
            string sql,
            IReadOnlyList<object?> parameters,
            CancellationToken cancellationToken = default)
        {
            try
            {
                await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

                await using (var readOnly = new NpgsqlCommand("SET TRANSACTION READ ONLY", connection, transaction))
                {
                    await readOnly.ExecuteNonQueryAsync(cancellationToken);
                }

                await using var command = new NpgsqlCommand(sql, connection, transaction);

                foreach (var parameter in parameters)
                {
                    command.Parameters.Add(new NpgsqlParameter { Value = parameter ?? DBNull.Value });
                }

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
                throw Presentable(exception, secret);
            }
        }

        public ValueTask DisposeAsync() => connection.DisposeAsync();
    }
}
