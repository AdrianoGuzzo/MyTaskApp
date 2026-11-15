using System.Text.Json;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Mobile;

public sealed class RemotePushRegistration(MobileLifecycle lifecycle, IRemotePushApi api, ISyncAccount account, INavigator navigator, IUseCaseRunner runner) : IRemotePushState
{
    public bool IsRegistered => Preferences.Default.Get(EnabledKey, false) && Preferences.Default.Get("push.registered", false);
    public const string AddressKey = "push.address";
    public const string EnabledKey = "push.enabled";
    private static readonly SemaphoreSlim Gate = new(1, 1);
    public static string Status => Preferences.Default.Get("push.status", "Avisos imediatos não configurados.");

    public async Task RegisterSavedAsync()
    {
        await lifecycle.PrepareAsync();
        if (!account.IsAvailable || !Preferences.Default.Get(EnabledKey, false)) return;
        if (!(await runner.RunAsync<IRemoteSettingsStore, RemoteSettings>((store, token) => store.GetAsync(token), CancellationToken.None)).Enabled)
        {
            await DisableAsync();
            Preferences.Default.Set("push.status", "Ligue o controle remoto antes de configurar os avisos.");
            return;
        }
        var address = Preferences.Default.Get(AddressKey, "");
        if (address.Length == 0) return;
#if ANDROID
        var subscription = new RemotePushSubscription(RemotePushProvider.Fcm, address);
#else
        var subscription = new RemotePushSubscription(RemotePushProvider.Apns, address);
#endif
        Preferences.Default.Set("push.registered", false);
        await api.RegisterAsync(subscription, CancellationToken.None);
        Preferences.Default.Set("push.registered", true);
        Preferences.Default.Set("push.status", "Avisos imediatos configurados.");
    }

    public async Task ReceiveAsync(byte[] payload)
    {
        if (!Preferences.Default.Get(EnabledKey, false)) return;
        using var json = JsonDocument.Parse(payload);
        var eventId = json.RootElement.GetProperty("eventId").GetGuid();
        var taskId = json.RootElement.GetProperty("taskId").GetGuid();
        await lifecycle.PrepareAsync();
        if (!account.IsAvailable) return;
        await Gate.WaitAsync();
        try
        {
            var seen = Preferences.Default.Get("push.seen", "").Split(',', StringSplitOptions.RemoveEmptyEntries).ToList();
            if (seen.Contains(eventId.ToString("N"))) return;
            seen.Add(eventId.ToString("N"));
            Preferences.Default.Set("push.seen", string.Join(',', seen.TakeLast(256)));
            IPlatformApplication.Current!.Services.GetRequiredService<ILocalNotifier>().Show(new LocalNotice("event:" + eventId.ToString("N"), "MyTaskApp",
                json.RootElement.GetProperty("text").GetString() ?? "Novidade no terminal remoto", taskId));
        }
        finally { Gate.Release(); }
    }

    public async Task OpenTaskAsync(Guid taskId)
    {
        await lifecycle.StartAsync();
        if (account.IsAvailable) await MainThread.InvokeOnMainThreadAsync(() => navigator.OpenTaskAsync(taskId));
    }

    public async Task DisableAsync()
    {
        Preferences.Default.Set(EnabledKey, false);
        Preferences.Default.Set("push.registered", false);
        await api.RemoveAsync(CancellationToken.None);
        Preferences.Default.Remove(AddressKey);
        Preferences.Default.Set("push.status", "Avisos imediatos desativados.");
    }
}
