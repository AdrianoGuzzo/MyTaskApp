using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using MyTaskApp.Mobile.Core.Ports;

namespace MyTaskApp.Mobile;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
    WindowSoftInputMode = Android.Views.SoftInput.AdjustResize)]
public class MainActivity : MauiAppCompatActivity
{
    private const int ConfirmCredentialRequest = 4201;

    private TaskCompletionSource<UnlockResult>? _confirmation;

    /// <summary>A tela do bloqueio do aparelho (Android 9 e anteriores), para o <c>MauiDeviceUnlock</c>.</summary>
    internal Task<UnlockResult> ConfirmCredentialAsync(Intent intent)
    {
        _confirmation?.TrySetResult(UnlockResult.Canceled);
        _confirmation = new TaskCompletionSource<UnlockResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartActivityForResult(intent, ConfirmCredentialRequest);
        return _confirmation.Task;
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        Follow(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        Follow(intent);
    }

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode == ConfirmCredentialRequest)
        {
            _confirmation?.TrySetResult(resultCode == Result.Ok ? UnlockResult.Unlocked : UnlockResult.Canceled);
            _confirmation = null;
        }
    }

    /// <summary>
    /// O toque no aviso do controle remoto (ADR-096) abre a aba Remoto. Esta
    /// tela é a de entrada, que qualquer app pode chamar: só essa rota vale.
    /// </summary>
    private static void Follow(Intent? intent)
    {
        if (intent?.GetStringExtra(AndroidNotifier.RouteExtra) != "//remote")
        {
            return;
        }

        intent.RemoveExtra(AndroidNotifier.RouteExtra);
        MainThread.BeginInvokeOnMainThread(async () =>
        {
            if (Shell.Current is { } shell)
            {
                await shell.GoToAsync("//remote");
            }
        });
    }
}
