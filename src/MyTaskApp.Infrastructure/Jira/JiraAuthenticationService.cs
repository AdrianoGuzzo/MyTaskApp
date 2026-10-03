using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.External;
using MyTaskApp.Application.External.Jira;
using MyTaskApp.Domain;
using MyTaskApp.Domain.External;
using MyTaskApp.Infrastructure.Secrets;

namespace MyTaskApp.Infrastructure.Jira;

/// <summary>Onde e com que credencial falar com o Jira agora. Vive só na memória.</summary>
internal sealed record JiraAccess(Uri ApiBase, AuthenticationHeaderValue Authorization, Uri SiteUrl, string? DefaultProject);

/// <summary>
/// O acesso pronto para uma chamada à API. Quem pede não sabe se é OAuth ou
/// API token, e não vê o token — só o cabeçalho, que vai direto para o request.
/// </summary>
internal interface IJiraAccess
{
    /// <summary>Sem conexão, <see cref="ExternalTaskUnavailableException"/>; nunca devolve acesso vencido.</summary>
    Task<JiraAccess> GetAccessAsync(CancellationToken cancellationToken);

    /// <summary>A API recusou o token: o próximo pedido renova antes de usar.</summary>
    void ForgetAccessToken();
}

/// <summary>
/// A autenticação com o Jira Cloud (ADR-045): OAuth 2.0 (3LO) como caminho
/// principal, e-mail + API token como avançado. Singleton — guarda o access
/// token na memória e serializa a renovação.
/// </summary>
/// <remarks>
/// <para>
/// <b>Onde fica cada coisa.</b> O refresh token (ou o API token) vai para o
/// <see cref="ISecretStore"/>, cifrado pelo DPAPI. Site, conta e projeto vão
/// para o <c>jira.json</c>, que não tem segredo nenhum. O access token só
/// existe na memória: dura uma hora e se renova sozinho.
/// </para>
/// <para>
/// <b>Renovação.</b> A Atlassian gira o refresh token a cada uso — o antigo
/// morre quando o novo nasce. Duas renovações em paralelo fariam a segunda
/// usar um token já morto e derrubar a conexão; por isso passam pelo mesmo
/// <see cref="SemaphoreSlim"/>, e o novo é gravado antes de o access ser usado.
/// </para>
/// </remarks>
internal sealed class JiraAuthenticationService(
    JiraOptions options,
    JiraHttp http,
    JiraOAuthClient oauth,
    ISecretStore secrets,
    JiraConnectionFile file,
    TimeProvider timeProvider,
    ILogger<JiraAuthenticationService> logger) : IJiraAuthenticationService, IJiraAccess, IDisposable
{
    public const string SecretName = "jira";

    private readonly SemaphoreSlim _gate = new(1, 1);

    private StoredJiraConnection? _state;

    private JiraSecret? _secret;

    private bool _loaded;

    private JiraTokens? _access;

    private CancellationTokenSource? _pendingAuthorization;

    public async Task<JiraConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            await EnsureLoadedAsync(cancellationToken);
            return Describe();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<JiraAuthorization> BeginAuthorizationAsync(CancellationToken cancellationToken = default)
    {
        if (!options.IsOAuthAvailable)
        {
            throw new DomainException(
                "Esta versão do MyTaskApp não tem o login do Jira configurado. Use \"Conectar com API token\".");
        }

        // Um clique novo desiste do anterior: a porta é uma só.
        if (_pendingAuthorization is { } previous)
        {
            await previous.CancelAsync();
        }

        var pending = new CancellationTokenSource(options.AuthorizationTimeout);
        _pendingAuthorization = pending;

        var listener = OAuthCallbackListener.Start(options.CallbackPort, logger);
        var state = JiraOAuthClient.NewSecret();
        var verifier = JiraOAuthClient.NewSecret();

        logger.LogInformation("JiraAuthorizationStarted {Port}", listener.Port);

        return new JiraAuthorization(
            oauth.AuthorizeUrl(state, JiraOAuthClient.ChallengeFor(verifier)),
            CompleteAuthorizationAsync(listener, state, verifier, pending, cancellationToken));
    }

    public async Task<JiraConnection> ChooseSiteAsync(string siteId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            await EnsureLoadedAsync(cancellationToken);

            var site = _state?.PendingSites?.FirstOrDefault(candidate => candidate.Id == siteId)
                ?? throw new DomainException("Esse site não está entre os que você autorizou. Conecte de novo.");

            var token = await AccessTokenLockedAsync(cancellationToken);
            await ConnectSiteLockedAsync(site, token, cancellationToken);

            return Describe();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<JiraConnection> ConnectWithApiTokenAsync(
        string siteUrl,
        string email,
        string apiToken,
        CancellationToken cancellationToken = default)
    {
        var site = NormalizeSite(siteUrl)
            ?? throw new DomainException("Informe o endereço do seu Jira, como empresa.atlassian.net.");

        var trimmedEmail = email?.Trim() ?? string.Empty;
        var trimmedToken = apiToken?.Trim() ?? string.Empty;

        if (trimmedEmail.Length == 0 || !trimmedEmail.Contains('@', StringComparison.Ordinal))
        {
            throw new DomainException("Informe o e-mail da sua conta Atlassian.");
        }

        if (trimmedToken.Length == 0)
        {
            throw new DomainException("Cole o API token criado na sua conta Atlassian.");
        }

        var authorization = Basic(trimmedEmail, trimmedToken);
        var me = await MyselfAsync(site, authorization, onUnauthorized: "O Jira não aceitou esse e-mail e token. Confira e tente de novo.", cancellationToken);

        await _gate.WaitAsync(cancellationToken);

        try
        {
            await EnsureLoadedAsync(cancellationToken);

            var state = new StoredJiraConnection
            {
                Method = JiraAuthMethod.ApiToken,
                SiteName = site.Host,
                SiteUrl = site.ToString().TrimEnd('/'),
                AccountName = me.Name,
                AccountEmail = me.Email ?? trimmedEmail,
                DefaultProject = SameSite(site) ? _state?.DefaultProject : null,
            };

            await SaveLockedAsync(state, new JiraSecret(ApiToken: trimmedToken, Email: trimmedEmail), cancellationToken);
            _access = null;

            logger.LogInformation("JiraConnected {Method} {Site}", state.Method, state.SiteName);
            return Describe();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<JiraConnection> TestAsync(CancellationToken cancellationToken = default)
    {
        var access = await AccessForUserAsync(cancellationToken);
        var me = await MyselfAsync(
            access.ApiBase,
            access.Authorization,
            onUnauthorized: "O Jira recusou a conexão. Desconecte e conecte de novo.",
            cancellationToken);

        await _gate.WaitAsync(cancellationToken);

        try
        {
            if (_state is { } state)
            {
                await file.SaveAsync(state with { AccountName = me.Name, AccountEmail = me.Email ?? state.AccountEmail }, cancellationToken);
                _state = await file.LoadAsync(cancellationToken);
            }

            return Describe();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<JiraProject>> ListProjectsAsync(CancellationToken cancellationToken = default)
    {
        var access = await AccessForUserAsync(cancellationToken);

        try
        {
            using var document = await http.GetAsync(
                new Uri(access.ApiBase, "rest/api/3/project/search?maxResults=100&orderBy=name"),
                access.Authorization,
                cancellationToken);

            return document.RootElement.GetProperty("values")
                .EnumerateArray()
                .Select(project => new JiraProject(
                    project.GetProperty("key").GetString()!,
                    project.TryGetProperty("name", out var name) ? name.GetString() ?? string.Empty : string.Empty))
                .ToList();
        }
        catch (JiraHttpException exception)
        {
            throw Explain(exception, "O Jira recusou a lista de projetos. Desconecte e conecte de novo.");
        }
    }

    public async Task<JiraConnection> SetDefaultProjectAsync(string? projectKey, CancellationToken cancellationToken = default)
    {
        var key = string.IsNullOrWhiteSpace(projectKey) ? null : projectKey.Trim().ToUpperInvariant();

        if (key is not null && !IssueKey.TryParse($"{key}-1", out _))
        {
            throw new DomainException($"\"{projectKey}\" não é a chave de um projeto do Jira.");
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            await EnsureLoadedAsync(cancellationToken);

            var state = _state is { IsChoosingSite: false } connected
                ? connected
                : throw new DomainException("Conecte o Jira antes de escolher o projeto padrão.");

            await file.SaveAsync(state with { DefaultProject = key }, cancellationToken);
            _state = state with { DefaultProject = key };

            return Describe();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_pendingAuthorization is { } pending)
        {
            await pending.CancelAsync();
        }

        await _gate.WaitAsync(cancellationToken);

        try
        {
            await secrets.DeleteAsync(SecretName, cancellationToken);
            file.Delete();

            _state = null;
            _secret = null;
            _access = null;
            _loaded = true;

            logger.LogInformation("JiraDisconnected");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<JiraAccess> GetAccessAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            await EnsureLoadedAsync(cancellationToken);

            if (_state is not { IsChoosingSite: false, SiteUrl: { } siteUrl } state)
            {
                throw new ExternalTaskUnavailableException(ExternalTaskFailure.NotConnected);
            }

            if (_secret is null)
            {
                // O jira.json existe, o segredo não decifra: outro usuário ou
                // outra máquina. É preciso conectar de novo.
                throw new ExternalTaskUnavailableException(ExternalTaskFailure.Unauthorized);
            }

            var site = new Uri(siteUrl + "/");

            if (state.Method == JiraAuthMethod.ApiToken)
            {
                return new JiraAccess(site, Basic(_secret.Email!, _secret.ApiToken!), site, state.DefaultProject);
            }

            var token = await AccessTokenLockedAsync(cancellationToken);

            return new JiraAccess(
                JiraOAuthClient.ApiBaseFor(state.SiteId!),
                JiraOAuthClient.Bearer(token),
                site,
                state.DefaultProject);
        }
        finally
        {
            _gate.Release();
        }
    }

    public void ForgetAccessToken() => _access = null;

    public void Dispose()
    {
        // Fechar o app desiste de uma autorização em andamento e solta a porta.
        _pendingAuthorization?.Cancel();
        _gate.Dispose();
    }

    /// <summary>
    /// <c>empresa</c>, <c>empresa.atlassian.net</c> ou o link inteiro de uma
    /// issue: o que o usuário colar vira <c>https://empresa.atlassian.net</c>.
    /// </summary>
    internal static Uri? NormalizeSite(string? input)
    {
        var text = input?.Trim().TrimEnd('/') ?? string.Empty;

        if (text.Length == 0)
        {
            return null;
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = text.Contains('.', StringComparison.Ordinal) ? $"https://{text}" : $"https://{text}.atlassian.net";
        }

        return Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && uri.Host.Length > 0
            ? new Uri($"https://{uri.Authority}/")
            : null;
    }

    private async Task<JiraConnection> CompleteAuthorizationAsync(
        OAuthCallbackListener listener,
        string state,
        string verifier,
        CancellationTokenSource pending,
        CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(pending.Token, cancellationToken);

        try
        {
            string code;

            await using (listener)
            {
                code = await listener.WaitForCodeAsync(state, linked.Token);
            }

            var tokens = await oauth.ExchangeCodeAsync(code, verifier, linked.Token);

            if (tokens.RefreshToken is null)
            {
                throw new DomainException("O Jira não liberou o acesso contínuo. Tente conectar de novo.");
            }

            var sites = await oauth.ListSitesAsync(tokens.AccessToken, linked.Token);

            if (sites.Count == 0)
            {
                throw new DomainException("Sua conta não tem acesso a nenhum site do Jira.");
            }

            await _gate.WaitAsync(linked.Token);

            try
            {
                await EnsureLoadedAsync(linked.Token);
                _access = tokens;

                if (sites.Count == 1)
                {
                    _secret = new JiraSecret(RefreshToken: tokens.RefreshToken);
                    await secrets.WriteAsync(SecretName, JsonSerializer.Serialize(_secret, JiraHttp.Json), linked.Token);
                    await ConnectSiteLockedAsync(sites[0], tokens.AccessToken, linked.Token);
                }
                else
                {
                    await SaveLockedAsync(
                        new StoredJiraConnection { Method = JiraAuthMethod.OAuth, PendingSites = sites },
                        new JiraSecret(RefreshToken: tokens.RefreshToken),
                        linked.Token);
                }

                logger.LogInformation("JiraAuthorized {Sites}", sites.Count);
                return Describe();
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (pending.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new DomainException(
                pending.Token.IsCancellationRequested && _pendingAuthorization != pending
                    ? "A autorização anterior foi substituída por uma nova."
                    : "A autorização não foi concluída a tempo. Clique em \"Conectar ao Jira\" de novo.");
        }
        catch (JiraHttpException exception)
        {
            throw Explain(exception, "O Jira recusou a autorização. Tente conectar de novo.");
        }
        finally
        {
            if (_pendingAuthorization == pending)
            {
                _pendingAuthorization = null;
            }

            pending.Dispose();
        }
    }

    /// <summary>Com o site escolhido: quem é o usuário ali, e a conexão gravada.</summary>
    private async Task ConnectSiteLockedAsync(JiraSite site, string accessToken, CancellationToken cancellationToken)
    {
        var me = await MyselfAsync(
            JiraOAuthClient.ApiBaseFor(site.Id),
            JiraOAuthClient.Bearer(accessToken),
            onUnauthorized: "O Jira recusou o acesso a esse site. Confira as permissões da sua conta.",
            cancellationToken);

        var state = new StoredJiraConnection
        {
            Method = JiraAuthMethod.OAuth,
            SiteId = site.Id,
            SiteName = site.Name,
            SiteUrl = site.Url,
            AccountName = me.Name,
            AccountEmail = me.Email,
            DefaultProject = _state is { SiteId: { } previous } && previous == site.Id ? _state.DefaultProject : null,
        };

        await file.SaveAsync(state, cancellationToken);
        _state = state;

        logger.LogInformation("JiraConnected {Method} {Site}", state.Method, state.SiteName);
    }

    /// <summary>O access token do OAuth, renovado se precisar. Chamado com o portão fechado.</summary>
    private async Task<string> AccessTokenLockedAsync(CancellationToken cancellationToken)
    {
        if (_access is { } current && current.ExpiresAt > timeProvider.GetUtcNow())
        {
            return current.AccessToken;
        }

        if (_secret?.RefreshToken is not { } refreshToken)
        {
            throw new ExternalTaskUnavailableException(ExternalTaskFailure.Unauthorized);
        }

        if (!options.IsOAuthAvailable)
        {
            // Conectado por OAuth numa build que não tem o app: não há como renovar.
            throw new ExternalTaskUnavailableException(ExternalTaskFailure.Unauthorized);
        }

        JiraTokens tokens;

        try
        {
            tokens = await oauth.RefreshAsync(refreshToken, cancellationToken);
        }
        catch (JiraHttpException exception) when (exception.Failure is JiraHttpFailure.BadRequest
            or JiraHttpFailure.Unauthorized or JiraHttpFailure.Forbidden)
        {
            logger.LogWarning("JiraRefreshRejected {Status}", exception.Status);
            throw new ExternalTaskUnavailableException(ExternalTaskFailure.Unauthorized);
        }
        catch (JiraHttpException)
        {
            throw new ExternalTaskUnavailableException(ExternalTaskFailure.Unavailable);
        }

        // Gravado antes de usar: o refresh antigo já morreu do lado da Atlassian.
        _secret = _secret with { RefreshToken = tokens.RefreshToken ?? refreshToken };
        await secrets.WriteAsync(SecretName, JsonSerializer.Serialize(_secret, JiraHttp.Json), cancellationToken);
        _access = tokens;

        return tokens.AccessToken;
    }

    /// <summary>O acesso para uma ação que o usuário pediu: a falha vira mensagem, e não silêncio.</summary>
    private async Task<JiraAccess> AccessForUserAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await GetAccessAsync(cancellationToken);
        }
        catch (ExternalTaskUnavailableException exception)
        {
            throw new DomainException(exception.Message);
        }
    }

    private async Task<(string? Name, string? Email)> MyselfAsync(
        Uri apiBase,
        AuthenticationHeaderValue authorization,
        string onUnauthorized,
        CancellationToken cancellationToken)
    {
        try
        {
            using var document = await http.GetAsync(new Uri(apiBase, "rest/api/3/myself"), authorization, cancellationToken);
            var root = document.RootElement;

            return (
                root.TryGetProperty("displayName", out var name) ? name.GetString() : null,
                root.TryGetProperty("emailAddress", out var email) ? email.GetString() : null);
        }
        catch (JiraHttpException exception)
        {
            throw Explain(exception, onUnauthorized);
        }
    }

    private async Task SaveLockedAsync(StoredJiraConnection state, JiraSecret secret, CancellationToken cancellationToken)
    {
        // O segredo primeiro: um jira.json sem segredo é "conecte de novo"; um
        // segredo sem jira.json é lixo inofensivo que a próxima conexão sobrescreve.
        await secrets.WriteAsync(SecretName, JsonSerializer.Serialize(secret, JiraHttp.Json), cancellationToken);
        await file.SaveAsync(state, cancellationToken);

        _secret = secret;
        _state = state;
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_loaded)
        {
            return;
        }

        _state = await file.LoadAsync(cancellationToken);

        if (_state is not null && await secrets.ReadAsync(SecretName, cancellationToken) is { } stored)
        {
            try
            {
                _secret = JsonSerializer.Deserialize<JiraSecret>(stored, JiraHttp.Json);
            }
            catch (JsonException)
            {
                _secret = null;
            }
        }

        _loaded = true;
    }

    private bool SameSite(Uri site) =>
        _state?.SiteUrl is { } current && string.Equals(current.TrimEnd('/'), site.ToString().TrimEnd('/'), StringComparison.OrdinalIgnoreCase);

    private JiraConnection Describe()
    {
        if (_state is not { } state)
        {
            return JiraConnection.Disconnected(options.IsOAuthAvailable);
        }

        if (state.IsChoosingSite)
        {
            return new JiraConnection(JiraConnectionState.ChoosingSite, state.Method, Sites: state.PendingSites)
            {
                IsOAuthAvailable = options.IsOAuthAvailable,
            };
        }

        return new JiraConnection(
            JiraConnectionState.Connected,
            state.Method,
            state.SiteName,
            state.SiteUrl,
            state.AccountName,
            state.AccountEmail,
            state.DefaultProject)
        {
            IsOAuthAvailable = options.IsOAuthAvailable,
        };
    }

    private static AuthenticationHeaderValue Basic(string email, string token) =>
        new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{token}")));

    private static DomainException Explain(JiraHttpException exception, string onUnauthorized) =>
        new(exception.Failure switch
        {
            JiraHttpFailure.Unauthorized or JiraHttpFailure.Forbidden => onUnauthorized,
            JiraHttpFailure.Network => "Não foi possível falar com o Jira. Confira o endereço e a sua conexão.",
            JiraHttpFailure.Timeout => "O Jira demorou demais para responder. Tente de novo.",
            JiraHttpFailure.NotFound => "Esse endereço não parece ser de um Jira. Confira e tente de novo.",
            JiraHttpFailure.RateLimited => "O Jira pediu para esperar um pouco. Tente de novo em instantes.",
            _ => "O Jira respondeu com um erro. Tente de novo em instantes.",
        });
}

/// <summary>O que vai para o cofre: o refresh token do OAuth, ou o API token e o e-mail.</summary>
internal sealed record JiraSecret(string? RefreshToken = null, string? ApiToken = null, string? Email = null);
