using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Application;
namespace MyTaskApp.Server.Infrastructure.Persistence;

internal sealed class RemotePushDestinationRow
{
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public RemotePushProvider Provider { get; set; }
    public required string Address { get; set; }
}
internal sealed class RemotePushDeliveryRow
{
    public Guid EventId { get; set; }
    public Guid UserId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid TaskId { get; set; }
    public Guid SessionId { get; set; }
    public required string Kind { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset DueAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

public sealed class RemotePushOptions
{
    public string FcmProjectId { get; set; } = string.Empty;
    public string FcmServiceAccountFile { get; set; } = string.Empty;
    public string ApnsBaseAddress { get; set; } = string.Empty;
    public string ApnsKeyFile { get; set; } = string.Empty;
    public string ApnsKeyId { get; set; } = string.Empty;
    public string ApnsTeamId { get; set; } = string.Empty;
    public string ApnsTopic { get; set; } = string.Empty;
}

internal sealed class RemotePushSubscriptions(ServerDbContext context, IOptions<RemotePushOptions> settings) : IRemotePushSubscriptions
{
    public async Task RegisterAsync(SyncCaller caller, RemotePushSubscription subscription, CancellationToken cancellationToken)
    {
        var options = settings.Value;
        if (string.IsNullOrWhiteSpace(subscription.Address) || !Enum.IsDefined(subscription.Provider))
            throw SyncServerException.BadRequest("Destino push inválido.");
        if (subscription.Provider == RemotePushProvider.Apns && (!Uri.TryCreate(options.ApnsBaseAddress, UriKind.Absolute, out var apns) || apns.Scheme != "https" || string.IsNullOrWhiteSpace(options.ApnsBaseAddress) || string.IsNullOrWhiteSpace(options.ApnsTopic) || string.IsNullOrWhiteSpace(options.ApnsKeyId) || string.IsNullOrWhiteSpace(options.ApnsTeamId) || !File.Exists(options.ApnsKeyFile)))
            throw SyncServerException.BadRequest("O administrador ainda não configurou o push Apple no servidor.");
        if (subscription.Provider == RemotePushProvider.Fcm)
        {
            if (string.IsNullOrWhiteSpace(options.FcmProjectId) || !File.Exists(options.FcmServiceAccountFile))
                throw SyncServerException.BadRequest("O administrador ainda não configurou as notificações Android no servidor.");
            if (subscription.Address.Length > 2048 || subscription.Address.Any(character => !char.IsAsciiLetterOrDigit(character) && character is not ':' and not '-' and not '_'))
                throw SyncServerException.BadRequest("Registro de notificações Android inválido.");
        }
        else if (subscription.Provider != RemotePushProvider.Apns || subscription.Address.Length is < 32 or > 512 || subscription.Address.Any(character => !Uri.IsHexDigit(character)))
            throw SyncServerException.BadRequest("Destino push inválido.");
        var row = await context.Set<RemotePushDestinationRow>().FindAsync([caller.UserId, caller.DeviceId], cancellationToken);
        if (row is null) context.Add(new RemotePushDestinationRow { UserId = caller.UserId, DeviceId = caller.DeviceId, Address = subscription.Address, Provider = subscription.Provider });
        else { row.Provider = subscription.Provider; row.Address = subscription.Address; }
        await context.SaveChangesAsync(cancellationToken);
    }
    public async Task RemoveAsync(SyncCaller caller, CancellationToken cancellationToken)
    {
        await context.Set<RemotePushDestinationRow>().Where(row => row.UserId == caller.UserId && row.DeviceId == caller.DeviceId).ExecuteDeleteAsync(cancellationToken);
        await context.Set<RemotePushDeliveryRow>().Where(row => row.UserId == caller.UserId && row.DeviceId == caller.DeviceId).ExecuteDeleteAsync(cancellationToken);
    }
}
