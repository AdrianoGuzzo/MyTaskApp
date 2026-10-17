using Android.App;
using Android.Content;
using Android.Content.PM;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Mobile;

/// <summary>
/// A volta do login pelo esquema do app (ADR-067): o Custom Tabs abre
/// <c>io.github.adrianoguzzo.mytaskapp://oauth/callback?code=…</c>, o Android
/// entrega aqui, e o <c>WebAuthenticator</c> devolve os parâmetros ao app.
/// </summary>
[Activity(NoHistory = true, LaunchMode = LaunchMode.SingleTop, Exported = true)]
[IntentFilter(
    [Intent.ActionView],
    Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = SyncSchema.MobileRedirectScheme,
    DataHost = "oauth",
    DataPath = "/callback")]
public class OAuthCallbackActivity : WebAuthenticatorCallbackActivity
{
}
