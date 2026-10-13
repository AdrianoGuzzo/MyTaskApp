using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MyTaskApp.Server.Application;
using MyTaskApp.Server.Infrastructure.Persistence;
using MyTaskApp.Sync.Contracts;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyTaskApp.Server.Api;

/// <summary>
/// O <c>/connect/authorize</c> e o <c>/connect/token</c> em passthrough
/// (ADR-060): o OpenIddict valida o protocolo (cliente, redirect exato, PKCE),
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
    /// Sem sessão, manda entrar (pelo Google) e volta. Com sessão, registra o
    /// dispositivo do parâmetro <c>device_id</c>, troca a autorização dele —
    /// a anterior é revogada — e emite o código com a claim do dispositivo.
    /// </summary>
    private static async Task<IResult> AuthorizeAsync(
        HttpContext context,
        UserManager<ApplicationUser> users,
        ServerDbContext database,
        IOpenIddictApplicationManager applications,
        IOpenIddictAuthorizationManager authorizations,
        IOpenIddictTokenManager tokens,
        WorkspaceProvisioner workspaces,
        DeviceGate gate,
        TimeProvider timeProvider,
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

        var now = timeProvider.GetUtcNow();

        if (device is null)
        {
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

        var application = await applications.FindByClientIdAsync(request.ClientId!, cancellationToken)
            ?? throw new InvalidOperationException("Cliente OAuth desconhecido.");

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
            Scopes.OpenId, Scopes.Profile, Scopes.Email, Scopes.OfflineAccess, SyncSchema.Scope,
        ]));

        // Uma autorização por dispositivo: entrar de novo revoga a anterior, e revogar o dispositivo revoga esta.
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
    /// se confere que a conta e o dispositivo continuam valendo.
    /// </summary>
    private static async Task<IResult> TokenAsync(
        HttpContext context,
        UserManager<ApplicationUser> users,
        DeviceGate gate,
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

        if (await users.FindByIdAsync(userId.ToString()) is null)
        {
            return Forbid(Errors.InvalidGrant, "A conta não existe mais.");
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
