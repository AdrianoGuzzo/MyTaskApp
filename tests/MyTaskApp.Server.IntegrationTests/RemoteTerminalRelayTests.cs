using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using MyTaskApp.Remote.Contracts;
using static MyTaskApp.Server.IntegrationTests.RemoteTestKit;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// O terminal remoto pelo hub de verdade (ADR-099): a saída da máquina alvo
/// chega a quem pediu a conexão, a entrada assinada vai à máquina alvo como
/// saiu, ninguém mais segue nem publica, a mensagem grande demais derruba a
/// conexão, e nada do conteúdo vai para o log.
/// </summary>
public sealed class RemoteTerminalRelayTests(PostgresFixture postgres) : IAsyncLifetime
{
    private ApiFactory _api = null!;

    private RemoteTestKit _kit = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

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
    public async Task TheOutputReachesWhoAsked_TheInputReachesTheTarget_AndLeavingIsHeard()
    {
        var (phone, notebook, workspace) = await PairAsync();
        var attach = await AttachAsync(phone, notebook, workspace);

        await using var agent = await _kit.ListenAsync(notebook.Session);
        var controller = await _kit.ListenAsync(phone.Session);

        await controller.Connection.InvokeAsync(RemoteHubMethods.TerminalSubscribe, attach, Ct);
        await agent.Connection.InvokeAsync(RemoteHubMethods.TerminalPublish, Output(attach, 0, "segredo-da-tela", reset: true), Ct);
        await agent.Connection.InvokeAsync(RemoteHubMethods.TerminalPublish, Output(attach, 15, "\r\n> "), Ct);

        var frames = (await controller.WaitUntilAsync(received => received.Count(item => item.Message == RemoteHubMessages.TerminalFrame) == 2))
            .Where(item => item.Message == RemoteHubMessages.TerminalFrame)
            .Select(item => (RemoteTerminalFrame)item.Args[0]!)
            .ToList();
        frames.Select(frame => frame.Offset).Should().Equal(0, 15);
        Encoding.UTF8.GetString(frames[0].Data!).Should().Be("segredo-da-tela");

        var input = new SignedEnvelope("eyJhbGciOiJFUzI1NiJ9.dGVjbGFkbw.YXNzaW5hZG8");
        await controller.Connection.InvokeAsync(RemoteHubMethods.TerminalSend, attach, input, Ct);
        await agent.ExpectAsync(RemoteHubMessages.TerminalInput, attach, input);

        await controller.DisposeAsync();
        await agent.ExpectAsync(RemoteHubMessages.TerminalDetached, attach);

        _api.Logs.Text.Should().NotContain("segredo-da-tela").And.NotContain("dGVjbGFkbw");
    }

    [Fact]
    public async Task WhenTheAgentLeaves_TheFollowerHearsItClosed()
    {
        var (phone, notebook, workspace) = await PairAsync();
        var attach = await AttachAsync(phone, notebook, workspace);

        var agent = await _kit.ListenAsync(notebook.Session);
        await using var controller = await _kit.ListenAsync(phone.Session);
        await controller.Connection.InvokeAsync(RemoteHubMethods.TerminalSubscribe, attach, Ct);
        await agent.Connection.InvokeAsync(RemoteHubMethods.TerminalPublish, Output(attach, 0, "x"), Ct);

        await agent.DisposeAsync();

        await controller.WaitUntilAsync(received => received.Any(item => item.Message == RemoteHubMessages.TerminalClosed && (Guid)item.Args[0]! == attach));
    }

    [Fact]
    public async Task NobodyElse_FollowsTypesOrPublishes()
    {
        var (phone, notebook, workspace) = await PairAsync();
        var attach = await AttachAsync(phone, notebook, workspace);
        var stranger = await _kit.DeviceAsync("PC da Bia", DeviceRoles.Controller | DeviceRoles.Executor, "bia");
        var tablet = await _kit.DeviceAsync("Tablet", DeviceRoles.Controller);

        await using var agent = await _kit.ListenAsync(notebook.Session);
        await using var controller = await _kit.ListenAsync(phone.Session);
        await using var outsider = await _kit.ListenAsync(stranger.Session);
        await using var sibling = await _kit.ListenAsync(tablet.Session);

        foreach (var hub in new[] { outsider, sibling, agent })
        {
            var subscribe = () => hub.Connection.InvokeAsync(RemoteHubMethods.TerminalSubscribe, attach, Ct);
            await subscribe.Should().ThrowAsync<HubException>();
        }

        var send = () => sibling.Connection.InvokeAsync(RemoteHubMethods.TerminalSend, attach, new SignedEnvelope("a.b.c"), Ct);
        await send.Should().ThrowAsync<HubException>();

        var publish = () => controller.Connection.InvokeAsync(RemoteHubMethods.TerminalPublish, Output(attach, 0, "falso"), Ct);
        await publish.Should().ThrowAsync<HubException>("só a máquina alvo publica");

        var other = () => outsider.Connection.InvokeAsync(RemoteHubMethods.TerminalPublish, Output(attach, 0, "falso"), Ct);
        await other.Should().ThrowAsync<HubException>();

        agent.Received.Should().NotContain(item => item.Message == RemoteHubMessages.TerminalInput);
    }

    [Fact]
    public async Task AnOperationThatIsNotAnAttach_IsNotAConnection()
    {
        var (phone, notebook, workspace) = await PairAsync();
        var inspection = (await CreateAsync(phone, Inspection(workspace, phone, notebook))).OperationId;

        await using var controller = await _kit.ListenAsync(phone.Session);

        var subscribe = () => controller.Connection.InvokeAsync(RemoteHubMethods.TerminalSubscribe, inspection, Ct);
        (await subscribe.Should().ThrowAsync<HubException>()).Which.Message.Should().Contain("não existe ou já acabou");
    }

    [Fact]
    public async Task AMessageOverTheCeiling_DropsTheConnection()
    {
        var (phone, notebook, workspace) = await PairAsync();
        var attach = await AttachAsync(phone, notebook, workspace);
        await using var controller = await _kit.ListenAsync(phone.Session);

        var huge = new SignedEnvelope(new string('a', RemoteLimits.MaxTerminalMessageLength + 1));
        var send = () => controller.Connection.InvokeAsync(RemoteHubMethods.TerminalSend, attach, huge, Ct);

        await send.Should().ThrowAsync<Exception>();
        await controller.Closed.WaitAsync(TimeSpan.FromSeconds(10), Ct);
    }

    [Fact]
    public async Task TheTerminalSessions_AreReportedByTheAgent_AndListedForTheAccountOnly()
    {
        var (phone, notebook, _) = await PairAsync();
        var stranger = await _kit.DeviceAsync("PC da Bia", DeviceRoles.Controller, "bia");
        await using var controller = await _kit.ListenAsync(phone.Session);

        var session = new RemoteTerminalSessionReport(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), RemoteTerminalProcess.Running, null,
            RemoteAgentActivity.WaitingForUser, "Pedido de permissão", HasDialog: true, Monitored: true, PermissionsBypassed: false,
            RemotelyControlled: false, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        using (var response = await PutAsync(notebook, new RemoteTerminalSessionBatch([session])))
        {
            response.StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);
        }

        await controller.ExpectAsync(RemoteHubMessages.TerminalsChanged, notebook.DeviceId);

        var listed = await GetAsync<RemoteTerminalSessionList>(phone.Session, RemoteRoutes.Terminals);
        listed.Sessions.Should().ContainSingle().Which.Should().Match<RemoteTerminalSessionView>(view => view.DeviceId == notebook.DeviceId && view.Session.HasDialog);
        (await GetAsync<RemoteTerminalSessionList>(stranger.Session, RemoteRoutes.Terminals)).Sessions.Should().BeEmpty();

        using var fromController = await PutAsync(phone, new RemoteTerminalSessionBatch([session]));
        fromController.StatusCode.Should().Be(System.Net.HttpStatusCode.Conflict, "só a máquina que aceita controle relata");
    }

    private async Task<(RemoteDevice Phone, RemoteDevice Notebook, Guid Workspace)> PairAsync()
    {
        var phone = await _kit.DeviceAsync("Celular", DeviceRoles.Controller);
        var notebook = await _kit.DeviceAsync("Notebook", DeviceRoles.Executor);

        return (phone, notebook, await phone.Session.WorkspaceAsync());
    }

    private async Task<Guid> AttachAsync(RemoteDevice from, RemoteDevice to, Guid workspace)
    {
        var viewer = _kit.NewKey();
        var now = DateTimeOffset.UtcNow;
        var request = new RemoteOperationRequest(
            Guid.CreateVersion7(),
            workspace,
            to.DeviceId,
            from.DeviceId,
            RemoteOperationKinds.TerminalAttach,
            JsonSerializer.SerializeToElement(
                new RemoteTerminalAttachRequest(Guid.CreateVersion7(), Guid.CreateVersion7(), DevicePublicKey.Es256Of(viewer.ExportSubjectPublicKeyInfo()), 120, 30),
                RemoteJson.Options),
            null,
            [],
            now,
            now.AddMinutes(10));

        return (await CreateAsync(from, request)).OperationId;
    }

    private static RemoteTerminalFrame Output(Guid attach, long offset, string text, bool reset = false) =>
        new(attach, RemoteTerminalFrameKinds.Output, offset, Encoding.UTF8.GetBytes(text), reset);

    private static async Task<HttpResponseMessage> PutAsync(RemoteDevice device, RemoteTerminalSessionBatch batch)
    {
        using var client = device.Session.Api();
        return await client.PutAsync(
            RemoteRoutes.AgentTerminals,
            new StringContent(JsonSerializer.Serialize(batch, RemoteJson.Options), Encoding.UTF8, "application/json"),
            Ct);
    }
}
