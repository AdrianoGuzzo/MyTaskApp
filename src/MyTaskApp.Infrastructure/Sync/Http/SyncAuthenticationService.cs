using System.Text;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.OAuth;
using MyTaskApp.Infrastructure.Secrets;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Infrastructure.Sync.Http;

/// <summary>
/// A conta da sincronização (ADR-061). Singleton: o access token fica na
/// memória, e a renovação passa por um portão só. Desktop e celular usam a
/// mesma; muda só por onde o login volta (<see cref="ISyncAuthorizationRedirect"/>, ADR-067).
/// </summary>
/// <remarks>
/// <para>
/// <b>Login pelo navegador com PKCE, sem secret.</b> O desktop é cliente
/// público: o verificador nasce aqui e nunca sai deste processo, então um
/// código interceptado na volta (loopback ou esquema do app) não serve para ninguém.
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
    ILogger<SyncAuthenticationService> logger,
    ISyncAuthorizationRedirect? redirect = null) : ISyncAuthenticator, IDisposable
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

    /// <summary>Entre o servidor e o refresh no cofre. Espaço: nem a URL normalizada (vira <c>%20</c>) nem o token o têm.</summary>
    private const string BoundSeparator = " ";

    private readonly SemaphoreSlim _gate = new(1, 1);

    private AccessToken? _access;

    private CancellationTokenSource? _pending;

    private ISyncAuthorizationSession? _listener;

    public bool IsAvailable => options.IsAvailable;

    public Uri? ServerBaseUrl => options.BaseUri;

    /// <summary>Sem uma volta registrada pelo app, a do desktop: o loopback.</summary>
    private ISyncAuthorizationRedirect Redirect => redirect ??= new LoopbackSyncAuthorizationRedirect(options, logger);

    public async Task<SyncAuthorization> BeginAuthorizationAsync(bool includeRemote, CancellationToken cancellationToken)
    {
        if (!options.IsAvailable)
        {
            throw new DomainException("Nenhum servidor de sincronização configurado.");
        }

        // Um clique novo desiste do anterior: a porta (ou a volta do app) é uma só, e é solta aqui, na hora.
        var previous = _pending;
        _pending = null;

        if (previous is not null)
        {
            await previous.CancelAsync();
        }

        _listener?.Close();

        var pending = new CancellationTokenSource(options.AuthorizationTimeout);
        _pending = pending;

        var listener = Redirect.Open();
        _listener = listener;

        var state = Pkce.NewSecret();
        var verifier = Pkce.NewSecret();

        logger.LogInformation("SyncAuthorizationStarted {Client} {RedirectUri}", Redirect.ClientId, listener.RedirectUri);

        return new SyncAuthorization(
            AuthorizeUrl(listener.RedirectUri, state, Pkce.ChallengeFor(verifier), includeRemote),
            CompleteAsync(listener, state, verifier, pending, cancellationToken));
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_access is { } current && current.ExpiresAt > timeProvider.GetUtcNow() && SyncServerUrl.SameServer(current.Server, options.BaseUri))
            {
                return current.Value;
            }

            var refresh = await ReadRefreshAsync(cancellationToken)
                ?? throw new SyncApiException(SyncFailureKind.NeedsReconnect, "Conecte a conta de novo.");

            try
            {
                var renewed = await RequestTokensAsync(
                    new Dictionary<string, string>
                    {
                        ["grant_type"] = "refresh_token",
                        ["refresh_token"] = refresh,
                        ["client_id"] = Redirect.ClientId,
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
            if (options.IsAvailable)
            {
                // O servidor revoga só o token pedido: o refresh sozinho deixaria o access valendo até vencer (SEC-010).
                if (_access is { } access && access.ExpiresAt > timeProvider.GetUtcNow() && SyncServerUrl.SameServer(access.Server, options.BaseUri))
                {
                    await RevokeAsync(access.Value, "access_token", cancellationToken);
                }

                if (await ReadRefreshAsync(cancellationToken) is { } refresh)
                {
                    await RevokeAsync(refresh, "refresh_token", cancellationToken);
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
        _listener?.Close();
        _gate.Dispose();
    }

    private Uri AuthorizeUrl(Uri redirectUri, string state, string challenge, bool includeRemote)
    {
        var scopes = includeRemote ? $"{Scopes} {RemoteSchema.Scope}" : Scopes;

        var query = new StringBuilder()
            .Append("client_id=").Append(Uri.EscapeDataString(Redirect.ClientId))
            .Append("&response_type=code")
            .Append("&redirect_uri=").Append(Uri.EscapeDataString(redirectUri.ToString()))
            .Append("&scope=").Append(Uri.EscapeDataString(scopes))
            .Append("&state=").Append(Uri.EscapeDataString(state))
            .Append("&code_challenge=").Append(Uri.EscapeDataString(challenge))
            .Append("&code_challenge_method=S256")
            .Append('&').Append(SyncSchema.DeviceIdClaim).Append('=').Append(device.DeviceId.ToString("D"))
            .Append('&').Append(SyncSchema.DeviceNameParameter).Append('=').Append(Uri.EscapeDataString(device.DefaultNickname));

        return new UriBuilder(new Uri(options.BaseUri!, ApiRoutes.Authorize.TrimStart('/'))) { Query = query.ToString() }.Uri;
    }

    private async Task CompleteAsync(
        ISyncAuthorizationSession listener,
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
                    ["redirect_uri"] = listener.RedirectUri.ToString(),
                    ["client_id"] = Redirect.ClientId,
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
        catch (AuthorizationRefusedException refused) when (refused.Error == "invalid_scope")
        {
            // O único escopo que pode faltar é o remote: o servidor é anterior à orquestração (ADR-087).
            throw new DomainException(
                "O servidor de sincronização ainda não tem o controle remoto. Atualize o servidor, "
                + "ou desligue o controle remoto em Máquinas para conectar só a sincronização.");
        }
        catch (AuthorizationRefusedException refused) when (refused.Error == SyncErrorCodes.DeviceLimitReached)
        {
            // Este aparelho ainda não entrou, então não lista nem revoga: quem faz isso é um computador já conectado.
            throw new DomainException(
                "Sua conta chegou ao limite de dispositivos ativos. Num computador já conectado, abra Sincronização, "
                + "revogue um dispositivo que você não usa mais e conecte de novo.");
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

    private async Task RevokeAsync(string token, string hint, CancellationToken cancellationToken)
    {
        try
        {
            using var _ = await http.PostFormAsync(
                ApiRoutes.Revocation,
                new Dictionary<string, string>
                {
                    ["token"] = token,
                    ["token_type_hint"] = hint,
                    ["client_id"] = Redirect.ClientId,
                },
                cancellationToken);
        }
        catch (SyncApiException exception)
        {
            // Sem rede, o token só deixa de existir aqui; ele expira sozinho lá.
            logger.LogWarning("SyncRevocationFailed {Kind}", exception.Kind);
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
        var server = options.BaseUri ?? throw new SyncApiException(SyncFailureKind.NeedsReconnect, "Sincronização sem servidor configurado.");

        if (tokens.RefreshToken is { } refresh)
        {
            await secrets.WriteAsync(SyncOptions.SecretName, server.AbsoluteUri + BoundSeparator + refresh, cancellationToken);
        }

        var lifetime = TimeSpan.FromSeconds(tokens.ExpiresIn);
        var safety = TimeSpan.FromSeconds(Math.Min(60, tokens.ExpiresIn / 4.0));

        _access = new AccessToken(tokens.AccessToken, timeProvider.GetUtcNow() + lifetime - safety, server);
    }

    /// <summary>
    /// O refresh token, só se for do servidor configurado agora (ADR-078): o
    /// cofre guarda junto o endereço que o emitiu, e um token de outro
    /// servidor nunca sai deste aparelho para o novo. O valor antigo, sem
    /// endereço, vale até a próxima renovação, que já o grava amarrado.
    /// </summary>
    private async Task<string?> ReadRefreshAsync(CancellationToken cancellationToken)
    {
        if (await secrets.ReadAsync(SyncOptions.SecretName, cancellationToken) is not { } stored)
        {
            return null;
        }

        var separator = stored.IndexOf(BoundSeparator, StringComparison.Ordinal);

        if (separator < 0)
        {
            return stored;
        }

        if (Uri.TryCreate(stored[..separator], UriKind.Absolute, out var issuedBy) && SyncServerUrl.SameServer(issuedBy, options.BaseUri))
        {
            return stored[(separator + BoundSeparator.Length)..];
        }

        logger.LogWarning("SyncRefreshFromAnotherServer");
        return null;
    }

    private sealed record TokenResponse(string AccessToken, string? RefreshToken, int ExpiresIn);

    private sealed record AccessToken(string Value, DateTimeOffset ExpiresAt, Uri Server);
}
