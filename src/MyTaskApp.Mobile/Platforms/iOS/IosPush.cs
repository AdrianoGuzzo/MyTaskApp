using Foundation;
using UIKit;
using UserNotifications;
namespace MyTaskApp.Mobile;

public static class IosPush
{
    public static async Task EnableAsync()
    {
#if REMOTE_PUSH
        var (granted, error) = await UNUserNotificationCenter.Current.RequestAuthorizationAsync(UNAuthorizationOptions.Alert | UNAuthorizationOptions.Sound | UNAuthorizationOptions.Badge);
        if (error is not null || !granted) throw new InvalidOperationException("Permita as notificações nas configurações do iOS.");
        Preferences.Default.Set(RemotePushRegistration.EnabledKey, true);
        Preferences.Default.Set("push.status", "Aguardando o registro com a Apple.");
        await MainThread.InvokeOnMainThreadAsync(() => UIApplication.SharedApplication.RegisterForRemoteNotifications());
#else
        await Task.CompletedTask;
        throw new InvalidOperationException("Esta instalação não inclui push. Use uma build com EnableRemotePush=true e perfil Apple com notificações habilitadas.");
#endif
    }
    public static async Task RegisteredAsync(NSData token)
    {
        Preferences.Default.Set(RemotePushRegistration.AddressKey, Convert.ToHexString(token.ToArray()).ToLowerInvariant());
        try { await IPlatformApplication.Current!.Services.GetRequiredService<RemotePushRegistration>().RegisterSavedAsync(); }
        catch (Exception) { Preferences.Default.Set("push.status", "Registro de push pendente. Abra a aba Remoto com rede."); }
    }
}

public sealed class IosPushDelegate : UNUserNotificationCenterDelegate
{
    public override void WillPresentNotification(UNUserNotificationCenter center, UNNotification notification, Action<UNNotificationPresentationOptions> completionHandler) =>
        completionHandler(Preferences.Default.Get(RemotePushRegistration.EnabledKey, false) ? UNNotificationPresentationOptions.Banner | UNNotificationPresentationOptions.Sound : UNNotificationPresentationOptions.None);

    public override void DidReceiveNotificationResponse(UNUserNotificationCenter center, UNNotificationResponse response, Action completionHandler)
    {
        if (Guid.TryParse(response.Notification.Request.Content.UserInfo["taskId"]?.ToString(), out var taskId))
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try { await IPlatformApplication.Current!.Services.GetRequiredService<RemotePushRegistration>().OpenTaskAsync(taskId); }
                catch (Exception) { /* A conta precisa estar conectada antes de abrir a tarefa. */ }
            });
        completionHandler();
    }
}
