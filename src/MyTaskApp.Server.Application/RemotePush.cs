using MyTaskApp.Remote.Contracts;
namespace MyTaskApp.Server.Application;
public interface IRemotePushSubscriptions
{
    Task RegisterAsync(SyncCaller caller, RemotePushSubscription subscription, CancellationToken cancellationToken);
    Task RemoveAsync(SyncCaller caller, CancellationToken cancellationToken);
}
