using Android.App;
using Android.Content;
using Android.Gms.Extensions;
using Firebase;
using Firebase.Messaging;
using MyTaskApp.Mobile.Core.Ports;
namespace MyTaskApp.Mobile;

public static class FcmPush
{
    public static async Task EnableAsync(bool requestPermission = true)
    {
        if (FirebaseApp.InitializeApp(Platform.AppContext) is null)
            throw new InvalidOperationException("Esta versão ainda não inclui a configuração das notificações Android.");
        if (requestPermission && !await IPlatformApplication.Current!.Services.GetRequiredService<ILocalNotifier>().RequestPermissionAsync())
            throw new InvalidOperationException("Permita as notificações nas configurações do Android.");
        AndroidNotifier.EnsureChannel(Platform.AppContext);
        var token = await FirebaseMessaging.Instance.GetToken().AsAsync<Java.Lang.String>();
        Preferences.Default.Set(RemotePushRegistration.AddressKey, token.ToString());
        Preferences.Default.Set(RemotePushRegistration.EnabledKey, true);
        await IPlatformApplication.Current!.Services.GetRequiredService<RemotePushRegistration>().RegisterSavedAsync();
    }
}

[Service(Exported = false)]
[IntentFilter(["com.google.firebase.MESSAGING_EVENT"])]
public sealed class FcmMessagingService : FirebaseMessagingService
{
    public override void OnNewToken(string token)
    {
        base.OnNewToken(token);
        Preferences.Default.Set(RemotePushRegistration.AddressKey, token);
        _ = RegisterAsync();
    }
    public override void OnMessageReceived(RemoteMessage message)
    {
        base.OnMessageReceived(message);
        if (message.Data is { Count: > 0 } data) _ = ReceiveAsync(data);
    }
    private static async Task RegisterAsync()
    {
        try
        {
            if (IPlatformApplication.Current?.Services is { } services)
                await services.GetRequiredService<RemotePushRegistration>().RegisterSavedAsync();
        }
        catch (Exception) { Preferences.Default.Set("push.status", "Registro de notificações pendente. Abra o app com rede."); }
    }
    private static async Task ReceiveAsync(IDictionary<string, string> data)
    {
        try
        {
            if (IPlatformApplication.Current?.Services is { } services)
                await services.GetRequiredService<RemotePushRegistration>().ReceiveAsync(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(data));
        }
        catch (Exception) { /* Um payload inválido não altera o estado das tarefas. */ }
    }
}
