using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using MyTaskApp.Server.Infrastructure.Persistence;

namespace MyTaskApp.Server.Api;

/// <summary>
/// O login pelo navegador (ADR-061): o Google diz quem é a pessoa, e o
/// Identity abre uma sessão curta, só para o <c>/connect/authorize</c> emitir o
/// código do desktop. Nada do Google vira token desta API.
/// </summary>
internal static class AccountEndpoints
{
    public const string ExternalProvider = "Google";

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
    /// A volta do Google: acha ou cria a conta pelo login externo, garante o
    /// workspace pessoal e abre a sessão. O e-mail do Google é só exibição;
    /// a identidade é o par (provedor, id) do Google.
    /// </summary>
    private static async Task<IResult> ExternalCallbackAsync(
        string? returnUrl,
        HttpContext context,
        SignInManager<ApplicationUser> signIn,
        UserManager<ApplicationUser> users,
        WorkspaceProvisioner workspaces,
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

        var user = await users.FindByLoginAsync(info.LoginProvider, info.ProviderKey);

        if (user is null)
        {
            var email = info.Principal.FindFirstValue(ClaimTypes.Email);
            user = new ApplicationUser
            {
                Id = Guid.CreateVersion7(),
                UserName = $"{info.LoginProvider.ToLowerInvariant()}-{Guid.NewGuid():N}",
                Email = email,
                EmailConfirmed = email is not null,
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

        await workspaces.EnsurePersonalAsync(user.Id, cancellationToken);
        await context.SignOutAsync(IdentityConstants.ExternalScheme);
        await signIn.SignInAsync(user, isPersistent: false);

        return Results.LocalRedirect(IsLocal(returnUrl) ? returnUrl! : "/");
    }

    /// <summary>Um formulário com o token antiforgery: sair também é uma ação que outro site não pode disparar.</summary>
    private static IResult LogoutForm(HttpContext context, IAntiforgery antiforgery)
    {
        var tokens = antiforgery.GetAndStoreTokens(context);

        return Results.Content(
            "<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><title>MyTaskApp</title></head><body>"
            + "<form method=\"post\" action=\"/account/logout\">"
            + $"<input type=\"hidden\" name=\"{tokens.FormFieldName}\" value=\"{System.Net.WebUtility.HtmlEncode(tokens.RequestToken)}\">"
            + "<button type=\"submit\">Sair</button></form></body></html>",
            "text/html; charset=utf-8");
    }

    private static async Task<IResult> LogoutAsync(HttpContext context, IAntiforgery antiforgery)
    {
        if (!await antiforgery.IsRequestValidAsync(context))
        {
            return Results.Problem("Pedido recusado.", statusCode: StatusCodes.Status400BadRequest);
        }

        await context.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Results.Content("Você saiu. Pode fechar esta aba.", "text/plain; charset=utf-8");
    }

    /// <summary>Só caminho deste servidor: <c>/x</c>, nunca <c>//x</c> nem <c>/\x</c>.</summary>
    private static bool IsLocal([NotNullWhen(true)] string? url) =>
        !string.IsNullOrEmpty(url)
        && url[0] == '/'
        && (url.Length == 1 || (url[1] != '/' && url[1] != '\\'));
}
