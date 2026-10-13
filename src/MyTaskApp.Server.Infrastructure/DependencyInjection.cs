using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Server.Application;
using MyTaskApp.Server.Infrastructure.Persistence;

namespace MyTaskApp.Server.Infrastructure;

public static class DependencyInjection
{
    /// <summary>O PostgreSQL do servidor e as portas da sincronização.</summary>
    public static IServiceCollection AddServerInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddDbContext<ServerDbContext>(builder => builder.UseNpgsql(connectionString));

        services.AddScoped<EfSyncStore>();
        services.AddScoped<ISyncStore>(provider => provider.GetRequiredService<EfSyncStore>());
        services.AddScoped<ISyncUnitOfWork>(provider => provider.GetRequiredService<EfSyncStore>());
        services.AddScoped<IWorkspaceAccess, EfWorkspaceAccess>();
        services.AddScoped<IDeviceRegistry, EfDeviceRegistry>();
        services.AddScoped<WorkspaceProvisioner>();

        return services;
    }
}

/// <summary>Para o <c>dotnet ef migrations add</c>: um banco qualquer, nunca aberto.</summary>
internal sealed class DesignTimeServerDbContextFactory : IDesignTimeDbContextFactory<ServerDbContext>
{
    public ServerDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<ServerDbContext>()
            .UseNpgsql("Host=localhost;Database=mytaskapp_sync_design")
            .Options);
}
