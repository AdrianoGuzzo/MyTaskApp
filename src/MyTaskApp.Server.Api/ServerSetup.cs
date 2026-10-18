using System.Net;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using MyTaskApp.Server.Application;
using MyTaskApp.Server.Infrastructure;
using MyTaskApp.Server.Infrastructure.Persistence;
using MyTaskApp.Sync.Contracts;
using OpenIddict.Abstractions;
using OpenIddict.Validation.AspNetCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyTaskApp.Server.Api;

/// <summary>A montagem do servidor, separada do <c>Program</c> para os testes subirem a mesma.</summary>
public static class ServerSetup
{
    public const string TestingEnvironment = "Testing";

    public const string SyncPolicy = "sync";

    public const string AuthLimiter = "auth";

    public const string SyncLimiter = "sync";

    public const string ApiLimiter = "api";

    public static IServiceCollection AddSyncServer(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var options = configuration.GetSection(SyncServerOptions.SectionName).Get<SyncServerOptions>() ?? new SyncServerOptions();
        var google = configuration.GetSection(GoogleOptions.SectionName).Get<GoogleOptions>() ?? new GoogleOptions();
        var connectionString = configuration.GetConnectionString("Sync")
            ?? throw new InvalidOperationException("Falta ConnectionStrings:Sync (variável ConnectionStrings__Sync).");
        var testing = environment.IsEnvironment(TestingEnvironment);

        services.AddSingleton(options);
        services.AddSingleton(google);
        services.AddSingleton(options.Pull);
        services.AddMemoryCache();
        services.AddProblemDetails();
        services.AddExceptionHandler<SyncExceptionHandler>();
        services.ConfigureHttpJsonOptions(json => SyncJson.Configure(json.SerializerOptions));

        services.AddServerApplication();
        services.AddServerInfrastructure(connectionString);
        services.AddScoped<IAuthorizationRevoker, OpenIddictAuthorizationRevoker>();
        services.AddScoped<DeviceGate>();
        services.AddHostedService<PublicClientSeeder>();
        services.AddHostedService<ProcessedOperationsPruner>();
        services.AddHostedService<DevelopmentLoopbackGuard>();

        services.AddDataProtection()
            .SetApplicationName("MyTaskApp.Sync")
            .PersistKeysToDbContext<ServerDbContext>();

        AddAccounts(services, google, testing);
        AddOAuthServer(services, options, environment, testing);
        AddRateLimits(services, options);

        services.Configure<ForwardedHeadersOptions>(forwarded =>
        {
            forwarded.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

            foreach (var proxy in configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
            {
                forwarded.KnownProxies.Add(IPAddress.Parse(proxy));
            }
        });

        services.AddHsts(hsts => hsts.MaxAge = TimeSpan.FromDays(365));

        return services;
    }

    public static WebApplication UseSyncServer(this WebApplication app)
    {
        app.UseForwardedHeaders();
        app.UseExceptionHandler();

        if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment(TestingEnvironment))
        {
            app.UseHsts();
            app.UseHttpsRedirection();
        }

        app.UseRouting();

        // O rate limit tem dois estágios (ADR-069). A borda, por IP, vem antes do
        // UseAuthentication, porque é dentro dele que o OpenIddict responde os erros
        // de /connect/*, e eles têm de gastar a cota de login. O da API vem depois,
        // com o Bearer já validado, e antes do UseAuthorization, para contar também
        // quem vai levar 401.
        app.UseRateLimiter(EdgeRateLimits(app.Services.GetRequiredService<SyncServerOptions>().RateLimits));
        app.UseAuthentication();
        app.Use(IdentifyApiCallerAsync);
        app.UseRateLimiter();
        app.UseAuthorization();
        app.UseAntiforgery();

        app.MapGet("/health", () => Results.Ok(new { status = "ok" })).AllowAnonymous();
        app.MapAccountEndpoints();
        app.MapConnectEndpoints();
        app.MapSyncApi();

        return app;
    }

    /// <summary>Em desenvolvimento, as migrations sobem com o app; em produção, por bundle (docs/sync-server.md).</summary>
    public static async Task MigrateIfConfiguredAsync(this WebApplication app)
    {
        if (!app.Configuration.GetValue("Database:MigrateOnStartup", app.Environment.IsDevelopment()))
        {
            return;
        }

        await using var scope = app.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<ServerDbContext>().Database.MigrateAsync();
    }

    /// <summary>
    /// Contas só por login externo. O cookie do Identity vive dez minutos e só
    /// serve para atravessar o <c>/connect/authorize</c>: a API nunca o aceita,
    /// e por isso não há o que um site de terceiros forjar nela.
    /// </summary>
    private static void AddAccounts(IServiceCollection services, GoogleOptions google, bool testing)
    {
        services.AddIdentityCore<ApplicationUser>(identity =>
            {
                identity.User.RequireUniqueEmail = false;
                identity.User.AllowedUserNameCharacters += " ";
            })
            .AddEntityFrameworkStores<ServerDbContext>()
            .AddSignInManager();

        var authentication = services
            .AddAuthentication(IdentityConstants.ApplicationScheme);

        authentication.AddIdentityCookies();

        if (google.IsConfigured)
        {
            authentication.AddGoogle(AccountEndpoints.ExternalProvider, options =>
            {
                options.ClientId = google.ClientId!;
                options.ClientSecret = google.ClientSecret!;
                options.SignInScheme = IdentityConstants.ExternalScheme;
                options.AdditionalAuthorizationParameters["prompt"] = "select_account";
                options.SaveTokens = false;

                // O e-mail só abre a lista de cadastro se verificado (ADR-068). O userinfo v3 chama de email_verified; o v2, de verified_email.
                options.ClaimActions.MapCustomJson(AccountEndpoints.EmailVerifiedClaim, user =>
                    (user.TryGetProperty("email_verified", out var flag) || user.TryGetProperty("verified_email", out flag))
                    && (flag.ValueKind == JsonValueKind.True || (flag.ValueKind == JsonValueKind.String && flag.GetString() == "true"))
                        ? "true"
                        : "false");
            });
        }

        services.ConfigureApplicationCookie(cookie =>
        {
            cookie.LoginPath = "/account/login";
            cookie.ExpireTimeSpan = TimeSpan.FromMinutes(10);
            cookie.SlidingExpiration = false;
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.Cookie.SecurePolicy = testing ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });

        services.Configure<CookieAuthenticationOptions>(IdentityConstants.ExternalScheme, cookie =>
        {
            cookie.ExpireTimeSpan = TimeSpan.FromMinutes(10);
            cookie.Cookie.HttpOnly = true;
            cookie.Cookie.SameSite = SameSiteMode.Lax;
            cookie.Cookie.SecurePolicy = testing ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });

        services.AddAntiforgery(antiforgery =>
        {
            antiforgery.Cookie.SameSite = SameSiteMode.Strict;
            antiforgery.Cookie.SecurePolicy = testing ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
        });
    }

    /// <summary>
    /// O OpenIddict como servidor OAuth do desktop: só authorization code com
    /// PKCE, refresh token que gira a cada uso e revogação. O id_token do
    /// Google nunca vira token desta API — ele só abre a sessão do Identity.
    /// </summary>
    private static void AddOAuthServer(IServiceCollection services, SyncServerOptions options, IHostEnvironment environment, bool testing)
    {
        services.AddOpenIddict()
            .AddCore(core => core.UseEntityFrameworkCore().UseDbContext<ServerDbContext>().ReplaceDefaultEntities<Guid>())
            .AddServer(server =>
            {
                server.SetAuthorizationEndpointUris("connect/authorize")
                    .SetTokenEndpointUris("connect/token")
                    .SetRevocationEndpointUris("connect/revoke");

                server.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow();
                server.RequireProofKeyForCodeExchange();

                // Só S256: o "plain" mandaria o verificador em claro no redirect.
                server.Configure(configuration => configuration.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));
                server.RegisterScopes(Scopes.OpenId, Scopes.Profile, Scopes.Email, Scopes.OfflineAccess, SyncSchema.Scope);

                server.SetAccessTokenLifetime(options.AccessTokenLifetime)
                    .SetRefreshTokenLifetime(options.RefreshTokenLifetime)
                    .SetAuthorizationCodeLifetime(options.AuthorizationCodeLifetime)
                    .SetRefreshTokenReuseLeeway(options.RefreshTokenReuseLeeway);

                if (testing)
                {
                    server.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
                }
                else if (environment.IsDevelopment())
                {
                    server.AddDevelopmentEncryptionCertificate().AddDevelopmentSigningCertificate();
                }
                else
                {
                    server.AddSigningCertificate(LoadCertificate(options.SigningCertificate, "assinatura"))
                        .AddEncryptionCertificate(LoadCertificate(options.EncryptionCertificate, "cifragem"));
                }

                var aspNetCore = server.UseAspNetCore()
                    .EnableAuthorizationEndpointPassthrough()
                    .EnableTokenEndpointPassthrough();

                // HTTP só nos testes e no desenvolvimento local; em produção, sem TLS o OpenIddict recusa.
                if (testing || environment.IsDevelopment())
                {
                    aspNetCore.DisableTransportSecurityRequirement();
                }
            })
            .AddValidation(validation =>
            {
                validation.UseLocalServer();
                validation.UseAspNetCore();

                // Revogar a autorização derruba na hora os access tokens dela.
                validation.EnableAuthorizationEntryValidation();
            });

        services.AddAuthorization(authorization => authorization.AddPolicy(SyncPolicy, policy => policy
            .AddAuthenticationSchemes(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireClaim(Claims.Subject)
            .RequireClaim(SyncSchema.DeviceIdClaim)
            .RequireAssertion(context => context.User.HasScope(SyncSchema.Scope))));
    }

    private static void AddRateLimits(IServiceCollection services, SyncServerOptions options) =>
        services.AddRateLimiter(limiter => ApiRateLimits(limiter, options.RateLimits));

    /// <summary>
    /// A borda, por IP e antes de qualquer autenticação: o teto global de
    /// proteção e o login. Sincronização e API passam; são do outro estágio.
    /// </summary>
    private static RateLimiterOptions EdgeRateLimits(RateLimitOptions limits)
    {
        var limiter = new RateLimiterOptions();
        Reject(limiter);

        if (limits.GlobalPerMinute > 0)
        {
            limiter.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context => RateLimitPartition.GetFixedWindowLimiter(
                ClientAddress(context),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.GlobalPerMinute, Window = TimeSpan.FromMinutes(1) }));
        }

        limiter.AddPolicy(AuthLimiter, context => RateLimitPartition.GetFixedWindowLimiter(
            ClientAddress(context),
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.AuthPerMinute, Window = TimeSpan.FromMinutes(1) }));

        limiter.AddPolicy(SyncLimiter, Unlimited);
        limiter.AddPolicy(ApiLimiter, Unlimited);

        return limiter;
    }

    /// <summary>
    /// A API, pelo <c>sub</c> do Bearer validado. Sem token válido, a
    /// requisição cai no balde anônimo do IP e não gasta a cota de ninguém. O
    /// login passa; é da borda.
    /// </summary>
    private static void ApiRateLimits(RateLimiterOptions limiter, RateLimitOptions limits)
    {
        Reject(limiter);

        limiter.AddPolicy(AuthLimiter, Unlimited);

        limiter.AddPolicy(SyncLimiter, context => ApiSubject(context) is { } subject
            ? RateLimitPartition.GetTokenBucketLimiter(
                $"u:{subject}",
                _ => new TokenBucketRateLimiterOptions
                {
                    TokenLimit = limits.SyncBurst,
                    TokensPerPeriod = Math.Max(1, limits.SyncPerMinute),
                    ReplenishmentPeriod = TimeSpan.FromMinutes(1),
                })
            : AnonymousPartition(context, limits));

        limiter.AddPolicy(ApiLimiter, context => ApiSubject(context) is { } subject
            ? RateLimitPartition.GetFixedWindowLimiter(
                $"u:{subject}",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.ApiPerMinute, Window = TimeSpan.FromMinutes(1) })
            : AnonymousPartition(context, limits));
    }

    private static void Reject(RateLimiterOptions limiter)
    {
        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        limiter.OnRejected = async (context, cancellationToken) =>
        {
            if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            {
                context.HttpContext.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
            }

            await Results.Problem(
                    title: "Muitas requisições. Tente de novo em instantes.",
                    statusCode: StatusCodes.Status429TooManyRequests,
                    extensions: new Dictionary<string, object?> { ["code"] = "rate_limited" })
                .ExecuteAsync(context.HttpContext);
        };
    }

    /// <summary>
    /// O <c>sub</c> do Bearer das rotas da API, validado pelo esquema do
    /// OpenIddict (assinatura, validade e autorização não revogada). O handler
    /// guarda o resultado, e a política <see cref="SyncPolicy"/> o reaproveita:
    /// o token não é validado duas vezes. Token inválido ou ausente fica sem
    /// <c>sub</c>, e a requisição cai no balde anônimo.
    /// </summary>
    private static async Task IdentifyApiCallerAsync(HttpContext context, RequestDelegate next)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName is SyncLimiter or ApiLimiter)
        {
            var result = await context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);
            context.Features.Set(new ApiCaller(result.Succeeded ? result.Principal.FindFirst(Claims.Subject)?.Value : null));
        }

        await next(context);
    }

    private static string ClientAddress(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "desconhecido";

    private static string? ApiSubject(HttpContext context) => context.Features.Get<ApiCaller>()?.Subject;

    private static RateLimitPartition<string> AnonymousPartition(HttpContext context, RateLimitOptions limits) =>
        RateLimitPartition.GetFixedWindowLimiter(
            $"anon:{ClientAddress(context)}",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = limits.AnonymousPerMinute, Window = TimeSpan.FromMinutes(1) });

    private static RateLimitPartition<string> Unlimited(HttpContext context) => RateLimitPartition.GetNoLimiter(string.Empty);

    private static X509Certificate2 LoadCertificate(CertificateOptions? certificate, string purpose)
    {
        if (certificate?.Base64 is { Length: > 0 } base64)
        {
            return X509CertificateLoader.LoadPkcs12(Convert.FromBase64String(base64), certificate.Password);
        }

        if (certificate?.Path is { Length: > 0 } path)
        {
            return X509CertificateLoader.LoadPkcs12FromFile(path, certificate.Password);
        }

        throw new InvalidOperationException(
            $"Falta o certificado de {purpose} do OpenIddict (Sync__SigningCertificate__Path ou __Base64, e Sync__EncryptionCertificate__…).");
    }

    private sealed record ApiCaller(string? Subject);
}
