using System.Buffers.Text;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Application;
using MyTaskApp.Server.Infrastructure.Persistence;
using MyTaskApp.Sync.Contracts;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyTaskApp.Server.Api;

/// <summary>
/// O <c>/connect/authorize</c> e o <c>/connect/token</c> em passthrough
/// (ADR-061): o OpenIddict valida o protocolo (cliente, redirect exato, PKCE),
/// e aqui entra o que é do MyTaskApp — a sessão, o dispositivo e a revogação.
/// </summary>
internal static class ConnectEndpoints
{
    public static void MapConnectEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapMethods("/connect/authorize", ["GET", "POST"], AuthorizeAsync)
            .RequireRateLimiting(ServerSetup.AuthLimiter)
            .AllowAnonymous();

        app.MapPost("/connect/token", TokenAsync)
            .RequireRateLimiting(ServerSetup.AuthLimiter)
            .AllowAnonymous();
    }

    /// <summary>
    /// Na sessão aberta pelo login que este pedido começou: a chave do fluxo
    /// (<see cref="FlowKey(string?, string?, string?, string?, string?)"/>). Vale uma vez (ADR-074).
    /// </summary>
    public const string AuthorizeFlowClaim = "mytaskapp_authorize_flow";

    /// <summary>O botão da tela de consentimento: <see cref="ConsentAccept"/> ou qualquer outra coisa, que cancela.</summary>
    private const string ConsentField = "consent";

    private const string ConsentAccept = "accept";

    /// <summary>
    /// Sem sessão, manda entrar (pelo Google) e volta. Com sessão, registra o
    /// dispositivo do parâmetro <c>device_id</c>, troca a autorização dele —
    /// a anterior é revogada — e emite o código com a claim do dispositivo.
    /// </summary>
    /// <remarks>
    /// Sem pergunta, só logo depois do login que este mesmo pedido começou. Uma
    /// sessão que sobrou atende qualquer navegação, até a que veio de outro site,
    /// então nela a pessoa confirma numa tela, com antiforgery (SEC-018, ADR-074).
    /// </remarks>
    private static async Task<IResult> AuthorizeAsync(
        HttpContext context,
        UserManager<ApplicationUser> users,
        ServerDbContext database,
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        IOpenIddictTokenManager tokens,
        WorkspaceProvisioner workspaces,
        DeviceGate gate,
        SyncServerOptions options,
        TimeProvider timeProvider,
        IAntiforgery antiforgery,
        IOptions<AntiforgeryOptions> antiforgeryOptions,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("O pedido OpenID Connect não foi lido.");

        var session = await context.AuthenticateAsync(IdentityConstants.ApplicationScheme);

        if (!session.Succeeded || await users.GetUserAsync(session.Principal) is not { } user)
        {
            return Results.Challenge(
                new AuthenticationProperties { RedirectUri = context.Request.PathBase + context.Request.Path + context.Request.QueryString },
                [IdentityConstants.ApplicationScheme]);
        }

        // A sessão vive dez minutos: a lista pode ter mudado desde o callback (ADR-068).
        if (!await AccountEndpoints.IsAllowedAsync(users, user, options.Registration))
        {
            await context.SignOutAsync(IdentityConstants.ApplicationScheme);
            return Forbid(Errors.AccessDenied, "Esta conta Google não tem acesso a este servidor.");
        }

        if (!Guid.TryParse((string?)request[SyncSchema.DeviceIdClaim], out var deviceId) || deviceId == Guid.Empty)
        {
            return Forbid(Errors.InvalidRequest, "O pedido precisa do device_id do aplicativo.");
        }

        var device = await database.Devices.FindAsync([user.Id, deviceId], cancellationToken);

        // Código próprio, e não access_denied: o desktop entende que precisa de um id novo.
        if (device is { RevokedAt: not null })
        {
            return Forbid(SyncErrorCodes.DeviceRevoked, "Este dispositivo foi revogado. Conecte de novo como um dispositivo novo.");
        }

        var application = await applications.FindByClientIdAsync(request.ClientId!, cancellationToken)
            ?? throw new InvalidOperationException("Cliente OAuth desconhecido.");

        var logger = loggers.CreateLogger("MyTaskApp.Server.Api.Connect");
        var answer = HttpMethods.IsPost(context.Request.Method) ? (string?)request[ConsentField] : null;

        if (answer is not null)
        {
            if (AccountEndpoints.RefuseWithoutHttps(context, antiforgeryOptions.Value, "Confirmar a conexão") is { } refused)
            {
                return refused;
            }

            if (!await antiforgery.IsRequestValidAsync(context))
            {
                return Results.Problem("Pedido recusado.", statusCode: StatusCodes.Status400BadRequest);
            }

            if (answer != ConsentAccept)
            {
                logger.LogInformation("SyncAuthorizeConsentDenied {Client} {DeviceId}", request.ClientId, deviceId);
                return Forbid(Errors.AccessDenied, "A conexão foi cancelada.");
            }

            logger.LogInformation("SyncAuthorizeConsentGiven {Client} {DeviceId}", request.ClientId, deviceId);
        }
        else if (session.Principal.Identity is ClaimsIdentity sessionIdentity
            && sessionIdentity.FindFirst(AuthorizeFlowClaim) is { } flow
            && flow.Value == FlowKey(request))
        {
            // Gasta a marca, com o mesmo vencimento: reabrir este endereço já pede a confirmação.
            sessionIdentity.RemoveClaim(flow);
            await context.SignInAsync(IdentityConstants.ApplicationScheme, session.Principal, session.Properties);
        }
        else if (request.HasPromptValue(PromptValues.None))
        {
            return Forbid(Errors.ConsentRequired, "Confirme a conexão no navegador.");
        }
        else
        {
            if (AccountEndpoints.RefuseWithoutHttps(context, antiforgeryOptions.Value, "Confirmar a conexão") is { } refused)
            {
                return refused;
            }

            logger.LogInformation("SyncAuthorizeConsentShown {Client} {DeviceId}", request.ClientId, deviceId);

            var nickname = device?.Nickname
                ?? DeviceUseCases.CleanNickname((string?)request[SyncSchema.DeviceNameParameter])
                ?? DeviceUseCases.DefaultNickname;

            return ConsentPage(context, request, antiforgery, user, await applications.GetDisplayNameAsync(application, cancellationToken), nickname);
        }

        var now = timeProvider.GetUtcNow();

        if (device is null)
        {
            // Só o dispositivo novo conta; entrar de novo num que já existe passa sempre.
            if (options.MaxActiveDevicesPerUser > 0
                && await database.Devices.CountAsync(row => row.UserId == user.Id && row.RevokedAt == null, cancellationToken) >= options.MaxActiveDevicesPerUser)
            {
                return Forbid(
                    SyncErrorCodes.DeviceLimitReached,
                    $"A conta chegou ao teto de {options.MaxActiveDevicesPerUser} dispositivos ativos. Revogue um que não usa mais e conecte de novo.");
            }

            device = new DeviceRow
            {
                UserId = user.Id,
                DeviceId = deviceId,
                Nickname = DeviceUseCases.CleanNickname((string?)request[SyncSchema.DeviceNameParameter]) ?? DeviceUseCases.DefaultNickname,
                RegisteredAt = now,
            };
            database.Devices.Add(device);
        }

        device.LastSeenAt = now;

        await workspaces.EnsurePersonalAsync(user.Id, cancellationToken);

        var identity = new ClaimsIdentity(
            authenticationType: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme,
            nameType: Claims.Name,
            roleType: Claims.Role);

        identity.SetClaim(Claims.Subject, user.Id.ToString())
            .SetClaim(Claims.Name, user.DisplayName)
            .SetClaim(Claims.Email, user.Email)
            .SetClaim(SyncSchema.DeviceIdClaim, deviceId.ToString());

        identity.SetScopes(request.GetScopes().Intersect(
        [
            Scopes.OpenId, Scopes.Profile, Scopes.Email, Scopes.OfflineAccess, SyncSchema.Scope, RemoteSchema.Scope,
        ]));

        // Uma autorização por dispositivo: entrar de novo revoga a anterior, e revogar o dispositivo revoga o refresh desta.
        var authorization = await authorizations.CreateAsync(
            identity,
            user.Id.ToString(),
            (await applications.GetIdAsync(application, cancellationToken))!,
            AuthorizationTypes.Permanent,
            identity.GetScopes(),
            cancellationToken);

        if (device.CurrentAuthorizationId is { } previous && await authorizations.FindByIdAsync(previous, cancellationToken) is { } old)
        {
            await authorizations.TryRevokeAsync(old, cancellationToken);
            await tokens.RevokeByAuthorizationIdAsync(previous, cancellationToken);
        }

        device.CurrentAuthorizationId = await authorizations.GetIdAsync(authorization, cancellationToken);
        await database.SaveChangesAsync(cancellationToken);
        gate.Forget(user.Id, deviceId);

        identity.SetAuthorizationId(device.CurrentAuthorizationId);
        identity.SetDestinations(Destinations);

        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// A troca do código e a renovação. O OpenIddict já conferiu PKCE,
    /// redirect e a cadeia do refresh token (reuso revoga a cadeia); aqui só
    /// se confere que a conta (ainda na lista, ADR-068) e o dispositivo
    /// continuam valendo.
    /// </summary>
    private static async Task<IResult> TokenAsync(
        HttpContext context,
        UserManager<ApplicationUser> users,
        DeviceGate gate,
        SyncServerOptions options,
        CancellationToken cancellationToken)
    {
        var request = context.GetOpenIddictServerRequest()
            ?? throw new InvalidOperationException("O pedido OpenID Connect não foi lido.");

        if (!request.IsAuthorizationCodeGrantType() && !request.IsRefreshTokenGrantType())
        {
            return Forbid(Errors.UnsupportedGrantType, "Só authorization_code e refresh_token.");
        }

        var result = await context.AuthenticateAsync(OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);

        if (result.Principal is not { } principal
            || !Guid.TryParse(principal.GetClaim(Claims.Subject), out var userId)
            || !Guid.TryParse(principal.GetClaim(SyncSchema.DeviceIdClaim), out var deviceId))
        {
            return Forbid(Errors.InvalidGrant, "Autorização inválida.");
        }

        if (await users.FindByIdAsync(userId.ToString()) is not { } user)
        {
            return Forbid(Errors.InvalidGrant, "A conta não existe mais.");
        }

        if (!await AccountEndpoints.IsAllowedAsync(users, user, options.Registration))
        {
            return Forbid(Errors.InvalidGrant, "Esta conta Google não tem acesso a este servidor.");
        }

        if (!await gate.IsActiveAsync(new SyncCaller(userId, deviceId), cancellationToken))
        {
            return Forbid(Errors.InvalidGrant, "Este dispositivo foi revogado.");
        }

        var identity = new ClaimsIdentity(principal.Claims, OpenIddictServerAspNetCoreDefaults.AuthenticationScheme, Claims.Name, Claims.Role);
        identity.SetDestinations(Destinations);

        return Results.SignIn(new ClaimsPrincipal(identity), authenticationScheme: OpenIddictServerAspNetCoreDefaults.AuthenticationScheme);
    }

    /// <summary>
    /// A chave de um pedido de autorização: o que o cliente sorteou para este
    /// login (<c>state</c> e <c>code_challenge</c>), para quem e para qual
    /// dispositivo. Outro pedido, mesmo do mesmo app, tem outra chave.
    /// </summary>
    internal static string FlowKey(string? clientId, string? redirectUri, string? state, string? codeChallenge, string? deviceId) =>
        Base64Url.EncodeToString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', clientId, redirectUri, state, codeChallenge, deviceId))));

    /// <summary>
    /// A chave do pedido para o qual o login volta, se o <paramref name="returnUrl"/>
    /// (já conferido como local) é o <c>/connect/authorize</c>.
    /// </summary>
    internal static string? FlowKeyOf(PathString pathBase, string returnUrl)
    {
        var split = returnUrl.IndexOf('?', StringComparison.Ordinal);

        if (split < 0 || !string.Equals(returnUrl[..split], pathBase + ApiRoutes.Authorize, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var query = QueryHelpers.ParseQuery(returnUrl[split..]);

        return FlowKey(query["client_id"], query["redirect_uri"], query["state"], query["code_challenge"], query[SyncSchema.DeviceIdClaim]);
    }

    private static string FlowKey(OpenIddictRequest request) =>
        FlowKey(request.ClientId, request.RedirectUri, request.State, request.CodeChallenge, (string?)request[SyncSchema.DeviceIdClaim]);

    /// <summary>
    /// "Conectar este dispositivo à sua conta?": o pedido inteiro volta num POST
    /// para cá, com o antiforgery. Nenhum site a põe num frame, e o formulário só
    /// vai para este servidor e, no 302 que vem depois, para a volta do cliente.
    /// </summary>
    private static IResult ConsentPage(
        HttpContext context,
        OpenIddictRequest request,
        IAntiforgery antiforgery,
        ApplicationUser user,
        string? clientName,
        string deviceName)
    {
        var form = antiforgery.GetAndStoreTokens(context);
        var html = new StringBuilder()
            .Append("<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\">")
            .Append("<meta name=\"viewport\" content=\"width=device-width\"><title>MyTaskApp</title></head><body>")
            .Append("<p>Conectar <strong>").Append(WebUtility.HtmlEncode(deviceName)).Append("</strong> (")
            .Append(WebUtility.HtmlEncode(clientName ?? request.ClientId)).Append(") à conta <strong>")
            .Append(WebUtility.HtmlEncode(user.DisplayName)).Append("</strong>")
            .Append(user.Email is { } email ? $" ({WebUtility.HtmlEncode(email)})" : string.Empty).Append("?</p>")
            .Append("<p>Se você não acabou de pedir isso no MyTaskApp, cancele.</p>")
            .Append("<form method=\"post\" action=\"").Append(WebUtility.HtmlEncode(context.Request.PathBase + ApiRoutes.Authorize)).Append("\">");

        foreach (var (name, value) in request.GetParameters())
        {
            if (name is ConsentField || name == form.FormFieldName)
            {
                continue;
            }

            foreach (var item in (StringValues)value)
            {
                html.Append("<input type=\"hidden\" name=\"").Append(WebUtility.HtmlEncode(name))
                    .Append("\" value=\"").Append(WebUtility.HtmlEncode(item)).Append("\">");
            }
        }

        html.Append("<input type=\"hidden\" name=\"").Append(form.FormFieldName)
            .Append("\" value=\"").Append(WebUtility.HtmlEncode(form.RequestToken)).Append("\">")
            .Append("<button type=\"submit\" name=\"").Append(ConsentField).Append("\" value=\"").Append(ConsentAccept).Append("\">Conectar</button> ")
            .Append("<button type=\"submit\" name=\"").Append(ConsentField).Append("\" value=\"deny\">Cancelar</button>")
            .Append("</form></body></html>");

        // O Chrome confere o form-action também no redirect depois do POST: a volta (já validada pelo OpenIddict) entra.
        var redirect = new Uri(request.RedirectUri!);
        var target = redirect.Scheme is "http" or "https" ? redirect.GetLeftPart(UriPartial.Authority) : redirect.Scheme + ":";

        context.Response.Headers.ContentSecurityPolicy = $"default-src 'none'; form-action 'self' {target}; frame-ancestors 'none'";
        context.Response.Headers.XFrameOptions = "DENY";

        return Results.Content(html.ToString(), "text/html; charset=utf-8");
    }

    /// <summary>
    /// O access token leva só o necessário para a API: quem (sub) e de qual
    /// dispositivo. Nome e e-mail, só no id_token, e só com o escopo pedido.
    /// </summary>
    private static IEnumerable<string> Destinations(Claim claim) => claim.Type switch
    {
        Claims.Subject => [OpenIddictConstants.Destinations.AccessToken, OpenIddictConstants.Destinations.IdentityToken],
        SyncSchema.DeviceIdClaim => [OpenIddictConstants.Destinations.AccessToken],
        Claims.Name when claim.Subject?.HasScope(Scopes.Profile) == true => [OpenIddictConstants.Destinations.IdentityToken],
        Claims.Email when claim.Subject?.HasScope(Scopes.Email) == true => [OpenIddictConstants.Destinations.IdentityToken],
        _ => [],
    };

    private static IResult Forbid(string error, string description) => Results.Forbid(
        new AuthenticationProperties(new Dictionary<string, string?>
        {
            [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
            [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description,
        }),
        [OpenIddictServerAspNetCoreDefaults.AuthenticationScheme]);
}
