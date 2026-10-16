using MyTaskApp.Infrastructure.Sync.Http;
using MyTaskApp.Mobile.Core.Ports;

namespace MyTaskApp.Mobile.Core.SignIn;

/// <summary>
/// Abre o login no navegador do sistema e entrega a volta ao
/// <see cref="AppLinkAuthorizationRedirect"/> (ADR-065). Quem espera o código é
/// o mesmo <c>SyncAuthenticationService</c> do desktop.
/// </summary>
public sealed class AppLinkSignIn(IWebAuthenticatorPort browser, AppLinkAuthorizationRedirect redirect)
{
    /// <summary>Fechar o navegador ou não conseguir abri-lo cancela o login que está esperando.</summary>
    public async Task RunAsync(Uri authorizeUrl, CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, string>? result;

        try
        {
            result = await browser.AuthenticateAsync(authorizeUrl, redirect.RedirectUri, cancellationToken);
        }
        catch
        {
            redirect.Abandon();
            throw;
        }

        if (result is null)
        {
            redirect.Abandon();
        }
        else
        {
            redirect.Deliver(result);
        }
    }
}
