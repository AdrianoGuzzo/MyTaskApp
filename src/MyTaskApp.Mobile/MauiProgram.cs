using System.Reflection;
using Microsoft.Extensions.Logging;
using MyTaskApp.Infrastructure.Secrets;
using MyTaskApp.Mobile.Adapters;
using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Views;

namespace MyTaskApp.Mobile;

/// <summary>
/// A composição do app no celular (ADR-067): as portas da plataforma aqui, o
/// resto em <see cref="MobileServices.AddMobileCore"/> — a mesma Application e
/// os mesmos dados do desktop.
/// </summary>
public static class MauiProgram
{
    public const string ServerSuggestionMetadata = "SyncServerSuggestion";

    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder.UseMauiApp<App>();

        // Escopos conferidos como no desktop. Sem ValidateOnBuild: a Application
        // registra casos de uso do computador (Git, Jira) que o celular nunca pede.
        builder.ConfigureContainer(new DefaultServiceProviderFactory(new ServiceProviderOptions { ValidateScopes = true }));

#if DEBUG
        builder.Logging.AddDebug();
#endif

        var data = FileSystem.AppDataDirectory;

#if IOS
        BackupExclusion.Apply(data);
        KeyboardMappings.Apply();
#elif ANDROID
        builder.ConfigureMauiHandlers(handlers => handlers.AddHandler<Shell, ThemedShellRenderer>());
#endif

        builder.Services.AddSingleton<ISecretStore, MauiSecretStore>();
        builder.Services.AddSingleton<IWebAuthenticatorPort, MauiWebAuthenticator>();
        builder.Services.AddSingleton<IUiDispatcher, MauiDispatcher>();
        builder.Services.AddSingleton<INavigator, ShellNavigator>();
        builder.Services.AddSingleton<IDialogService, MauiDialogs>();
        builder.Services.AddSingleton<MauiThemeService>();
        builder.Services.AddSingleton<IThemeService>(provider => provider.GetRequiredService<MauiThemeService>());
        builder.Services.AddSingleton<IPreferenceStore, MauiPreferences>();
        builder.Services.AddSingleton<IDeviceUnlock, MauiDeviceUnlock>();

        builder.Services.AddMobileCore(new MobileHostOptions
        {
            DataDirectory = data,
            DeviceName = DeviceName(),
            SuggestedServerUrl = ServerSuggestion(),
#if DEBUG
            AllowHttpLoopback = true,
#endif
        });

        builder.Services.AddSingleton<AppShell>();
        builder.Services.AddSingleton<TaskListPage>();
        builder.Services.AddSingleton<HistoryPage>();
        builder.Services.AddSingleton<AccountPage>();
        builder.Services.AddSingleton<RemotePage>();
        builder.Services.AddTransient<TaskDetailPage>();
        builder.Services.AddTransient<TaskFormPage>();
        builder.Services.AddTransient<FiltersPage>();
        builder.Services.AddTransient<ConnectPage>();
        builder.Services.AddTransient<SyncServerPage>();
        builder.Services.AddTransient<RemoteMachinePage>();
        builder.Services.AddTransient<RemoteOperationPage>();
        builder.Services.AddTransient<RemoteSessionPage>();
        builder.Services.AddTransient<RemotePreparePage>();

        return builder.Build();
    }

    /// <summary>"Pixel da Ana": o nome que a pessoa deu ao aparelho; sem ele, marca e modelo.</summary>
    private static string DeviceName()
    {
        var name = DeviceInfo.Current.Name;

        return string.IsNullOrWhiteSpace(name)
            ? $"{DeviceInfo.Current.Manufacturer} {DeviceInfo.Current.Model}".Trim()
            : name.Trim();
    }

    /// <summary>
    /// Só o texto inicial do campo da tela de servidor: no Debug, o local pelo
    /// adb reverse. Nenhum servidor vem da build (ADR-078).
    /// </summary>
    private static string? ServerSuggestion() =>
        typeof(MauiProgram).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == ServerSuggestionMetadata)?.Value is { Length: > 0 } url
            ? url
            : null;
}
