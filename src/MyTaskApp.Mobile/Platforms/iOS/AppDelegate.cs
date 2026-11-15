using Foundation;

namespace MyTaskApp.Mobile;

[Register("AppDelegate")]
public class AppDelegate : MauiUIApplicationDelegate
{
    private readonly IosPushDelegate _push = new();
    protected override MauiApp CreateMauiApp() => MauiProgram.CreateMauiApp();
    public override bool FinishedLaunching(UIKit.UIApplication application, NSDictionary? launchOptions)
    {
        var launched = base.FinishedLaunching(application, launchOptions);
        UserNotifications.UNUserNotificationCenter.Current.Delegate = _push;
#if REMOTE_PUSH
        if (Preferences.Default.Get(RemotePushRegistration.EnabledKey, false)) application.RegisterForRemoteNotifications();
#endif
        return launched;
    }
    [Export("application:didRegisterForRemoteNotificationsWithDeviceToken:")]
    public void RegisteredForRemoteNotifications(UIKit.UIApplication application, NSData deviceToken) => _ = IosPush.RegisteredAsync(deviceToken);
    [Export("application:didFailToRegisterForRemoteNotificationsWithError:")]
    public void FailedToRegisterForRemoteNotifications(UIKit.UIApplication application, NSError error) =>
        Preferences.Default.Set("push.status", "A Apple recusou o registro. Confira a assinatura e a capacidade Push Notifications.");
}
