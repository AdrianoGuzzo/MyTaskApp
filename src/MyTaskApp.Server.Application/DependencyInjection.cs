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
        services.AddScoped<PushProcessor>();
        services.AddScoped<SyncQueries>();
        services.AddScoped<DeviceUseCases>();

        return services;
    }
}
