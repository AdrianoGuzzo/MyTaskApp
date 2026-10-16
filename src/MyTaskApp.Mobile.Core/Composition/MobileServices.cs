using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using MyTaskApp.Application;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Infrastructure;
using MyTaskApp.Infrastructure.Sync.Http;
using MyTaskApp.Mobile.Core.Presentation;
using MyTaskApp.Mobile.Core.SignIn;
using MyTaskApp.Mobile.Core.ViewModels;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Mobile.Core.Composition;

/// <summary>O que a casca do app sabe e o núcleo não: onde gravar e como o aparelho se chama.</summary>
public sealed class MobileHostOptions
{
    /// <summary>A pasta de dados do app (<c>FileSystem.AppDataDirectory</c>): banco e id do dispositivo.</summary>
    public required string DataDirectory { get; init; }

    /// <summary>O nome do aparelho, para a lista de dispositivos e a auditoria.</summary>
    public string? DeviceName { get; init; }

    /// <summary>O servidor de sincronização; vazio = o que a build gravou, ou nenhum.</summary>
    public string? ServerUrl { get; init; }

    public string DatabaseFileName { get; init; } = "mytaskapp.db";
}

/// <summary>
/// A composição do celular (ADR-067): a mesma Application e os mesmos dados do
/// desktop (<see cref="DependencyInjection.AddLocalData"/>), e nada de
/// processo, Git, terminal ou Jira. A casca MAUI registra as portas da
/// plataforma (<see cref="Ports"/> e o <see cref="Infrastructure.Secrets.ISecretStore"/>).
/// </summary>
public static class MobileServices
{
    public static IServiceCollection AddMobileCore(this IServiceCollection services, MobileHostOptions host)
    {
        ArgumentNullException.ThrowIfNull(host);

        var settings = new Dictionary<string, string?>
        {
            ["Database:Directory"] = host.DataDirectory,
            ["Database:FileName"] = host.DatabaseFileName,
        };

        if (!string.IsNullOrWhiteSpace(host.ServerUrl))
        {
            settings["Sync:ApiBaseUrl"] = host.ServerUrl.Trim();
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();

        // Antes da Application: ela registra com TryAdd, e quem sabe o nome ganha.
        services.TryAddSingleton<ICurrentUser>(new DeviceUser(host.DeviceName));
        services.TryAddSingleton<IUseCaseRunner, ScopedUseCaseRunner>();

        services.AddApplication(configuration);
        services.AddLocalData(
            configuration,
            new LocalDataOptions { DeviceIdentityFolder = host.DataDirectory, DeviceNickname = host.DeviceName });

        // O login volta pelo esquema do app; a casca entrega a volta aqui.
        var redirect = new AppLinkAuthorizationRedirect(SyncSchema.MobileClientId, new Uri(SyncSchema.MobileRedirectUri));
        services.AddSingleton(redirect);
        services.AddSingleton<ISyncAuthorizationRedirect>(redirect);
        services.AddSingleton<AppLinkSignIn>();

        services.AddSingleton<MobileLifecycle>();
        services.AddSingleton<OriginCatalog>();

        // A lista fica viva entre as telas: voltar do detalhe não recarrega do zero.
        services.AddSingleton<TaskListViewModel>();
        services.AddSingleton<AccountViewModel>();
        services.AddTransient<ConnectViewModel>();
        services.AddTransient<TaskDetailViewModel>();
        services.AddTransient<TaskFormViewModel>();

        return services;
    }
}
