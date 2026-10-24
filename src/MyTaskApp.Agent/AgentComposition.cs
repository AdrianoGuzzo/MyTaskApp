using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyTaskApp.Application;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Infrastructure;
using MyTaskApp.Infrastructure.Storage;

namespace MyTaskApp.Agent;

/// <summary>
/// O composition root do agente (ADR-085): a Application e a Infrastructure
/// do desktop, com as portas de tela trocadas por versões sem tela. Os testes
/// montam pelo mesmo método, com a configuração deles.
/// </summary>
internal static class AgentComposition
{
    /// <summary>O mesmo que o desktop liga só no desenvolvimento (ADR-078).</summary>
    private static readonly Dictionary<string, string?> DevelopmentDefaults =
#if DEBUG
        new() { ["Sync:AllowHttpLoopback"] = "true" };
#else
        [];
#endif

    /// <summary>A mesma ordem do desktop (ADR-018): o arquivo da instalação, e por cima o do usuário.</summary>
    public static IConfiguration BuildConfiguration() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(DevelopmentDefaults)
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: false, reloadOnChange: false)
            .AddJsonFile(
                Path.Combine(UserDataLocation.Current.State, "appsettings.user.json"),
                optional: true,
                reloadOnChange: false)
            .Build();

    public static IServiceCollection AddAgent(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton(configuration);

        // Antes da Application, que registra com TryAdd: quem sabe o nome ganha.
        services.TryAddSingleton<ICurrentUser, AgentUser>();
        services.TryAddSingleton<IAlertPresenter, NoAlertPresenter>();
        services.TryAddSingleton<ISoundPlayer, NoSoundPlayer>();

        services.AddApplication(configuration);
        services.AddInfrastructure(configuration);

        services.AddSingleton<AgentHost>();

        return services;
    }
}
