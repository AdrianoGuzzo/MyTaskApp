using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.ViewModels;

namespace MyTaskApp.Mobile.Views;

public partial class RemotePage
{
    private readonly RemoteViewModel _viewModel;

    private readonly MobileLifecycle _lifecycle;

    public RemotePage(RemoteViewModel viewModel, MobileLifecycle lifecycle)
    {
        InitializeComponent();
        BindingContext = _viewModel = viewModel;
        _lifecycle = lifecycle;
    }

    private async void ConfigurePush(object? sender, EventArgs e)
    {
        try
        {
#if ANDROID
            await FcmPush.EnableAsync();
#elif IOS
            await IosPush.EnableAsync();
#endif
        }
        catch (Exception exception) { await DisplayAlertAsync("Avisos imediatos", exception.Message, "OK"); }
        PushStatus.Text = RemotePushRegistration.Status;
    }
    private async void DisablePush(object? sender, EventArgs e)
    {
        try
        {
            await IPlatformApplication.Current!.Services.GetRequiredService<RemotePushRegistration>().DisableAsync();
        }
        catch (Exception) { await DisplayAlertAsync("Avisos imediatos", "Abra o app com rede para concluir a remoção do registro no servidor.", "OK"); }
        PushStatus.Text = RemotePushRegistration.Status;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();

        try
        {
            await _lifecycle.StartAsync();
        }
        catch (Exception)
        {
            // A aba Tarefas mostra o erro; aqui, o remoto só não carrega.
            return;
        }

        await _viewModel.LoadAsync();
#if ANDROID
        if (_viewModel.IsReady && (!Preferences.Default.ContainsKey(RemotePushRegistration.EnabledKey) || Preferences.Default.Get(RemotePushRegistration.EnabledKey, false)))
        {
            try { await FcmPush.EnableAsync(requestPermission: !Preferences.Default.ContainsKey(RemotePushRegistration.EnabledKey)); }
            catch (Exception) { Preferences.Default.Set("push.status", "Ative as notificações para receber avisos com o app fechado."); }
        }
#endif
        try { await IPlatformApplication.Current!.Services.GetRequiredService<RemotePushRegistration>().RegisterSavedAsync(); }
        catch (Exception) { Preferences.Default.Set("push.status", "Registro de push pendente; confira o servidor e a conexão."); }
        PushStatus.Text = RemotePushRegistration.Status;
    }
}
