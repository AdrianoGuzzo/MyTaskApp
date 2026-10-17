using Android.App;
using Android.Content.PM;

namespace MyTaskApp.Mobile;

[Activity(
    Theme = "@style/Maui.SplashTheme",
    MainLauncher = true,
    LaunchMode = LaunchMode.SingleTop,
    ConfigurationChanges = ConfigChanges.ScreenSize | ConfigChanges.Orientation | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density,
    WindowSoftInputMode = Android.Views.SoftInput.AdjustResize)]
public class MainActivity : MauiAppCompatActivity
{
}
