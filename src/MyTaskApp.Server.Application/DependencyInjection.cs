using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace MyTaskApp.Server.Application;

public static class DependencyInjection
{
    /// <summary>Os casos de uso do servidor. As portas vêm da infraestrutura.</summary>
    public static IServiceCollection AddServerApplication(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(new PullLimits());
        services.TryAddSingleton(new PushLimits());
        services.TryAddSingleton<IRemoteNotifier>(NullRemoteNotifier.Instance);
        services.AddScoped<PushProcessor>();
        services.AddScoped<SyncQueries>();
        services.AddScoped<DeviceUseCases>();
        services.AddScoped<RemoteDeviceUseCases>();
        services.AddScoped<RemotePairingUseCases>();
        services.AddScoped<RemoteOperationUseCases>();
        services.AddScoped<RemoteApprovalUseCases>();
        services.AddScoped<RemoteCatalogUseCases>();

        return services;
    }
}
