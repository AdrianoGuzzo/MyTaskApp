using Android.App;
using Android.Content;
using Android.Content.PM;
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

    protected override void OnActivityResult(int requestCode, Result resultCode, Intent? data)
    {
        base.OnActivityResult(requestCode, resultCode, data);

        if (requestCode == ConfirmCredentialRequest)
        {
            _confirmation?.TrySetResult(resultCode == Result.Ok ? UnlockResult.Unlocked : UnlockResult.Canceled);
            _confirmation = null;
        }
    }
}
