using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Application;

namespace MyTaskApp.Server.Infrastructure.Persistence;

/// <summary>Os catálogos das máquinas (ADR-088) sobre o PostgreSQL.</summary>
internal sealed class EfRemoteCatalogStore(ServerDbContext context) : IRemoteCatalogStore
{
    public async Task SaveAsync(SyncCaller caller, AgentCatalog catalog, DateTimeOffset at, CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);

        var row = await context.AgentCatalogs.FindAsync([caller.UserId, caller.DeviceId], cancellationToken);

        if (row is null)
        {
            row = new AgentCatalogRow { UserId = caller.UserId, DeviceId = caller.DeviceId, Hash = catalog.Hash };
            context.AgentCatalogs.Add(row);
        }

        row.Hash = catalog.Hash;
        row.Catalog = JsonSerializer.Serialize(catalog, RemoteJson.Options);
        row.UpdatedAt = at;

        await context.SaveChangesAsync(cancellationToken);

        // O hash nas capacidades: o controlador que lista as máquinas sabe quando reler o catálogo.
        await context.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"DeviceCapabilities\" SET \"Capabilities\" = jsonb_set(\"Capabilities\", '{{catalogHash}}', to_jsonb({catalog.Hash}::text)) WHERE \"UserId\" = {caller.UserId} AND \"DeviceId\" = {caller.DeviceId}",
            cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<AgentCatalog?> FindAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken) =>
        await context.AgentCatalogs.AsNoTracking()
            .Where(row => row.UserId == userId && row.DeviceId == deviceId)
            .Select(row => row.Catalog)
            .SingleOrDefaultAsync(cancellationToken) is { } json
            ? JsonSerializer.Deserialize<AgentCatalog>(json, RemoteJson.Options)
            : null;
}
