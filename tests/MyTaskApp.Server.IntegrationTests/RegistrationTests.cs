using System.Net;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Server.Api;
using MyTaskApp.Server.Infrastructure.Persistence;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// A lista de quem pode usar o servidor (ADR-068): fora dela, nem conta, nem
/// workspace, nem token; a conta tirada da lista perde a renovação e o login.
/// </summary>
public sealed class RegistrationTests(PostgresFixture postgres) : IAsyncLifetime
{
    private readonly List<ApiFactory> _servers = [];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public ValueTask InitializeAsync() => ValueTask.CompletedTask;

    public async ValueTask DisposeAsync()
    {
        foreach (var server in _servers)
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task ASubjectOutsideTheList_GetsNeitherAccountNorWorkspaceNorToken()
    {
        var api = await ServerAsync();

        var authorize = () => api.AuthorizeAsync("intrusa", Guid.CreateVersion7());

        var refused = (await authorize.Should().ThrowAsync<OAuthRefusedException>()).Which;
        refused.Status.Should().Be(HttpStatusCode.Forbidden, "a recusa é uma página do callback, sem volta para o desktop com código");
        refused.Description.Should().Contain(SyncErrorCodes.RegistrationClosed);

        await using var scope = api.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var database = scope.ServiceProvider.GetRequiredService<ServerDbContext>();

        (await users.FindByLoginAsync(AccountEndpoints.ExternalProvider, "intrusa")).Should().BeNull();
        (await database.Users.CountAsync(Ct)).Should().Be(0);
        (await database.Workspaces.CountAsync(Ct)).Should().Be(0);
        (await database.Devices.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task ASubjectOnTheList_LogsInNormally_AndTheListReplacesTheDefault()
    {
        var api = await ServerAsync(new() { ["Sync:Registration:AllowedSubjects:0"] = "carla" });

        var session = await api.LoginAsync("carla");

        (await session.WorkspaceAsync()).Should().NotBeEmpty();

        var outsider = () => api.AuthorizeAsync("ana", Guid.CreateVersion7());
        (await outsider.Should().ThrowAsync<OAuthRefusedException>()).Which.Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnEmailOnTheList_OpensOnlyWhenGoogleSaysItIsVerified()
    {
        var api = await ServerAsync(new() { ["Sync:Registration:AllowedEmails:0"] = " Dora@Gmail.com " });

        var unverified = () => api.AuthorizeAsync("dora", Guid.CreateVersion7(), emailVerified: false);
        (await unverified.Should().ThrowAsync<OAuthRefusedException>()).Which.Description.Should().Contain(SyncErrorCodes.RegistrationClosed);

        var session = await api.LoginAsync("dora");

        (await session.WorkspaceAsync()).Should().NotBeEmpty("sem diferença de maiúsculas nem de espaços na configuração");
    }

    [Fact]
    public async Task NothingConfigured_NobodyGetsIn()
    {
        var api = await ServerAsync(new() { ["Sync:Registration:AllowedEmails:0"] = string.Empty });

        var authorize = () => api.AuthorizeAsync("ana", Guid.CreateVersion7());

        (await authorize.Should().ThrowAsync<OAuthRefusedException>()).Which.Status.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task AnAccountTakenOffTheList_LosesTheRefreshAndTheLogin_ButKeepsItsData()
    {
        var api = await ServerAsync();
        var session = await api.LoginAsync("bia");
        var workspace = await session.WorkspaceAsync();

        // Como se a configuração mudasse e o servidor reiniciasse.
        api.Services.GetRequiredService<SyncServerOptions>().Registration.AllowedSubjects = ["ana"];

        var refresh = () => api.RefreshAsync(session.Tokens.RefreshToken);
        (await refresh.Should().ThrowAsync<OAuthRefusedException>()).Which.Error.Should().Be("invalid_grant");

        var login = () => api.AuthorizeAsync("bia", Guid.CreateVersion7());
        (await login.Should().ThrowAsync<OAuthRefusedException>()).Which.Description.Should().Contain(SyncErrorCodes.RegistrationClosed);

        await using var scope = api.Services.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        (await database.Workspaces.AnyAsync(row => row.Id == workspace, Ct)).Should().BeTrue("tirar da lista não apaga nada");
    }

    [Fact]
    public async Task ASessionStillOpen_IsRefusedAtAuthorize_WhenTheListChanged()
    {
        var api = await ServerAsync();
        using var browser = api.Browser("bia");
        await api.AuthorizeAsync("bia", Guid.CreateVersion7(), browser: browser);

        api.Services.GetRequiredService<SyncServerOptions>().Registration.AllowedSubjects = ["ana"];

        // A sessão do Identity ainda vale: o navegador nem passa pelo callback.
        var again = () => api.AuthorizeAsync("bia", Guid.CreateVersion7(), browser: browser);

        (await again.Should().ThrowAsync<OAuthRefusedException>()).Which.Error.Should().Be("access_denied");
    }

    private async Task<ApiFactory> ServerAsync(Dictionary<string, string?>? settings = null)
    {
        var connectionString = await postgres.CreateDatabaseAsync(Ct);
        var server = new ApiFactory(connectionString, settings);
        _ = server.Server;
        _servers.Add(server);
        return server;
    }
}
