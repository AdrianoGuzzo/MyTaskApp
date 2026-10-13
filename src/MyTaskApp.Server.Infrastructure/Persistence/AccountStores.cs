using Microsoft.EntityFrameworkCore;
using MyTaskApp.Server.Application;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.Infrastructure.Persistence;

/// <summary>Quem é membro de qual workspace.</summary>
internal sealed class EfWorkspaceAccess(ServerDbContext context) : IWorkspaceAccess
{
    public async Task<WorkspaceRole?> RoleOfAsync(Guid userId, Guid workspaceId, CancellationToken cancellationToken) =>
        await context.WorkspaceMembers.AsNoTracking()
            .Where(member => member.UserId == userId && member.WorkspaceId == workspaceId)
            .Select(member => (WorkspaceRole?)member.Role)
            .SingleOrDefaultAsync(cancellationToken);

    public async Task<IReadOnlyList<WorkspaceInfo>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        await context.WorkspaceMembers.AsNoTracking()
            .Where(member => member.UserId == userId)
            .Join(context.Workspaces, member => member.WorkspaceId, workspace => workspace.Id, (member, workspace) => new { member, workspace })
            .OrderByDescending(pair => pair.workspace.IsPersonal)
            .ThenBy(pair => pair.workspace.Name)
            .Select(pair => new WorkspaceInfo(pair.workspace.Id, pair.workspace.Name, (WorkspaceRole)pair.member.Role, pair.workspace.IsPersonal))
            .ToListAsync(cancellationToken);
}

/// <summary>Os dispositivos de cada usuário.</summary>
internal sealed class EfDeviceRegistry(ServerDbContext context) : IDeviceRegistry
{
    public async Task<DeviceRecord?> FindAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken) =>
        await context.Devices.AsNoTracking().SingleOrDefaultAsync(device => device.UserId == userId && device.DeviceId == deviceId, cancellationToken) is { } row
            ? ToModel(row)
            : null;

    public async Task<IReadOnlyList<DeviceRecord>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
        [.. (await context.Devices.AsNoTracking().Where(device => device.UserId == userId).ToListAsync(cancellationToken)).Select(ToModel)];

    public async Task SaveAsync(DeviceRecord device, CancellationToken cancellationToken)
    {
        var row = await context.Devices.FindAsync([device.UserId, device.DeviceId], cancellationToken);

        if (row is null)
        {
            row = new DeviceRow { UserId = device.UserId, DeviceId = device.DeviceId, Nickname = device.Nickname, RegisteredAt = device.RegisteredAt };
            context.Devices.Add(row);
        }

        row.Nickname = device.Nickname;
        row.Platform = device.Platform;
        row.AppVersion = device.AppVersion;
        row.SchemaVersion = device.SchemaVersion;
        row.LastSeenAt = device.LastSeenAt;
        row.RevokedAt = device.RevokedAt;

        await context.SaveChangesAsync(cancellationToken);
    }

    private static DeviceRecord ToModel(DeviceRow row) => new()
    {
        UserId = row.UserId,
        DeviceId = row.DeviceId,
        Nickname = row.Nickname,
        Platform = row.Platform,
        AppVersion = row.AppVersion,
        SchemaVersion = row.SchemaVersion,
        RegisteredAt = row.RegisteredAt,
        LastSeenAt = row.LastSeenAt,
        RevokedAt = row.RevokedAt,
    };
}

/// <summary>O workspace pessoal, criado no primeiro login e nunca duplicado.</summary>
internal sealed class WorkspaceProvisioner(ServerDbContext context, TimeProvider timeProvider)
{
    public const string PersonalName = "Pessoal";

    public async Task<Guid> EnsurePersonalAsync(Guid userId, CancellationToken cancellationToken)
    {
        if (await context.Workspaces.AsNoTracking()
                .Where(workspace => workspace.OwnerUserId == userId && workspace.IsPersonal)
                .Select(workspace => (Guid?)workspace.Id)
                .SingleOrDefaultAsync(cancellationToken) is { } existing)
        {
            return existing;
        }

        var now = timeProvider.GetUtcNow();
        var workspace = new WorkspaceRow
        {
            Id = Guid.CreateVersion7(now),
            Name = PersonalName,
            OwnerUserId = userId,
            IsPersonal = true,
            CreatedAt = now,
        };

        context.Workspaces.Add(workspace);
        context.WorkspaceMembers.Add(new WorkspaceMemberRow
        {
            WorkspaceId = workspace.Id,
            UserId = userId,
            Role = (int)WorkspaceRole.Owner,
            JoinedAt = now,
        });

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return workspace.Id;
        }
        catch (DbUpdateException)
        {
            // Dois logins ao mesmo tempo: o índice único deixou um passar, e é esse que vale.
            context.ChangeTracker.Clear();

            return await context.Workspaces.AsNoTracking()
                .Where(candidate => candidate.OwnerUserId == userId && candidate.IsPersonal)
                .Select(candidate => candidate.Id)
                .SingleAsync(cancellationToken);
        }
    }
}
