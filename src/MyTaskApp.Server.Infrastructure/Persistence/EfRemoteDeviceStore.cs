using System.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Application;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.Infrastructure.Persistence;

/// <summary>Chaves e capacidades dos dispositivos (ADR-087) sobre o PostgreSQL.</summary>
internal sealed class EfRemoteDeviceStore(ServerDbContext context) : IRemoteDeviceStore
{
    public async Task<RemoteRegistrationOutcome> RegisterAsync(
        SyncCaller caller,
        DeviceRegistration registration,
        DateTimeOffset at,
        CancellationToken cancellationToken)
    {
        await using var transaction = await context.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // SELECT … FOR UPDATE na linha do dispositivo: o desktop e o agente da
        // mesma instalação se registram juntos, e o segundo espera o primeiro.
        var device = await context.Database
            .SqlQuery<DateTimeOffset?>(
                $"SELECT \"RevokedAt\" AS \"Value\" FROM \"Devices\" WHERE \"UserId\" = {caller.UserId} AND \"DeviceId\" = {caller.DeviceId} FOR UPDATE")
            .ToListAsync(cancellationToken);

        if (device.Count == 0)
        {
            throw SyncServerException.NotFound();
        }

        if (device[0] is not null)
        {
            throw SyncServerException.DeviceRevoked();
        }

        var keys = await context.DeviceKeys
            .Where(key => key.UserId == caller.UserId && key.DeviceId == caller.DeviceId)
            .ToListAsync(cancellationToken);
        var current = keys.SingleOrDefault(key => key.RevokedAt is null);
        var fingerprint = registration.Key.Fingerprint;

        var outcome = current switch
        {
            null => RemoteRegistrationOutcome.Registered,
            _ when current.Fingerprint == fingerprint => RemoteRegistrationOutcome.Renewed,
            _ => RemoteRegistrationOutcome.KeyReplaced,
        };

        if (outcome == RemoteRegistrationOutcome.KeyReplaced)
        {
            current!.RevokedAt = at;

            // O índice de uma chave valendo por dispositivo confere a cada comando:
            // a anterior sai antes de a nova entrar.
            await context.SaveChangesAsync(cancellationToken);
        }

        if (outcome != RemoteRegistrationOutcome.Renewed)
        {
            // Uma chave já revogada que volta (o cofre restaurado) vale de novo.
            if (keys.SingleOrDefault(key => key.Fingerprint == fingerprint) is { } returning)
            {
                returning.RevokedAt = null;
                returning.CreatedAt = at;
            }
            else
            {
                context.DeviceKeys.Add(new DeviceKeyRow
                {
                    UserId = caller.UserId,
                    DeviceId = caller.DeviceId,
                    Fingerprint = fingerprint,
                    SubjectPublicKeyInfo = registration.Key.SubjectPublicKeyInfo,
                    CreatedAt = at,
                });
            }
        }

        var capabilities = await context.DeviceCapabilities.FindAsync([caller.UserId, caller.DeviceId], cancellationToken);

        if (capabilities is null)
        {
            capabilities = new DeviceCapabilitiesRow { UserId = caller.UserId, DeviceId = caller.DeviceId };
            context.DeviceCapabilities.Add(capabilities);
        }

        capabilities.Roles = (int)registration.Roles;
        capabilities.Capabilities = JsonSerializer.Serialize(registration.Capabilities, RemoteJson.Options);
        capabilities.LastHeartbeatAt = at;

        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return outcome;
    }

    public async Task<IReadOnlyList<RemoteDeviceState>> ListAsync(Guid userId, CancellationToken cancellationToken)
    {
        var keys = await context.DeviceKeys.AsNoTracking()
            .Where(key => key.UserId == userId && key.RevokedAt == null)
            .ToDictionaryAsync(key => key.DeviceId, key => key.Fingerprint, cancellationToken);

        var capabilities = await context.DeviceCapabilities.AsNoTracking()
            .Where(row => row.UserId == userId)
            .ToListAsync(cancellationToken);

        return
        [
            .. capabilities
                .Where(row => keys.ContainsKey(row.DeviceId))
                .Select(row => new RemoteDeviceState(
                    row.DeviceId,
                    (DeviceRoles)row.Roles,
                    keys[row.DeviceId],
                    JsonSerializer.Deserialize<DeviceCapabilities>(row.Capabilities, RemoteJson.Options)!,
                    row.LastHeartbeatAt)),
        ];
    }
}
