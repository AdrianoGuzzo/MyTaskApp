using Microsoft.Extensions.Logging;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;
using MyTaskApp.Infrastructure.Secrets;

namespace MyTaskApp.Infrastructure.PostgreSql;

/// <summary><c>pg_dump</c> e a leitura do índice do dump (ADR-056). A guarda do <see cref="PgToolRunner"/> julga antes.</summary>
internal sealed class PostgresDumpService(PgToolRunner runner, DatabaseOperationsOptions options) : IPostgresDumpService
{
    public async Task<PgToolRun> DumpAsync(PgDumpRequest request, IProgress<PgToolEvent>? progress, CancellationToken cancellationToken = default)
    {
        var parent = Path.GetDirectoryName(request.OutputDirectory);

        if (parent is not null)
        {
            Directory.CreateDirectory(parent);
        }

        var output = await runner.RunAsync(
            new PgInvocation(
                PostgresTool.PgDump,
                PgArguments.Dump(request, options.LockWaitTimeoutSeconds),
                options.DumpTimeout,
                request.Connection,
                request.ProtectedEndpoints,
                request.SourceVersion),
            progress,
            cancellationToken);

        return output.Run;
    }

    public async Task<ArchiveSummary> ListArchiveAsync(string archiveDirectory, CancellationToken cancellationToken = default)
    {
        var output = await runner.RunAsync(
            new PgInvocation(PostgresTool.PgRestore, PgArguments.ListArchive(archiveDirectory), options.ListTimeout),
            progress: null,
            cancellationToken);

        return output.Run.Succeeded
            ? PgArchiveListParser.Parse(output.StandardOutput)
            : throw new DomainException($"Não foi possível ler o índice do dump: {output.Run.ErrorTail}");
    }
}

/// <summary><c>pg_restore</c>, <c>createdb</c> e <c>dropdb</c> (ADR-056). Nenhum deles alcança produção: a guarda recusa antes.</summary>
internal sealed class PostgresRestoreService(PgToolRunner runner, DatabaseOperationsOptions options) : IPostgresRestoreService
{
    public async Task<PgToolRun> RestoreAsync(PgRestoreRequest request, IProgress<PgToolEvent>? progress, CancellationToken cancellationToken = default) =>
        (await runner.RunAsync(
            new PgInvocation(
                PostgresTool.PgRestore,
                PgArguments.Restore(request),
                options.RestoreTimeout,
                request.Target,
                request.ProtectedEndpoints,
                request.SourceVersion),
            progress,
            cancellationToken)).Run;

    public async Task<PgToolRun> CreateDatabaseAsync(
        DatabaseConnectionSnapshot target,
        IReadOnlyCollection<string> protectedEndpoints,
        CancellationToken cancellationToken = default) =>
        (await runner.RunAsync(
            new PgInvocation(PostgresTool.CreateDb, PgArguments.CreateDatabase(target), options.CreateDropTimeout, target, protectedEndpoints),
            progress: null,
            cancellationToken)).Run;

    public async Task<PgToolRun> DropDatabaseAsync(
        DatabaseConnectionSnapshot target,
        IReadOnlyCollection<string> protectedEndpoints,
        bool force,
        CancellationToken cancellationToken = default) =>
        (await runner.RunAsync(
            new PgInvocation(PostgresTool.DropDb, PgArguments.DropDatabase(target, force), options.CreateDropTimeout, target, protectedEndpoints),
            progress: null,
            cancellationToken)).Run;
}

/// <summary>Quem lê a senha: só a Infrastructure, na hora de conectar. Nenhuma porta da Application a devolve.</summary>
internal interface IPostgresPasswordReader
{
    Task<string?> ReadAsync(string? reference, CancellationToken cancellationToken = default);
}

/// <summary>
/// As senhas das conexões no cofre do sistema (ADR-056): DPAPI no Windows,
/// chaveiro pelo <c>secret-tool</c> no Linux — o mesmo <see cref="ISecretStore"/>
/// do Jira. O banco do app guarda só o nome do segredo.
/// </summary>
internal sealed class PostgresCredentialStore(ISecretStore secrets, ILogger<PostgresCredentialStore> logger)
    : IDatabaseCredentialStore, IPostgresPasswordReader
{
    public async Task<string> StoreAsync(Guid connectionId, SecretText password, CancellationToken cancellationToken = default)
    {
        var reference = DatabaseConnection.SecretReferencePrefix + connectionId.ToString("N");
        await secrets.WriteAsync(reference, password.Reveal(), cancellationToken);
        logger.LogInformation("DatabasePasswordStored {ConnectionId}", connectionId);
        return reference;
    }

    public async Task<bool> HasAsync(string? reference, CancellationToken cancellationToken = default) =>
        IsOurs(reference) && await secrets.ReadAsync(reference!, cancellationToken) is not null;

    public Task DeleteAsync(string? reference, CancellationToken cancellationToken = default) =>
        IsOurs(reference) ? secrets.DeleteAsync(reference!, cancellationToken) : Task.CompletedTask;

    public Task<string?> ReadAsync(string? reference, CancellationToken cancellationToken = default) =>
        IsOurs(reference) ? secrets.ReadAsync(reference!, cancellationToken) : Task.FromResult<string?>(null);

    /// <summary>Só segredos de conexão: uma referência adulterada no SQLite não lê o token do Jira.</summary>
    private static bool IsOurs(string? reference) =>
        reference is not null && reference.StartsWith(DatabaseConnection.SecretReferencePrefix, StringComparison.Ordinal);
}
