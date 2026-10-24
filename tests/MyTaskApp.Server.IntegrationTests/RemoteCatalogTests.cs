using System.Net;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Sync.Contracts;
using static MyTaskApp.Server.IntegrationTests.RemoteTestKit;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// O catálogo de cada máquina (ADR-088): o agente publica o que foi liberado
/// lá, a conta lê, e o hash vai para as capacidades, para o controlador saber
/// quando reler.
/// </summary>
public sealed class RemoteCatalogTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ApiFactory _api = null!;

    private RemoteTestKit _kit = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static AgentCatalog Catalog(string hash = "c0ffee") => new(
        hash,
        [new CatalogDirectory(Guid.CreateVersion7(), Guid.CreateVersion7(), "api", MappedHere: true, IsGitRepository: true)],
        [new CatalogCommand(Guid.CreateVersion7(), "Testes", CommandScope.Directory, null, NeedsApproval: true, [new CatalogParameter("filtro", false, null)])],
        [new CatalogDatabase(Guid.CreateVersion7(), "Loja", "Production", CanBeSource: true, CanBeTarget: false)],
        [new CatalogAgentProfile("leitura", "Só leitura", "Lê e roda testes")]);

    public async ValueTask InitializeAsync()
    {
        _api = new ApiFactory(await postgres.CreateDatabaseAsync(Ct));
        _kit = new RemoteTestKit(_api);
    }

    public async ValueTask DisposeAsync()
    {
        _kit.Dispose();
        await _api.DisposeAsync();
    }

    [Fact]
    public async Task TheAgentPublishes_TheAccountReads_AndTheHashGoesToTheCapabilities()
    {
        var phone = await _kit.DeviceAsync("Celular", DeviceRoles.Controller);
        var notebook = await _kit.DeviceAsync("Notebook", DeviceRoles.Executor);

        (await ErrorAsync(phone.Session, HttpMethod.Get, RemoteRoutes.Catalog(notebook.DeviceId), null)).Status
            .Should().Be(HttpStatusCode.NotFound, "ainda não publicou");

        var catalog = Catalog();
        await SendAsync<AgentCatalog>(notebook.Session, HttpMethod.Put, RemoteRoutes.CurrentCatalog, catalog);

        (await GetAsync<AgentCatalog>(phone.Session, RemoteRoutes.Catalog(notebook.DeviceId))).Should().BeEquivalentTo(catalog);
        (await GetAsync<List<RemoteDeviceInfo>>(phone.Session, RemoteRoutes.Devices))
            .Single(device => device.DeviceId == notebook.DeviceId).Capabilities!.CatalogHash.Should().Be("c0ffee");

        // Publicar de novo troca o catálogo inteiro.
        var next = Catalog("beef") with { Commands = [] };
        await SendAsync<AgentCatalog>(notebook.Session, HttpMethod.Put, RemoteRoutes.CurrentCatalog, next);
        (await GetAsync<AgentCatalog>(phone.Session, RemoteRoutes.Catalog(notebook.DeviceId))).Commands.Should().BeEmpty();
        (await GetAsync<List<RemoteDeviceInfo>>(phone.Session, RemoteRoutes.Devices))
            .Single(device => device.DeviceId == notebook.DeviceId).Capabilities!.CatalogHash.Should().Be("beef");
    }

    [Fact]
    public async Task OnlyARegisteredExecutor_PublishesAWellFormedCatalog()
    {
        var phone = await _kit.DeviceAsync("Celular", DeviceRoles.Controller);
        var notebook = await _kit.DeviceAsync("Notebook", DeviceRoles.Executor);
        var unregistered = await _api.LoginAsync("ana", deviceName: "Sem chave", scope: WithRemote);

        (await ErrorAsync(phone.Session, HttpMethod.Put, RemoteRoutes.CurrentCatalog, Catalog()))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.NotAnExecutor));
        (await ErrorAsync(unregistered, HttpMethod.Put, RemoteRoutes.CurrentCatalog, Catalog()))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.NotRegistered));

        AgentCatalog[] invalid =
        [
            Catalog(hash: string.Empty),
            Catalog() with { Directories = [new CatalogDirectory(Guid.CreateVersion7(), Guid.CreateVersion7(), "api\u001b[2J", true, true)] },
            Catalog() with { AgentProfiles = [new CatalogAgentProfile("a", "A", ""), new CatalogAgentProfile("a", "B", "")] },
        ];

        foreach (var catalog in invalid)
        {
            (await ErrorAsync(notebook.Session, HttpMethod.Put, RemoteRoutes.CurrentCatalog, catalog)).Status.Should().Be(HttpStatusCode.BadRequest);
        }
    }

    [Fact]
    public async Task AnotherAccount_OrARevokedMachine_HasNoCatalogToShow()
    {
        var phone = await _kit.DeviceAsync("Celular", DeviceRoles.Controller);
        var notebook = await _kit.DeviceAsync("Notebook", DeviceRoles.Executor);
        var stranger = await _kit.DeviceAsync("PC da Bia", DeviceRoles.Controller, "bia");
        await SendAsync<AgentCatalog>(notebook.Session, HttpMethod.Put, RemoteRoutes.CurrentCatalog, Catalog());

        (await ErrorAsync(stranger.Session, HttpMethod.Get, RemoteRoutes.Catalog(notebook.DeviceId), null)).Status.Should().Be(HttpStatusCode.NotFound);

        await phone.Session.SendAsync<object, DeviceInfo>(HttpMethod.Post, ApiRoutes.RevokeDevice(notebook.DeviceId), new { });
        (await ErrorAsync(phone.Session, HttpMethod.Get, RemoteRoutes.Catalog(notebook.DeviceId), null)).Status.Should().Be(HttpStatusCode.NotFound);
    }
}
