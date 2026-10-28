using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Infrastructure;
using MyTaskApp.Infrastructure.Remote;
using MyTaskApp.Infrastructure.Secrets;
using MyTaskApp.Infrastructure.Sync.Http;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.Presentation;
using MyTaskApp.Mobile.Core.Remote;
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

    /// <summary>
    /// Só preenche o campo da tela de servidor (no Debug, o local pelo adb
    /// reverse). Nunca é usado sozinho: o servidor é o que o usuário salvou (ADR-078).
    /// </summary>
    public string? SuggestedServerUrl { get; init; }

    /// <summary>Aceita <c>http://localhost</c>: só a build Debug liga.</summary>
    public bool AllowHttpLoopback { get; init; }

    public string DatabaseFileName { get; init; } = "mytaskapp.db";
}

/// <summary>
/// A composição do celular (ADR-067): a mesma Application e os mesmos dados do
/// desktop (<see cref="DependencyInjection.AddLocalData"/>), e nada de
/// processo, Git, terminal ou Jira. A casca MAUI registra as portas da
/// plataforma (<see cref="Ports"/>, inclusive o <see cref="Ports.IPreferenceStore"/>
/// e o <see cref="IDeviceUnlock"/>, e o <see cref="ISecretStore"/>).
/// </summary>
/// <remarks>
/// No controle remoto (E7), o celular só controla: pede e aprova, assinando
/// com a chave dele depois de destravar, e não executa nada.
/// </remarks>
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

        // Sem Sync:ApiBaseUrl: o servidor vem das preferências, na abertura (SyncServerSettings, ADR-078).
        if (host.AllowHttpLoopback)
        {
            settings["Sync:AllowHttpLoopback"] = "true";
        }

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        services.AddSingleton(host);

        // Antes da Application: ela registra com TryAdd, e quem sabe o nome ganha.
        services.TryAddSingleton<ICurrentUser>(new DeviceUser(host.DeviceName));

        services.AddApplication(configuration);
        services.AddLocalData(
            configuration,
            new LocalDataOptions { DeviceIdentityFolder = host.DataDirectory, DeviceNickname = host.DeviceName, SingleProcess = true });

        // O login volta pelo esquema do app; a casca entrega a volta aqui.
        var redirect = new AppLinkAuthorizationRedirect(SyncSchema.MobileClientId, new Uri(SyncSchema.MobileRedirectUri));
        services.AddSingleton(redirect);
        services.AddSingleton<ISyncAuthorizationRedirect>(redirect);
        services.AddSingleton<AppLinkSignIn>();

        services.AddSingleton<MobileLifecycle>();
        services.AddSingleton<OriginCatalog>();
        services.AddSingleton<DeviceFocus>();
        services.AddSingleton<SyncServerSettings>();

        // As listas ficam vivas entre as telas: voltar do detalhe não recarrega do zero.
        services.AddSingleton<TaskListViewModel>();
        services.AddSingleton<HistoryViewModel>();
        services.AddSingleton<AccountViewModel>();
        services.AddTransient<ConnectViewModel>();
        services.AddTransient<SyncServerViewModel>();
        services.AddTransient<TaskDetailViewModel>();
        services.AddTransient<TaskFormViewModel>();

        // A chave no cofre, como no desktop (ADR-087), mas só assina destravado.
        services.AddSingleton(provider => new UnlockedDeviceSigner(
            DeviceSigning.Create(provider.GetRequiredService<ISecretStore>(), provider.GetRequiredService<ILoggerFactory>()),
            provider.GetRequiredService<IDeviceUnlock>(),
            provider.GetRequiredService<TimeProvider>()));
        services.AddSingleton<IDeviceSigner>(provider => provider.GetRequiredService<UnlockedDeviceSigner>());
        services.AddSingleton<ApprovalWatcher>();
        services.AddSingleton<ApprovalAnswers>();
        services.AddSingleton<SentOperations>();
        services.AddSingleton<RemoteSender>();
        services.AddSingleton<RemoteViewModel>();
        services.AddTransient<RemoteMachineViewModel>();
        services.AddTransient<RemoteOperationViewModel>();
        services.AddTransient<RemoteSessionViewModel>();
        services.AddTransient<RemotePrepareViewModel>();

        return services;
    }
}
