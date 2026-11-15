using MyTaskApp.Remote.Contracts;
namespace MyTaskApp.Application.Remote;
public interface IRemotePushApi
{
    Task RegisterAsync(RemotePushSubscription subscription, CancellationToken cancellationToken);
    Task RemoveAsync(CancellationToken cancellationToken);
}
