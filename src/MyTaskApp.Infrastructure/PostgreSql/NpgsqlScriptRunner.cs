using System.Diagnostics;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using Npgsql;

namespace MyTaskApp.Infrastructure.PostgreSql;

/// <summary>
/// O script depois da cópia (ADR-082), pelo Npgsql: uma conexão de escrita no
/// destino, já julgado pela guarda, e o script inteiro numa transação só.
/// </summary>
/// <remarks>
/// <para>
/// Nada de <c>psql</c>: os meta-comandos dele (<c>\!</c>, <c>\copy</c>,
/// <c>\i</c>) rodariam coisas nesta máquina. Pelo Npgsql, o script é só SQL.
/// </para>
/// <para>
/// O texto do script não vai para o log, nem a mensagem do servidor: só o
/// SQLSTATE. O detalhe de um erro de constraint traria o valor da linha.
/// </para>
/// </remarks>
internal sealed class NpgsqlScriptRunner(
    IPostgresPasswordReader passwords,
    DatabaseOperationsOptions options,
    ILogger<NpgsqlScriptRunner> logger) : IPostgresScriptRunner
{
    public async Task<PostCopyScriptRun> RunAsync(
        DatabaseConnectionSnapshot target,
        string script,
        IReadOnlyCollection<string> protectedEndpoints,
        IProgress<string>? notices,
        CancellationToken cancellationToken = default)
    {
        // A última barreira, como antes de um pg_restore: produção nunca recebe nada.
        PostgresProcessGuard.CheckDestination(target, protectedEndpoints);

        if (!target.Permissions.CanExecuteSql)
        {
            throw new DatabaseSecurityException(new SecurityDecision(
                [new SecurityViolation(SecurityViolationCode.MissingPermission, $"{target.Name} não tem permissão para executar SQL.")]));
        }

        var secret = await passwords.ReadAsync(target.SecretReference, cancellationToken);
        await using var connection = new NpgsqlConnection(NpgsqlPostgresSessionFactory.Builder(
            target, secret, options, readOnly: false, (int)Math.Min(int.MaxValue, options.RestoreTimeout.TotalSeconds)).ConnectionString);

        connection.Notice += (_, args) =>
            notices?.Report(SensitiveText.Mask($"{args.Notice.Severity}: {args.Notice.MessageText}", [secret]));

        var stopwatch = Stopwatch.StartNew();

        try
        {
            await connection.OpenAsync(cancellationToken);

            // Um comando que falha desfaz o script inteiro: o banco fica como a cópia o deixou.
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = new NpgsqlCommand(script, connection, transaction);
            var rows = await command.ExecuteNonQueryAsync(cancellationToken);

            try
            {
                await transaction.CommitAsync(cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // O domínio recusa COMMIT no script; um que escapasse deixaria a transação já encerrada.
                throw new DomainException(
                    "O script encerrou a transação por conta própria (COMMIT ou ROLLBACK): o que veio antes disso pode ter ficado gravado.");
            }

            logger.LogInformation("PostCopyScriptCommitted {ConnectionId}", target.Id);
            return new PostCopyScriptRun(Math.Max(0, rows), stopwatch.Elapsed);
        }
        catch (Exception exception) when (exception is NpgsqlException or TimeoutException or System.Net.Sockets.SocketException)
        {
            logger.LogWarning("PostCopyScriptFailed {ConnectionId} {SqlState}", target.Id, (exception as PostgresException)?.SqlState);
            throw new DomainException(
                $"{NpgsqlPostgresSessionFactory.Presentable(exception, secret).Message}. O script foi desfeito: o banco ficou como a cópia o deixou.");
        }
    }
}
