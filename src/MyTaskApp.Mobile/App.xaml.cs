using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Adapters;
using MyTaskApp.Mobile.Core.Composition;

namespace MyTaskApp.Mobile;

/// <summary>
/// O app no celular (ADR-067). A janela liga o ciclo de vida: o banco sobe ao
/// abrir, a sincronização para em segundo plano e volta ao reabrir — e também
/// quando a rede volta.
/// </summary>
public partial class App
{
    private readonly AppShell _shell;

    private readonly MobileLifecycle _lifecycle;

    private readonly ISyncAccount _account;

    private readonly MauiThemeService _themes;
    private readonly RemotePushRegistration _push;

    public App(AppShell shell, MobileLifecycle lifecycle, ISyncAccount account, MauiThemeService themes, RemotePushRegistration push)
    {
        InitializeComponent();

        // As cores entram antes da primeira tela: os estilos pedem por DynamicResource.
        themes.Attach(this);
        _themes = themes;
        _shell = shell;
        _lifecycle = lifecycle;
        _account = account;
        _push = push;
    }

    protected override Window CreateWindow(IActivationState? activationState)
    {
        var window = new Window(_shell) { Title = "MyTaskApp" };

        window.Created += (_, _) =>
        {
            // Antes da janela não há barra de status para pintar.
            _themes.Refresh();
            _ = _lifecycle.StartAsync();
            _ = RefreshPushAsync();
            Connectivity.Current.ConnectivityChanged += OnConnectivityChanged;
        };
        window.Stopped += (_, _) => _ = _lifecycle.SleepAsync();
        window.Resumed += (_, _) => { _lifecycle.Resume(); _ = RefreshPushAsync(); };
        window.Destroying += (_, _) => Connectivity.Current.ConnectivityChanged -= OnConnectivityChanged;

        return window;
    }

    private async Task RefreshPushAsync()
    {
        try
        {
#if ANDROID
            if (Preferences.Default.Get(RemotePushRegistration.EnabledKey, false)) await FcmPush.EnableAsync(requestPermission: false);
#else
            await _push.RegisterSavedAsync();
#endif
        }
        catch (Exception) { /* O registro será tentado novamente ao abrir a aba Remoto. */ }
    }

    private void OnConnectivityChanged(object? sender, ConnectivityChangedEventArgs e)
    {
        // A rede voltou: o que ficou guardado vai já, sem esperar o próximo minuto.
        if (e.NetworkAccess == NetworkAccess.Internet)
        {
            _account.SyncNow();
        }
    }
}
