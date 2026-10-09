using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class DatabaseConnectionRepository(MyTaskAppDbContext context) : IDatabaseConnectionRepository
{
    public async Task AddAsync(DatabaseConnection connection, CancellationToken cancellationToken = default) =>
        await context.DatabaseConnections.AddAsync(connection, cancellationToken);

    public Task<DatabaseConnection?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        context.DatabaseConnections.SingleOrDefaultAsync(connection => connection.Id == id, cancellationToken);

    public async Task<IReadOnlyList<DatabaseConnection>> ListAsync(CancellationToken cancellationToken = default) =>
        await context.DatabaseConnections.OrderBy(connection => connection.Name).ToListAsync(cancellationToken);

    // A coluna é NOCASE: a comparação no SQL já ignora maiúsculas.
    public Task<bool> NameExistsAsync(string name, Guid? exceptId, CancellationToken cancellationToken = default) =>
        context.DatabaseConnections.AnyAsync(
            connection => connection.Name == name.Trim() && connection.Id != exceptId,
            cancellationToken);

    public void Remove(DatabaseConnection connection) => context.DatabaseConnections.Remove(connection);
}

internal sealed class AnonymizationProfileRepository(MyTaskAppDbContext context) : IAnonymizationProfileRepository
{
    public async Task AddAsync(AnonymizationProfile profile, CancellationToken cancellationToken = default) =>
        await context.AnonymizationProfiles.AddAsync(profile, cancellationToken);

    public Task<AnonymizationProfile?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        context.AnonymizationProfiles
            .Include(profile => profile.Rules)
            .SingleOrDefaultAsync(profile => profile.Id == id, cancellationToken);

    public async Task<IReadOnlyList<AnonymizationProfile>> ListAsync(CancellationToken cancellationToken = default) =>
        await context.AnonymizationProfiles
            .Include(profile => profile.Rules)
            .OrderBy(profile => profile.Name)
            .ToListAsync(cancellationToken);

    public Task<bool> AnyUsesConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default) =>
        context.AnonymizationProfiles.AnyAsync(profile => profile.ConnectionId == connectionId, cancellationToken);

    public void Remove(AnonymizationProfile profile) => context.AnonymizationProfiles.Remove(profile);
}

internal sealed class DatabaseCopyProfileRepository(MyTaskAppDbContext context) : IDatabaseCopyProfileRepository
{
    public async Task AddAsync(DatabaseCopyProfile profile, CancellationToken cancellationToken = default) =>
        await context.DatabaseCopyProfiles.AddAsync(profile, cancellationToken);

    public Task<DatabaseCopyProfile?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        context.DatabaseCopyProfiles.SingleOrDefaultAsync(profile => profile.Id == id, cancellationToken);

    public async Task<IReadOnlyList<DatabaseCopyProfile>> ListAsync(CancellationToken cancellationToken = default) =>
        await context.DatabaseCopyProfiles.OrderBy(profile => profile.Name).ToListAsync(cancellationToken);

    public Task<bool> AnyUsesConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default) =>
        context.DatabaseCopyProfiles.AnyAsync(
            profile => profile.SourceConnectionId == connectionId || profile.DestinationConnectionId == connectionId,
            cancellationToken);

    public Task<bool> AnyUsesAnonymizationProfileAsync(Guid anonymizationProfileId, CancellationToken cancellationToken = default) =>
        context.DatabaseCopyProfiles.AnyAsync(profile => profile.AnonymizationProfileId == anonymizationProfileId, cancellationToken);

    public void Remove(DatabaseCopyProfile profile) => context.DatabaseCopyProfiles.Remove(profile);
}

internal sealed class SavedDatabaseRepository(MyTaskAppDbContext context) : ISavedDatabaseRepository
{
    public async Task AddAsync(SavedDatabase saved, CancellationToken cancellationToken = default) =>
        await context.SavedDatabases.AddAsync(saved, cancellationToken);

    public Task<SavedDatabase?> FindByIdAsync(Guid id, CancellationToken cancellationToken = default) =>
        context.SavedDatabases.SingleOrDefaultAsync(saved => saved.Id == id, cancellationToken);

    public async Task<IReadOnlyList<SavedDatabase>> ListAsync(CancellationToken cancellationToken = default) =>
        await context.SavedDatabases.OrderBy(saved => saved.Alias).ToListAsync(cancellationToken);

    // A coluna é NOCASE: a comparação no SQL já ignora maiúsculas.
    public Task<bool> AliasExistsAsync(string alias, Guid? exceptId, CancellationToken cancellationToken = default) =>
        context.SavedDatabases.AnyAsync(saved => saved.Alias == alias.Trim() && saved.Id != exceptId, cancellationToken);

    public Task<bool> AnyUsesConnectionAsync(Guid connectionId, CancellationToken cancellationToken = default) =>
        context.SavedDatabases.AnyAsync(saved => saved.ConnectionId == connectionId, cancellationToken);

    public Task<bool> AnyUsesAnonymizationProfileAsync(Guid anonymizationProfileId, CancellationToken cancellationToken = default) =>
        context.SavedDatabases.AnyAsync(saved => saved.AnonymizationProfileId == anonymizationProfileId, cancellationToken);

    public void Remove(SavedDatabase saved) => context.SavedDatabases.Remove(saved);
}

internal sealed class EfDatabaseOperationAuditLog(MyTaskAppDbContext context) : IDatabaseOperationAuditLog
{
    /// <summary>Só rastreia; quem grava é o <c>SaveChanges</c> do caso de uso, como no <see cref="EfTaskAuditLog"/>.</summary>
    public async Task RecordAsync(DatabaseOperationAudit audit, CancellationToken cancellationToken = default) =>
        await context.DatabaseOperationAudits.AddAsync(audit, cancellationToken);

    public async Task<IReadOnlyList<DatabaseOperationAudit>> ListRecentAsync(int limit, CancellationToken cancellationToken = default) =>
        await context.DatabaseOperationAudits
            .AsNoTracking()
            .OrderByDescending(audit => audit.StartedAt)
            .ThenByDescending(audit => audit.Id)
            .Take(Math.Clamp(limit, 1, 1000))
            .ToListAsync(cancellationToken);

    // Rastreadas: quem pede é a recuperação, que vai marcá-las como interrompidas.
    public async Task<IReadOnlyList<DatabaseOperationAudit>> ListRunningAsync(CancellationToken cancellationToken = default) =>
        await context.DatabaseOperationAudits
            .Where(audit => audit.Status == DatabaseOperationStatus.Running)
            .ToListAsync(cancellationToken);
}
