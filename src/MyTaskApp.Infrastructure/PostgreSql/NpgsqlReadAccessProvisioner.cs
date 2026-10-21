using System.Globalization;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using Npgsql;

namespace MyTaskApp.Infrastructure.PostgreSql;

/// <summary>
/// "Preparar acesso de leitura" (ADR-081): a única escrita do app num servidor
/// fora das cópias. Cria (ou acerta) o usuário
/// <see cref="IConsultationReadAccessProvisioner.RoleName"/> e dá a ele só o
/// que ler pede.
/// </summary>
/// <remarks>
/// <para>
/// A política é conferida de novo aqui, logo antes de abrir a conexão: só
/// servidor de Desenvolvimento ou Teste, nunca produção nem um endpoint de
/// produção com outro rótulo.
/// </para>
/// <para>
/// Os comandos não aceitam parâmetro (DDL), então são montados com
/// identificadores e literais citados: o nome do usuário é constante, a senha
/// é gerada pelo app, o banco já foi validado e os schemas vêm do catálogo.
/// Nada daqui vai para log — a senha está no texto.
/// </para>
/// </remarks>
internal sealed class NpgsqlReadAccessProvisioner(
    IPostgresPasswordReader passwords,
    IDatabaseSecurityPolicy policy,
    DatabaseOperationsOptions options,
    ILogger<NpgsqlReadAccessProvisioner> logger) : IConsultationReadAccessProvisioner
{
    /// <summary>O que o servidor aplica ao usuário em toda sessão dele, além do que a consulta pede.</summary>
    private const string RoleAttributes =
        "LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS CONNECTION LIMIT 10";

    public async Task<bool> PrepareAsync(
        DatabaseConnectionSnapshot admin,
        SecretText? password,
        CancellationToken cancellationToken = default)
    {
        policy.Demand(new DatabaseOperationRequest(DatabaseOperationType.PrepareReadAccess, Destination: admin));

        var role = SqlQuoting.QuoteIdentifier(IConsultationReadAccessProvisioner.RoleName);
        var secret = await passwords.ReadAsync(admin.SecretReference, cancellationToken);
        var builder = NpgsqlPostgresSessionFactory.Builder(admin, secret, options, readOnly: false, options.QueryTimeoutSeconds);
        builder.ApplicationName = "MyTaskApp Consulta (preparo)";

        await using var connection = new NpgsqlConnection(builder.ConnectionString);

        try
        {
            await connection.OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

            var exists = await ScalarAsync(
                connection,
                transaction,
                "SELECT count(*) FROM pg_roles WHERE rolname = $1",
                IConsultationReadAccessProvisioner.RoleName,
                cancellationToken) > 0;

            if (!exists && password is null)
            {
                throw new DomainException("O usuário de leitura ainda não existe neste servidor: é preciso criar uma senha para ele.");
            }

            var statements = new List<string>();
            var withPassword = password is null ? string.Empty : $" PASSWORD {SqlQuoting.QuoteLiteral(password.Reveal())}";

            statements.Add(exists
                ? $"ALTER ROLE {role} WITH {RoleAttributes}{withPassword}"
                : $"CREATE ROLE {role} WITH {RoleAttributes}{withPassword}");

            statements.Add($"ALTER ROLE {role} SET default_transaction_read_only = on");
            statements.Add($"ALTER ROLE {role} SET statement_timeout = '60s'");
            statements.Add($"ALTER ROLE {role} SET idle_in_transaction_session_timeout = '60s'");
            statements.Add($"GRANT CONNECT ON DATABASE {SqlQuoting.QuoteIdentifier(PgArguments.DatabaseOf(admin))} TO {role}");

            foreach (var schema in await SchemasAsync(connection, transaction, cancellationToken))
            {
                var quoted = SqlQuoting.QuoteIdentifier(schema);
                statements.Add($"GRANT USAGE ON SCHEMA {quoted} TO {role}");
                statements.Add($"GRANT SELECT ON ALL TABLES IN SCHEMA {quoted} TO {role}");
            }

            foreach (var statement in statements)
            {
                await using var command = new NpgsqlCommand(statement, connection, transaction);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);

            logger.LogInformation(
                "ConsultationReadAccessPrepared {ConnectionId} {Created} {PasswordChanged}",
                admin.Id,
                !exists,
                password is not null);

            return !exists;
        }
        catch (NpgsqlException exception)
        {
            logger.LogWarning(
                "ConsultationReadAccessFailed {ConnectionId} {SqlState}",
                admin.Id,
                (exception as PostgresException)?.SqlState);

            var text = exception is PostgresException postgres
                ? $"{postgres.MessageText} ({postgres.SqlState})"
                : exception.InnerException?.Message ?? exception.Message;

            var hint = (exception as PostgresException)?.SqlState == "42501"
                ? $"O usuário {admin.Username} não pode criar usuários nem dar permissões neste servidor. "
                : string.Empty;

            throw new DomainException(hint + SensitiveText.Mask(text, [secret, password?.Reveal()]));
        }
    }

    private static async Task<long> ScalarAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        string parameter,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken), CultureInfo.InvariantCulture);
    }

    private static async Task<IReadOnlyList<string>> SchemasAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(PostgresQueries.Schemas, connection, transaction);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);

        var schemas = new List<string>();

        while (await reader.ReadAsync(cancellationToken))
        {
            schemas.Add(reader.GetString(0));
        }

        return schemas;
    }
}
