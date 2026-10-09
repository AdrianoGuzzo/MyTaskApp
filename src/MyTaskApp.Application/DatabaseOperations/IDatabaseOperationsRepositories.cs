using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Application.DatabaseOperations;

/// <summary>As conexões PostgreSQL cadastradas (ADR-056). Estreita por necessidade (ADR-005).</summary>
public interface IDatabaseConnectionRepository
{
    Task AddAsync(DatabaseConnection connection, CancellationToken cancellationToken = default);

    Task<DatabaseConnection?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Todas, por nome. São poucas: é cadastro, não dado.</summary>
    Task<IReadOnlyList<DatabaseConnection>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>Outro cadastro com o mesmo nome, sem diferenciar maiúsculas.</summary>
    Task<bool> NameExistsAsync(string name, Guid? exceptId, CancellationToken cancellationToken = default);

    void Remove(DatabaseConnection connection);
}

/// <summary>Os perfis de anonimização, com as regras (ADR-056).</summary>
public interface IAnonymizationProfileRepository
{
    Task AddAsync(AnonymizationProfile profile, CancellationToken cancellationToken = default);

    Task<AnonymizationProfile?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AnonymizationProfile>> ListAsync(CancellationToken cancellationToken = default);

    Task<bool> AnyUsesConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default);

    void Remove(AnonymizationProfile profile);
}

/// <summary>Os perfis de cópia (ADR-056).</summary>
public interface IDatabaseCopyProfileRepository
{
    Task AddAsync(DatabaseCopyProfile profile, CancellationToken cancellationToken = default);

    Task<DatabaseCopyProfile?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DatabaseCopyProfile>> ListAsync(CancellationToken cancellationToken = default);

    Task<bool> AnyUsesConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default);

    Task<bool> AnyUsesAnonymizationProfileAsync(Guid anonymizationProfileId, CancellationToken cancellationToken = default);

    void Remove(DatabaseCopyProfile profile);
}

/// <summary>A trilha das operações de banco (ADR-056). Só se acrescenta; nada é editado por fora da entidade.</summary>
public interface IDatabaseOperationAuditLog
{
    Task RecordAsync(DatabaseOperationAudit audit, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<DatabaseOperationAudit>> ListRecentAsync(int limit, CancellationToken cancellationToken = default);

    /// <summary>As que ficaram "em andamento" — numa volta do app, as que a queda interrompeu.</summary>
    Task<IReadOnlyList<DatabaseOperationAudit>> ListRunningAsync(CancellationToken cancellationToken = default);
}

internal static class DatabaseOperationsRepositoryExtensions
{
    public static async Task<DatabaseConnection> GetByIdAsync(
        this IDatabaseConnectionRepository connections,
        Guid id,
        CancellationToken cancellationToken) =>
        await connections.FindByIdAsync(id, cancellationToken)
        ?? throw new DomainException("Conexão de banco não encontrada.");

    public static async Task<AnonymizationProfile> GetByIdAsync(
        this IAnonymizationProfileRepository profiles,
        Guid id,
        CancellationToken cancellationToken) =>
        await profiles.FindByIdAsync(id, cancellationToken)
        ?? throw new DomainException("Perfil de anonimização não encontrado.");

    public static async Task<DatabaseCopyProfile> GetByIdAsync(
        this IDatabaseCopyProfileRepository profiles,
        Guid id,
        CancellationToken cancellationToken) =>
        await profiles.FindByIdAsync(id, cancellationToken)
        ?? throw new DomainException("Perfil de cópia não encontrado.");
}
