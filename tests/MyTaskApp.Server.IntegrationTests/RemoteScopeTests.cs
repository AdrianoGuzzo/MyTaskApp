using System.Net;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Api;
using MyTaskApp.Sync.Contracts;
using OpenIddict.Validation.AspNetCore;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// O escopo <c>remote</c> (fase 2, T-02; RS-07): o servidor o conhece, os dois
/// clientes podem pedi-lo, e a política <see cref="ServerSetup.RemotePolicy"/>
/// exige ele e o <c>sync</c>. Nenhuma rota usa a política ainda; uma sonda só
/// dos testes a confere com tokens de verdade.
/// </summary>
public sealed class RemoteScopeTests(PostgresFixture postgres) : IAsyncLifetime
{
    private const string WithRemote = ApiFactory.SyncScopes + " " + RemoteSchema.Scope;

    private ApiFactory _api = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() =>
        _api = new ApiFactory(
            await postgres.CreateDatabaseAsync(Ct),
            testServices: services => services.AddSingleton<IStartupFilter, RemotePolicyProbe>());

    public async ValueTask DisposeAsync() => await _api.DisposeAsync();

    [Fact]
    public async Task TheTokenOfToday_StillSyncs_ButIsRefusedByTheRemotePolicy()
    {
        var session = await _api.LoginAsync("ana");

        (await ProbeAsync(session)).Should().Be(HttpStatusCode.Forbidden);
        (await session.GetAsync<MeResponse>(ApiRoutes.Me)).Device!.DeviceId.Should().Be(session.DeviceId);
    }

    [Fact]
    public async Task ATokenWithRemote_PassesTheRemotePolicy_AndStillSyncs()
    {
        var session = await _api.LoginAsync("ana", scope: WithRemote);

        (await ProbeAsync(session)).Should().Be(HttpStatusCode.OK);
        (await session.GetAsync<MeResponse>(ApiRoutes.Me)).Device!.DeviceId.Should().Be(session.DeviceId);
    }

    [Fact]
    public async Task TheRefreshedToken_KeepsRemote()
    {
        var session = await _api.LoginAsync("ana", scope: WithRemote);

        session.Tokens = await _api.RefreshAsync(session.Tokens.RefreshToken);

        (await ProbeAsync(session)).Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ThePhone_CanAskForRemote()
    {
        var session = await _api.LoginMobileAsync("ana", scope: WithRemote);

        (await ProbeAsync(session)).Should().Be(HttpStatusCode.OK);
    }

    /// <summary>O <c>remote</c> soma ao <c>sync</c>; sozinho, não abre nada.</summary>
    [Fact]
    public async Task RemoteWithoutSync_IsRefused()
    {
        var session = await _api.LoginAsync("ana", scope: "openid offline_access " + RemoteSchema.Scope);

        (await ProbeAsync(session)).Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task WithoutAToken_TheProbeSaysUnauthorized()
    {
        using var client = _api.CreateClient();
        using var response = await client.GetAsync(RemotePolicyProbe.Path, Ct);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static async Task<HttpStatusCode> ProbeAsync(DesktopSession session)
    {
        using var client = session.Api();
        using var response = await client.GetAsync(RemotePolicyProbe.Path, Ct);
        return response.StatusCode;
    }

    /// <summary>
    /// Autentica o Bearer como as rotas da API (o OpenIddict só valida atrás do
    /// <c>UseAuthentication</c>) e avalia a política remota: 401
    /// sem token válido, 403 se a política recusa, 200 se aceita.
    /// </summary>
    private sealed class RemotePolicyProbe : IStartupFilter
    {
        public const string Path = "/test/remote-policy";

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            // MapWhen, não Map: o Map passaria o caminho para o PathBase, e o issuer
            // que o OpenIddict tira da requisição deixaria de bater com o do token.
            app.MapWhen(context => context.Request.Path == Path, probe => probe.UseAuthentication().Run(async context =>
            {
                var authentication = await context.AuthenticateAsync(OpenIddictValidationAspNetCoreDefaults.AuthenticationScheme);

                if (!authentication.Succeeded)
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }

                var authorization = await context.RequestServices.GetRequiredService<IAuthorizationService>()
                    .AuthorizeAsync(authentication.Principal, ServerSetup.RemotePolicy);

                context.Response.StatusCode = authorization.Succeeded ? StatusCodes.Status200OK : StatusCodes.Status403Forbidden;
            }));

            next(app);
        };
    }
}
