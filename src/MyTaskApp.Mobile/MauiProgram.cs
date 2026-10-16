using System.Reflection;
using Microsoft.Extensions.Logging;
using MyTaskApp.Infrastructure.Secrets;
using MyTaskApp.Mobile.Adapters;
using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Views;

namespace MyTaskApp.Mobile;

/// <summary>
/// A composição do app no celular (ADR-065): as portas da plataforma aqui, o
/// resto em <see cref="MobileServices.AddMobileCore"/> — a mesma Application e
/// os mesmos dados do desktop.
/// </summary>
public static class MauiProgram
{
    public const string ServerMetadata = "SyncApiBaseUrl";

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
#endif

        builder.Services.AddSingleton<ISecretStore, MauiSecretStore>();
        builder.Services.AddSingleton<IWebAuthenticatorPort, MauiWebAuthenticator>();
        builder.Services.AddSingleton<IUiDispatcher, MauiDispatcher>();
        builder.Services.AddSingleton<INavigator, ShellNavigator>();
        builder.Services.AddSingleton<IDialogService, MauiDialogs>();

        builder.Services.AddMobileCore(new MobileHostOptions
        {
            DataDirectory = data,
            DeviceName = DeviceName(),
            ServerUrl = BuildServer(),
        });

        builder.Services.AddSingleton<AppShell>();
        builder.Services.AddSingleton<TaskListPage>();
        builder.Services.AddSingleton<AccountPage>();
        builder.Services.AddTransient<TaskDetailPage>();
        builder.Services.AddTransient<TaskFormPage>();
        builder.Services.AddTransient<FiltersPage>();
        builder.Services.AddTransient<ConnectPage>();

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

    /// <summary>O servidor gravado na build (<c>-p:SyncApiBaseUrl=…</c>); no Debug, o local pelo adb reverse.</summary>
    private static string? BuildServer() =>
        typeof(MauiProgram).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(attribute => attribute.Key == ServerMetadata)?.Value is { Length: > 0 } url
            ? url
            : null;
}
