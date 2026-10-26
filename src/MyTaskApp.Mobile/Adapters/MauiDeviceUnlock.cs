using MyTaskApp.Mobile.Core.Ports;
#if ANDROID
using Android.App;
using Android.Content;
using Android.Hardware.Biometrics;
using Android.OS;
#elif IOS
using LocalAuthentication;
#endif

namespace MyTaskApp.Mobile.Adapters;

/// <summary>
/// O destravamento antes de assinar (E7): a biometria ou, sem ela, o PIN, o
/// padrão ou a senha do aparelho. No Android 10+, o <c>BiometricPrompt</c>
/// aceita os dois; antes, a tela de confirmação do bloqueio. No iOS, o
/// <c>LAContext</c> com a senha como alternativa.
/// </summary>
internal sealed class MauiDeviceUnlock : IDeviceUnlock
{
    public const string Title = "MyTaskApp";

    public Task<UnlockResult> UnlockAsync(string reason, CancellationToken cancellationToken) =>
        MainThread.InvokeOnMainThreadAsync(() => UnlockOnMainThreadAsync(reason, cancellationToken));

#if ANDROID
    private static Task<UnlockResult> UnlockOnMainThreadAsync(string reason, CancellationToken cancellationToken)
    {
        if (Platform.CurrentActivity is not MainActivity activity
            || activity.GetSystemService(Context.KeyguardService) is not KeyguardManager keyguard)
        {
            return Task.FromResult(UnlockResult.Canceled);
        }

        if (!keyguard.IsDeviceSecure)
        {
            return Task.FromResult(UnlockResult.NotAvailable);
        }

        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            return PromptAsync(activity, reason, cancellationToken);
        }

#pragma warning disable CA1422 // A alternativa do Android 9 e anteriores, onde o BiometricPrompt não aceita o PIN.
        var intent = keyguard.CreateConfirmDeviceCredentialIntent(Title, reason);
#pragma warning restore CA1422

        return intent is null ? Task.FromResult(UnlockResult.NotAvailable) : activity.ConfirmCredentialAsync(intent);
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
    private static Task<UnlockResult> PromptAsync(Activity activity, string reason, CancellationToken cancellationToken)
    {
        var builder = new BiometricPrompt.Builder(activity).SetTitle(Title).SetSubtitle(reason);

        if (OperatingSystem.IsAndroidVersionAtLeast(30))
        {
            builder.SetAllowedAuthenticators((int)(BiometricManagerAuthenticators.BiometricWeak | BiometricManagerAuthenticators.DeviceCredential));
        }
        else
        {
#pragma warning disable CA1422 // No Android 10, o jeito de aceitar o PIN.
            builder.SetDeviceCredentialAllowed(true);
#pragma warning restore CA1422
        }

        var answer = new TaskCompletionSource<UnlockResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancel = new CancellationSignal();
        cancellationToken.Register(cancel.Cancel);
        builder.Build().Authenticate(cancel, activity.MainExecutor!, new Callback(answer));
        return answer.Task;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android29.0")]
    private sealed class Callback(TaskCompletionSource<UnlockResult> answer) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result) =>
            answer.TrySetResult(UnlockResult.Unlocked);

        /// <summary>Terminal: cancelou, errou demais ou não há como (o "falhou" de cada tentativa não chega aqui).</summary>
        public override void OnAuthenticationError(BiometricErrorCode errorCode, Java.Lang.ICharSequence? errString) =>
            answer.TrySetResult(errorCode is BiometricErrorCode.NoDeviceCredential or BiometricErrorCode.HwNotPresent
                ? UnlockResult.NotAvailable
                : UnlockResult.Canceled);
    }
#elif IOS
    private static async Task<UnlockResult> UnlockOnMainThreadAsync(string reason, CancellationToken cancellationToken)
    {
        using var context = new LAContext();

        if (!context.CanEvaluatePolicy(LAPolicy.DeviceOwnerAuthentication, out _))
        {
            return UnlockResult.NotAvailable;
        }

        using var registration = cancellationToken.Register(context.Invalidate);
        var (unlocked, _) = await context.EvaluatePolicyAsync(LAPolicy.DeviceOwnerAuthentication, reason);
        return unlocked ? UnlockResult.Unlocked : UnlockResult.Canceled;
    }
#else
    private static Task<UnlockResult> UnlockOnMainThreadAsync(string reason, CancellationToken cancellationToken) =>
        Task.FromResult(UnlockResult.NotAvailable);
#endif
}
