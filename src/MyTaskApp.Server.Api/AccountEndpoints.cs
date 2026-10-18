using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using MyTaskApp.Server.Infrastructure.Persistence;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.Api;

/// <summary>
/// O login pelo navegador (ADR-061): o Google diz quem é a pessoa, e o
/// Identity abre uma sessão curta, só para o <c>/connect/authorize</c> emitir o
/// código do desktop. Nada do Google vira token desta API.
/// </summary>
internal static class AccountEndpoints
{
    public const string ExternalProvider = "Google";

    /// <summary>"true" quando o Google diz que o e-mail da conta é verificado (ADR-068).</summary>
    public const string EmailVerifiedClaim = "email_verified";

    public static void MapAccountEndpoints(this IEndpointRouteBuilder app)
    {
        var account = app.MapGroup("/account").RequireRateLimiting(ServerSetup.AuthLimiter).AllowAnonymous();

        account.MapGet("/login", Login);
        account.MapGet("/external-callback", ExternalCallbackAsync);
        account.MapGet("/logout", LogoutForm);
        account.MapPost("/logout", LogoutAsync);
    }

    /// <summary>Manda para o Google. A volta só aceita endereço deste servidor (sem redirect aberto).</summary>
    private static IResult Login(string? returnUrl, HttpContext context, SignInManager<ApplicationUser> signIn)
    {
        var safeReturn = IsLocal(returnUrl) ? returnUrl! : "/";
        var callback = $"/account/external-callback?returnUrl={Uri.EscapeDataString(safeReturn)}";
        var properties = signIn.ConfigureExternalAuthenticationProperties(ExternalProvider, callback);

        return Results.Challenge(properties, [ExternalProvider]);
    }

    /// <summary>
    /// A volta do Google: confere a lista de quem pode entrar (ADR-068), acha
    /// ou cria a conta pelo login externo, garante o workspace pessoal e abre a
    /// sessão. A identidade é o par (provedor, id) do Google; o e-mail só abre
    /// a lista se for verificado.
    /// </summary>
    private static async Task<IResult> ExternalCallbackAsync(
        string? returnUrl,
        HttpContext context,
        SignInManager<ApplicationUser> signIn,
        UserManager<ApplicationUser> users,
        WorkspaceProvisioner workspaces,
        SyncServerOptions options,
        TimeProvider timeProvider,
        ILoggerFactory loggers,
        CancellationToken cancellationToken)
    {
        var logger = loggers.CreateLogger("MyTaskApp.Server.Api.Account");
        var info = await signIn.GetExternalLoginInfoAsync();

        if (info is null)
        {
            return Results.Problem("O login não foi concluído. Volte ao MyTaskApp e tente de novo.", statusCode: StatusCodes.Status400BadRequest);
        }

        var email = info.Principal.FindFirstValue(ClaimTypes.Email);
        var emailVerified = bool.TryParse(info.Principal.FindFirstValue(EmailVerifiedClaim), out var verified) && verified;

        // Antes de achar ou criar: fora da lista, nem conta, nem workspace, nem sessão.
        if (!options.Registration.Allows([info.ProviderKey], email, emailVerified))
        {
            await context.SignOutAsync(IdentityConstants.ExternalScheme);
            logger.LogWarning("SyncRegistrationRefused {Provider} {Subject} {EmailVerified}", info.LoginProvider, info.ProviderKey, emailVerified);

            return Results.Problem(
                "Esta conta Google não tem acesso a este servidor. Peça a quem o administra para incluí-la.",
                statusCode: StatusCodes.Status403Forbidden,
                extensions: new Dictionary<string, object?> { ["code"] = SyncErrorCodes.RegistrationClosed });
        }

        var user = await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey);

        if (user is null)
        {
            user = new ApplicationUser
            {
                Id = Guid.CreateVersion7(),
                UserName = $"{info.LoginProvider.ToLowerInvariant()}-{Guid.NewGuid():N}",
                Email = email,
                EmailConfirmed = emailVerified,
                DisplayName = info.Principal.FindFirstValue(ClaimTypes.Name) ?? email ?? "Usuário",
                CreatedAt = timeProvider.GetUtcNow(),
            };

            var created = await users.CreateAsync(user);
            var linked = created.Succeeded ? await users.AddLoginAsync(user, info) : created;

            if (!linked.Succeeded)
            {
                logger.LogWarning("SyncAccountCreationFailed {Errors}", string.Join(",", linked.Errors.Select(error => error.Code)));
                return Results.Problem("Não foi possível criar a conta.", statusCode: StatusCodes.Status500InternalServerError);
            }

            logger.LogInformation("SyncAccountCreated {UserId}", user.Id);
        }
        else if (user.Email != email || user.EmailConfirmed != emailVerified)
        {
            // O /connect/token confere a lista pelo que está gravado: fica igual ao que o Google disse agora.
            user.Email = email;
            user.EmailConfirmed = emailVerified;
            await users.UpdateAsync(user);
        }

        await workspaces.EnsurePersonalAsync(user.Id, cancellationToken);
        await context.SignOutAsync(IdentityConstants.ExternalScheme);

        var safeReturn = IsLocal(returnUrl) ? returnUrl : "/";

        // Voltando para um /connect/authorize, a sessão leva a marca dele: só ele sai sem perguntar (ADR-074).
        if (ConnectEndpoints.FlowKeyOf(context.Request.PathBase, safeReturn) is { } flow)
        {
            await signIn.SignInWithClaimsAsync(user, isPersistent: false, [new Claim(ConnectEndpoints.AuthorizeFlowClaim, flow)]);
        }
        else
        {
            await signIn.SignInAsync(user, isPersistent: false);
        }

        return Results.LocalRedirect(safeReturn);
    }

    /// <summary>
    /// A conta que já existe continua na lista? Pelo que está gravado: o
    /// <c>sub</c> de cada login do Google e o e-mail, se verificado.
    /// </summary>
    internal static async Task<bool> IsAllowedAsync(UserManager<ApplicationUser> users, ApplicationUser user, RegistrationOptions registration)
    {
        var subjects = (await users.GetLoginsAsync(user))
            .Where(login => login.LoginProvider == ExternalProvider)
            .Select(login => login.ProviderKey);

        return registration.Allows(subjects, user.Email, user.EmailConfirmed);
    }

    /// <summary>Um formulário com o token antiforgery: sair também é uma ação que outro site não pode disparar.</summary>
    private static IResult LogoutForm(HttpContext context, IAntiforgery antiforgery, IOptions<AntiforgeryOptions> antiforgeryOptions)
    {
        if (RefuseWithoutHttps(context, antiforgeryOptions.Value, "Sair") is { } refused)
        {
            return refused;
        }

        var tokens = antiforgery.GetAndStoreTokens(context);

        // A página não carrega nada, só posta para este servidor, e nenhum site a põe num frame (SEC-009).
        context.Response.Headers.ContentSecurityPolicy = "default-src 'none'; form-action 'self'; frame-ancestors 'none'";
        context.Response.Headers.XFrameOptions = "DENY";

        return Results.Content(
            "<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><title>MyTaskApp</title></head><body>"
            + "<form method=\"post\" action=\"/account/logout\">"
            + $"<input type=\"hidden\" name=\"{tokens.FormFieldName}\" value=\"{System.Net.WebUtility.HtmlEncode(tokens.RequestToken)}\">"
            + "<button type=\"submit\">Sair</button></form></body></html>",
            "text/html; charset=utf-8");
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, IAntiforgery antiforgery, IOptions<AntiforgeryOptions> antiforgeryOptions)
    {
        if (RefuseWithoutHttps(context, antiforgeryOptions.Value, "Sair") is { } refused)
        {
            return refused;
        }

        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return Results.Problem("Pedido recusado.", statusCode: StatusCodes.Status400BadRequest);
        }

        await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Results.Content("Você saiu. Pode fechar esta aba.", "text/plain; charset=utf-8");
    }

    /// <summary>
    /// Só caminho deste servidor, pela regra do próprio <c>LocalRedirect</c>: <c>/x</c>, nunca <c>//x</c>,
    /// <c>/\x</c> nem caractere de controle, e sem espaço (SEC-013). O que não passa vira <c>/</c>.
    /// </summary>
    private static bool IsLocal([NotNullWhen(true)] string? url) =>
        RedirectHttpResult.IsLocalUrl(url) && !url.Any(char.IsWhiteSpace);

    /// <summary>
    /// Com o cookie do antiforgery só por HTTPS (fora dos testes), um pedido HTTP faria o antiforgery
    /// lançar: responde 400 dizendo o porquê (SEC-013). Em produção, o TLS e o redirect para HTTPS resolvem.
    /// </summary>
    internal static IResult? RefuseWithoutHttps(HttpContext context, AntiforgeryOptions antiforgery, string action) =>
        antiforgery.Cookie.SecurePolicy == CookieSecurePolicy.Always && !context.Request.IsHttps
            ? Results.Problem($"{action} exige HTTPS. Abra este endereço por https://.", statusCode: StatusCodes.Status400BadRequest)
            : null;
}
