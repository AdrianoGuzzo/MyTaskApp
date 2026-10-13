using System.Text;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.OAuth;
using MyTaskApp.Infrastructure.Secrets;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Infrastructure.Sync.Http;

/// <summary>
/// A conta da sincronização no desktop (ADR-060). Singleton: o access token
/// fica na memória, e a renovação passa por um portão só.
/// </summary>
/// <remarks>
/// <para>
/// <b>Login pelo navegador com PKCE, sem secret.</b> O desktop é cliente
/// público: o verificador nasce aqui e nunca sai deste processo, então um
/// código interceptado na volta pelo loopback não serve para ninguém.
/// </para>
/// <para>
/// <b>O refresh token gira a cada uso</b> (o servidor revoga a cadeia se o
/// antigo for reusado). Duas renovações em paralelo derrubariam a conexão; por
/// isso o portão, e o novo é gravado no cofre antes de o access ser usado.
/// </para>
/// </remarks>
internal sealed class SyncAuthenticationService(
    SyncOptions options,
    SyncHttp http,
    ISecretStore secrets,
    IDeviceIdentity device,
    TimeProvider timeProvider,
    ILogger<SyncAuthenticationService> logger) : ISyncAuthenticator, IDisposable
{
    public const string Scopes = "openid profile email offline_access sync";

    public static readonly LoopbackCallbackMessages CallbackMessages = new(
        PortInUse: "A porta {port} deste computador está ocupada, e o login da sincronização volta por ela. "
            + "Feche o programa que a usa e tente de novo.",
        Connected: "Pronto! O MyTaskApp está conectado à sua conta. Você já pode fechar esta aba e voltar ao app.",
        NotCompleted: "O login não foi concluído. Volte ao MyTaskApp para tentar de novo.",
        Denied: "O login foi recusado.",
        Failed: "O servidor de sincronização não concluiu o login. Tente de novo.",
        ReceivedLogEvent: "SyncAuthorizationCallbackReceived");

    private readonly SemaphoreSlim _gate = new(1, 1);

    private AccessToken? _access;

    private CancellationTokenSource? _pending;

    private LoopbackCallbackListener? _listener;

    public bool IsAvailable => options.IsAvailable;

    public Uri? ServerBaseUrl => options.BaseUri;

    public async Task<SyncAuthorization> BeginAuthorizationAsync(CancellationToken cancellationToken)
    {
        if (!options.IsAvailable)
        {
            throw new DomainException("Esta versão do MyTaskApp não tem um servidor de sincronização configurado.");
        }

        // Um clique novo desiste do anterior: a porta é uma só, e é solta aqui, na hora.
        var previous = _pending;
        _pending = null;

        if (previous is not null)
        {
            await previous.CancelAsync();
        }

        _listener?.StopListening();

        var pending = new CancellationTokenSource(options.AuthorizationTimeout);
        _pending = pending;

        var listener = LoopbackCallbackListener.Start(options.CallbackPort, CallbackMessages, logger);
        _listener = listener;

        var state = Pkce.NewSecret();
        var verifier = Pkce.NewSecret();

        logger.LogInformation("SyncAuthorizationStarted {Port}", listener.Port);

        return new SyncAuthorization(
            AuthorizeUrl(state, Pkce.ChallengeFor(verifier)),
            CompleteAsync(listener, state, verifier, pending, cancellationToken));
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_access is { } current && current.ExpiresAt > timeProvider.GetUtcNow())
            {
                return current.Value;
            }

            var refresh = await secrets.ReadAsync(SyncOptions.SecretName, cancellationToken)
                ?? throw new SyncApiException(SyncFailureKind.NeedsReconnect, "Conecte a conta de novo.");

            try
            {
                var renewed = await RequestTokensAsync(
                    new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = refresh,
                        ["client_id"] = SyncSchema.DesktopClientId,
                    },
                    cancellationToken);

                await StoreAsync(renewed, cancellationToken);
                return renewed.AccessToken;
            }
            catch (SyncApiException exception) when (exception.Kind == SyncFailureKind.NeedsReconnect)
            {
                // O refresh morreu (expirou, foi revogado, ou o dispositivo foi): não adianta guardar.
                await secrets.DeleteAsync(SyncOptions.SecretName, CancellationToken.None);
                _access = null;
                logger.LogWarning("SyncRefreshRejected");
                throw;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ForgetAccessToken() => _access = null;

    public async Task<bool> HasCredentialsAsync(CancellationToken cancellationToken) =>
        await secrets.ReadAsync(SyncOptions.SecretName, cancellationToken) is not null;

    public async Task SignOutAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (await secrets.ReadAsync(SyncOptions.SecretName, cancellationToken) is { } refresh && options.IsAvailable)
            {
                try
                {
                    using var _ = await http.PostFormAsync(
                        ApiRoutes.Revocation,
                        new Dictionary<string, string>
                        {
                            ["token"] = refresh,
                            ["token_type_hint"] = "refresh_token",
                            ["client_id"] = SyncSchema.DesktopClientId,
                        },
                        cancellationToken);
                }
                catch (SyncApiException exception)
                {
                    // Sem rede, o token só deixa de existir aqui; ele expira sozinho lá.
                    logger.LogWarning("SyncRevocationFailed {Kind}", exception.Kind);
                }
            }

            await secrets.DeleteAsync(SyncOptions.SecretName, cancellationToken);
            _access = null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _listener?.StopListening();
        _gate.Dispose();
    }

    private Uri AuthorizeUrl(string state, string challenge)
    {
        var query = new StringBuilder()
            .Append("client_id=").Append(Uri.EscapeDataString(SyncSchema.DesktopClientId))
            .Append("&response_type=code")
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(options.RedirectUri.ToString()))
            .Append("&scope=").Append(Uri.EscapeDataString(Scopes))
            .Append("&state=").Append(Uri.EscapeDataString(state))
            .Append("&code_challenge=").Append(Uri.EscapeDataString(challenge))
            .Append("&code_challenge_method=S256")
            .Append('&').Append(SyncSchema.DeviceIdClaim).Append('=').Append(device.DeviceId.ToString("D"))
            .Append('&').Append(SyncSchema.DeviceNameParameter).Append('=').Append(Uri.EscapeDataString(device.DefaultNickname));

        return new UriBuilder(new Uri(options.BaseUri!, ApiRoutes.Authorize.TrimStart('/'))) { Query = query.ToString() }.Uri;
    }

    private async Task CompleteAsync(
        LoopbackCallbackListener listener,
        string state,
        string verifier,
        CancellationTokenSource pending,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, pending.Token);

        try
        {
            var code = await listener.WaitForCodeAsync(state, linked.Token);

            var tokens = await RequestTokensAsync(
                new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = code,
                    ["redirect_uri"] = options.RedirectUri.ToString(),
                    ["client_id"] = SyncSchema.DesktopClientId,
                    ["code_verifier"] = verifier,
                },
                linked.Token);

            await _gate.WaitAsync(linked.Token);

            try
            {
                await StoreAsync(tokens, linked.Token);
            }
            finally
            {
                _gate.Release();
            }

            logger.LogInformation("SyncAuthorizationCompleted");
        }
        catch (AuthorizationRefusedException refused) when (refused.Error == SyncErrorCodes.DeviceRevoked)
        {
            throw new SyncApiException(SyncFailureKind.DeviceRevoked, "Este computador foi revogado na sua conta. Ele será registrado como um dispositivo novo.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested && _pending != pending)
        {
            throw new DomainException("O login foi substituído por outro.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new DomainException("O login não foi concluído a tempo. Tente de novo.");
        }
        finally
        {
            await listener.DisposeAsync();

            if (_pending == pending)
            {
                _pending = null;
            }
        }
    }

    private async Task<TokenResponse> RequestTokensAsync(Dictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var document = await http.PostFormAsync(ApiRoutes.Token, form, cancellationToken);
        var root = document.RootElement;

        return new TokenResponse(
            root.GetProperty("access_token").GetString()!,
            root.TryGetProperty("refresh_token", out var refresh) ? refresh.GetString() : null,
            root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 900);
    }

    /// <summary>O refresh vai para o cofre antes; o access, só na memória, com folga para não vencer no meio da chamada.</summary>
    private async Task StoreAsync(TokenResponse tokens, CancellationToken cancellationToken)
    {
        if (tokens.RefreshToken is { } refresh)
        {
            await secrets.WriteAsync(SyncOptions.SecretName, refresh, cancellationToken);
        }

        var lifetime = TimeSpan.FromSeconds(tokens.ExpiresIn);
        var safety = TimeSpan.FromSeconds(Math.Min(60, tokens.ExpiresIn / 4.0));

        _access = new AccessToken(tokens.AccessToken, timeProvider.GetUtcNow() + lifetime - safety);
    }

    private sealed record TokenResponse(string AccessToken, string? RefreshToken, int ExpiresIn);

    private sealed record AccessToken(string Value, DateTimeOffset ExpiresAt);
}
