using Microsoft.Extensions.Logging;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.Application;

/// <summary>
/// Os dispositivos de um usuário (ADR-061): o "olá" de cada ciclo, o
/// apelido, a lista e a revogação. Cada usuário só enxerga os seus.
/// </summary>
public sealed class DeviceUseCases(
    IDeviceRegistry devices,
    IAuthorizationRevoker revoker,
    TimeProvider timeProvider,
    ILogger<DeviceUseCases> logger)
{
    public const string DefaultNickname = "Dispositivo";

    private const int MaxPlatformLength = 40;

    /// <summary>
    /// Registra (no primeiro) ou atualiza versão e plataforma. Revogado não
    /// volta: o desktop precisa se conectar de novo como dispositivo novo.
    /// </summary>
    public async Task<DeviceInfo> HelloAsync(SyncCaller caller, DeviceHello hello, CancellationToken cancellationToken)
    {
        if (hello.SchemaVersion < SyncSchema.MinimumClient)
        {
            throw SyncServerException.UpgradeRequired();
        }

        if (ReceivedText.HasControl(hello.Platform) || ReceivedText.HasControl(hello.AppVersion))
        {
            throw SyncServerException.BadRequest("Plataforma e versão não podem ter caracteres de controle.");
        }

        var now = timeProvider.GetUtcNow();
        var device = await devices.FindAsync(caller.UserId, caller.DeviceId, cancellationToken);

        if (device is { IsRevoked: true })
        {
            throw SyncServerException.DeviceRevoked();
        }

        device ??= new DeviceRecord
        {
            UserId = caller.UserId,
            DeviceId = caller.DeviceId,
            Nickname = CleanNickname(hello.Nickname) ?? DefaultNickname,
            RegisteredAt = now,
        };

        device.Platform = Clip(hello.Platform, MaxPlatformLength);
        device.AppVersion = Clip(hello.AppVersion, MaxPlatformLength);
        device.SchemaVersion = hello.SchemaVersion;
        device.LastSeenAt = now;

        await devices.SaveAsync(device, cancellationToken);

        return device.ToInfo(caller.DeviceId);
    }

    public async Task<IReadOnlyList<DeviceInfo>> ListAsync(SyncCaller caller, CancellationToken cancellationToken) =>
        [
            .. (await devices.ListAsync(caller.UserId, cancellationToken))
                .OrderBy(device => device.IsRevoked)
                .ThenByDescending(device => device.LastSeenAt)
                .Select(device => device.ToInfo(caller.DeviceId)),
        ];

    public async Task<DeviceInfo> RenameAsync(SyncCaller caller, Guid deviceId, string? nickname, CancellationToken cancellationToken)
    {
        var device = await devices.FindAsync(caller.UserId, deviceId, cancellationToken) ?? throw SyncServerException.NotFound();

        device.Nickname = CleanNickname(nickname)
            ?? throw SyncServerException.BadRequest($"O apelido precisa ter de 1 a {SyncSchema.MaxNicknameLength} caracteres.");

        await devices.SaveAsync(device, cancellationToken);

        return device.ToInfo(caller.DeviceId);
    }

    /// <summary>
    /// Revoga: as autorizações e os tokens do dispositivo deixam de valer, e
    /// toda chamada dele passa a ser recusada. Os dados no computador dele
    /// ficam lá — só param de sincronizar.
    /// </summary>
    public async Task<DeviceInfo> RevokeAsync(SyncCaller caller, Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await devices.FindAsync(caller.UserId, deviceId, cancellationToken) ?? throw SyncServerException.NotFound();

        if (!device.IsRevoked)
        {
            device.RevokedAt = timeProvider.GetUtcNow();
            await devices.SaveAsync(device, cancellationToken);
            await revoker.RevokeDeviceAsync(caller.UserId, deviceId, cancellationToken);

            logger.LogInformation("SyncDeviceRevoked {UserId} {DeviceId}", caller.UserId, deviceId);
        }

        return device.ToInfo(caller.DeviceId);
    }

    /// <summary>O dispositivo do token existe e não foi revogado.</summary>
    public async Task<bool> IsActiveAsync(SyncCaller caller, CancellationToken cancellationToken) =>
        await devices.FindAsync(caller.UserId, caller.DeviceId, cancellationToken) is { IsRevoked: false };

    public static string? CleanNickname(string? nickname)
    {
        var trimmed = nickname?.Trim();

        return string.IsNullOrEmpty(trimmed) || trimmed.Length > SyncSchema.MaxNicknameLength || trimmed.Any(char.IsControl)
            ? null
            : trimmed;
    }

    private static string Clip(string? value, int max)
    {
        var trimmed = value?.Trim() ?? string.Empty;
        return trimmed.Length > max ? trimmed[..max] : trimmed;
    }
}
