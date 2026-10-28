using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Application;
using static MyTaskApp.Server.IntegrationTests.RemoteTestKit;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// As conversas do Claude no servidor (ADR-091), de ponta a ponta: o agente
/// relata, a conta lista e lê a linha do tempo com as operações da conversa,
/// e o relato é o sinal de vida do agente, separado do desktop.
/// </summary>
public sealed class RemoteAgentSessionTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow.AddMinutes(-5);

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
    public async Task TheAgentReports_TheAccountListsAndReads_AndTheOperationsOfTheConversationComeInOrder()
    {
        var phone = await _kit.DeviceAsync("Celular", DeviceRoles.Controller);
        var notebook = await _kit.DeviceAsync("Notebook", DeviceRoles.Executor);
        var workspace = await phone.Session.WorkspaceAsync();
        await using var controller = await _kit.ListenAsync(phone.Session);
        var start = await CreateAsync(phone, Request(workspace, phone, notebook, RemoteOperationKinds.AgentStart,
            new ClaudeCodeSessionRequest(Guid.CreateVersion7(), "read", null, null, new PromptRequest(null, "Leia o README"))));
        var report = Report(Guid.CreateVersion7(), 1, RemoteAgentSessionStatus.Running, start.OperationId);

        var ack = await SendAsync<RemoteAgentSessionAck>(notebook.Session, HttpMethod.Put, RemoteRoutes.AgentSessions, new RemoteAgentSessionBatch([report]));

        ack.Sessions.Should().Equal(new RemoteAgentSessionVersion(report.SessionId, 1));
        await controller.ExpectAsync(RemoteHubMessages.SessionChanged, report.SessionId, 1L);

        // O prompt seguinte já chega ligado à conversa, pelo payload; o pedido a outra máquina com o mesmo id, não.
        var next = await CreateAsync(phone, Request(workspace, phone, notebook, RemoteOperationKinds.AgentPrompt, new PromptRequest(report.SessionId, "E os testes?")));
        var desktop = await _kit.DeviceAsync("Desktop", DeviceRoles.Executor);
        await CreateAsync(phone, Request(workspace, phone, desktop, RemoteOperationKinds.AgentPrompt, new PromptRequest(report.SessionId, "Outra máquina")));
        await SendAsync<RemoteAgentSessionAck>(notebook.Session, HttpMethod.Put, RemoteRoutes.AgentSessions,
            new RemoteAgentSessionBatch([report with { Version = 2, Status = RemoteAgentSessionStatus.Idle, Activity = RemoteAgentActivity.WaitingReview, CurrentOperationId = next.OperationId }]));

        var list = await GetAsync<RemoteAgentSessionList>(phone.Session, RemoteRoutes.Sessions);
        var listed = list.Sessions.Should().ContainSingle().Subject;
        (listed.DeviceId, listed.Version, listed.Status, listed.TaskId).Should().Be((notebook.DeviceId, 2L, RemoteAgentSessionStatus.Idle, report.TaskId));
        list.Agents.Single(agent => agent.DeviceId == notebook.DeviceId).IsOnlineAt(DateTimeOffset.UtcNow).Should().BeTrue();

        var detail = await GetAsync<RemoteAgentSessionDetail>(phone.Session, RemoteRoutes.Session(report.SessionId));
        detail.Session.Activity.Should().Be(RemoteAgentActivity.WaitingReview);
        detail.Agent.DeviceId.Should().Be(notebook.DeviceId);
        detail.Operations.Select(operation => operation.OperationId).Should().Equal(start.OperationId, next.OperationId);
    }

    /// <summary>A versão antiga não desfaz a nova; repetir é inofensivo; a conversa é de uma máquina só.</summary>
    [Fact]
    public async Task AnOlderVersion_IsIgnored_AndAnotherMachineCannotReportTheSameConversation()
    {
        var notebook = await _kit.DeviceAsync("Notebook", DeviceRoles.Executor);
        var desktop = await _kit.DeviceAsync("Desktop", DeviceRoles.Executor);
        var report = Report(Guid.CreateVersion7(), 5, RemoteAgentSessionStatus.Idle);
        await SendAsync<RemoteAgentSessionAck>(notebook.Session, HttpMethod.Put, RemoteRoutes.AgentSessions, new RemoteAgentSessionBatch([report]));

        var stale = await SendAsync<RemoteAgentSessionAck>(notebook.Session, HttpMethod.Put, RemoteRoutes.AgentSessions,
            new RemoteAgentSessionBatch([report with { Version = 3, Status = RemoteAgentSessionStatus.Running }]));
        stale.Sessions.Should().Equal(new[] { new RemoteAgentSessionVersion(report.SessionId, 5) }, "a resposta diz a versão guardada, para o agente pular à frente");
        (await SendAsync<RemoteAgentSessionAck>(notebook.Session, HttpMethod.Put, RemoteRoutes.AgentSessions, new RemoteAgentSessionBatch([report])))
            .Sessions.Should().Equal(new RemoteAgentSessionVersion(report.SessionId, 5));
        (await GetAsync<RemoteAgentSessionDetail>(notebook.Session, RemoteRoutes.Session(report.SessionId))).Session.Status.Should().Be(RemoteAgentSessionStatus.Idle);

        (await ErrorAsync(desktop.Session, HttpMethod.Put, RemoteRoutes.AgentSessions, new RemoteAgentSessionBatch([report with { Version = 9 }])))
            .Status.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>Só a máquina que aceita controle relata; o relato tem forma e teto; outra conta não vê nada.</summary>
    [Fact]
    public async Task OnlyAnExecutor_Reports_WellFormed_AndAnotherAccountSeesNothing()
    {
        var phone = await _kit.DeviceAsync("Celular", DeviceRoles.Controller);
        var notebook = await _kit.DeviceAsync("Notebook", DeviceRoles.Executor);
        var stranger = await _kit.DeviceAsync("PC da Bia", DeviceRoles.Controller | DeviceRoles.Executor, "bia");
        var report = Report(Guid.CreateVersion7(), 1, RemoteAgentSessionStatus.Running);

        (await ErrorAsync(phone.Session, HttpMethod.Put, RemoteRoutes.AgentSessions, new RemoteAgentSessionBatch([report])))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.NotAnExecutor));
        (await ErrorAsync(notebook.Session, HttpMethod.Put, RemoteRoutes.AgentSessions, new RemoteAgentSessionBatch([report with { Version = 0 }])))
            .Status.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(notebook.Session, HttpMethod.Put, RemoteRoutes.AgentSessions, new RemoteAgentSessionBatch([report, report])))
            .Status.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(notebook.Session, HttpMethod.Put, RemoteRoutes.AgentSessions, new RemoteAgentSessionBatch([report with { ProfileId = "a\u0007b" }])))
            .Status.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(notebook.Session, HttpMethod.Put, RemoteRoutes.AgentSessions,
                new RemoteAgentSessionBatch([.. Enumerable.Range(0, RemoteLimits.MaxSessionsPerBatch + 1).Select(_ => Report(Guid.CreateVersion7(), 1, RemoteAgentSessionStatus.Idle))])))
            .Status.Should().Be(HttpStatusCode.BadRequest);

        await SendAsync<RemoteAgentSessionAck>(notebook.Session, HttpMethod.Put, RemoteRoutes.AgentSessions,
            new RemoteAgentSessionBatch([report with { ActivityMessage = new string('x', 500) }]));
        (await GetAsync<RemoteAgentSessionDetail>(phone.Session, RemoteRoutes.Session(report.SessionId))).Session.ActivityMessage
            .Should().HaveLength(RemoteLimits.MaxSessionActivityLength);

        (await GetAsync<RemoteAgentSessionList>(stranger.Session, RemoteRoutes.Sessions)).Sessions.Should().BeEmpty();
        (await ErrorAsync(stranger.Session, HttpMethod.Get, RemoteRoutes.Session(report.SessionId), null)).Status.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>
    /// O lote vazio é o sinal de vida do agente; o registro do desktop não é.
    /// Calado, o agente guarda o último estado, e o próximo relato atualiza.
    /// </summary>
    [Fact]
    public async Task TheAgentPresence_ComesFromItsReports_NotFromTheDesktop()
    {
        var phone = await _kit.DeviceAsync("Celular", DeviceRoles.Controller);
        var notebook = await _kit.DeviceAsync("Notebook", DeviceRoles.Executor);

        var before = await GetAsync<RemoteAgentSessionList>(phone.Session, RemoteRoutes.Sessions);
        before.Agents.Should().ContainSingle().Which.AgentSeenAt.Should().BeNull("o desktop registrou, mas o agente nunca relatou");

        await SendAsync<RemoteAgentSessionAck>(notebook.Session, HttpMethod.Put, RemoteRoutes.AgentSessions, new RemoteAgentSessionBatch([]));
        (await GetAsync<RemoteAgentSessionList>(phone.Session, RemoteRoutes.Sessions)).Agents.Single().IsOnlineAt(DateTimeOffset.UtcNow).Should().BeTrue();

        var report = Report(Guid.CreateVersion7(), 1, RemoteAgentSessionStatus.Running);
        await SendAsync<RemoteAgentSessionAck>(notebook.Session, HttpMethod.Put, RemoteRoutes.AgentSessions, new RemoteAgentSessionBatch([report]));
        await _kit.WithDatabaseAsync(database => database.DeviceCapabilities
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.AgentSeenAt, DateTimeOffset.UtcNow.AddHours(-1)), Ct));

        var silent = await GetAsync<RemoteAgentSessionDetail>(phone.Session, RemoteRoutes.Session(report.SessionId));
        silent.Session.Status.Should().Be(RemoteAgentSessionStatus.Running, "o último relato fica");
        silent.Agent.IsOnlineAt(DateTimeOffset.UtcNow).Should().BeFalse("quem mostra decide que o estado é desconhecido");
        (await GetAsync<RemoteAgentSessionList>(phone.Session, RemoteRoutes.SessionsQuery(notebook.DeviceId, openOnly: true, limit: 10))).Sessions.Should().ContainSingle();

        await SendAsync<RemoteAgentSessionAck>(notebook.Session, HttpMethod.Put, RemoteRoutes.AgentSessions,
            new RemoteAgentSessionBatch([report with { Version = 2, Status = RemoteAgentSessionStatus.Stopped }]));
        (await GetAsync<RemoteAgentSessionList>(phone.Session, RemoteRoutes.SessionsQuery(null, openOnly: true, limit: null))).Sessions.Should().BeEmpty();
        (await GetAsync<RemoteAgentSessionList>(phone.Session, RemoteRoutes.Sessions)).Sessions.Should().ContainSingle();
    }

    [Fact]
    public async Task AConversationWithoutReportsPastTheRetention_IsPurged()
    {
        var notebook = await _kit.DeviceAsync("Notebook", DeviceRoles.Executor);
        var old = Report(Guid.CreateVersion7(), 1, RemoteAgentSessionStatus.Idle);
        var recent = Report(Guid.CreateVersion7(), 1, RemoteAgentSessionStatus.Idle);
        await SendAsync<RemoteAgentSessionAck>(notebook.Session, HttpMethod.Put, RemoteRoutes.AgentSessions, new RemoteAgentSessionBatch([old, recent]));
        await _kit.WithDatabaseAsync(database => database.RemoteAgentSessions
            .Where(row => row.SessionId == old.SessionId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ReportedAt, DateTimeOffset.UtcNow.AddDays(-31)), Ct));

        await using (var scope = _api.Services.CreateAsyncScope())
        {
            (await scope.ServiceProvider.GetRequiredService<RemoteAgentSessionUseCases>().SweepAsync(TimeSpan.FromDays(30), Ct)).Should().Be(1);
        }

        (await GetAsync<RemoteAgentSessionList>(notebook.Session, RemoteRoutes.Sessions)).Sessions.Select(session => session.SessionId).Should().Equal(recent.SessionId);
    }

    private static RemoteAgentSessionReport Report(Guid sessionId, long version, RemoteAgentSessionStatus status, Guid? operationId = null) => new(
        sessionId,
        version,
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        "read",
        "haiku",
        status,
        status == RemoteAgentSessionStatus.Running ? RemoteAgentActivity.Working : RemoteAgentActivity.WaitingReview,
        null,
        operationId,
        Started,
        status == RemoteAgentSessionStatus.Running ? null : Started.AddMinutes(1),
        Started.AddMinutes(1));

    private static RemoteOperationRequest Request<T>(Guid workspaceId, RemoteDevice from, RemoteDevice to, string kind, T payload)
    {
        var now = DateTimeOffset.UtcNow;

        return new RemoteOperationRequest(
            Guid.CreateVersion7(),
            workspaceId,
            to.DeviceId,
            from.DeviceId,
            kind,
            JsonSerializer.SerializeToElement(payload, RemoteJson.Options),
            Guid.CreateVersion7(),
            [],
            now,
            now.AddMinutes(10));
    }
}
