using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Application;
using MyTaskApp.Sync.Contracts;
using static MyTaskApp.Server.IntegrationTests.RemoteTestKit;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// A fila de operações (fase 2, T-12; ADR-086), de ponta a ponta: o celular
/// cria assinando, o agente do notebook busca os bytes assinados, aceita,
/// conta o andamento e termina; a varredura devolve e expira o que ficou parado.
/// </summary>
public sealed class RemoteOperationTests(PostgresFixture postgres) : IAsyncLifetime
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
    public async Task ThePhoneQueues_TheAgentReceivesTheSignedBytes_AndRunsItToTheEnd()
    {
        var (phone, notebook, workspace) = await PairAsync();
        var request = Inspection(workspace, phone, notebook);
        var envelope = Sign(phone.Key, request, RemoteJws.OperationType);

        var queued = await SendAsync<RemoteOperationView>(phone.Session, HttpMethod.Post, RemoteRoutes.Operations, envelope, HttpStatusCode.Created);
        queued.Status.Should().Be(RemoteOperationStatus.Queued);
        queued.TargetDeviceId.Should().Be(notebook.DeviceId);
        (await GetAsync<List<RemoteOperationView>>(phone.Session, RemoteRoutes.Operations)).Should().ContainSingle()
            .Which.OperationId.Should().Be(request.OperationId);

        // O agente recebe o JWS como o celular mandou, e busca de novo sem perder nada.
        var delivered = (await GetAsync<List<AgentOperation>>(notebook.Session, RemoteRoutes.AgentOperations)).Should().ContainSingle().Subject;
        delivered.Request.Should().Be(envelope);
        delivered.Status.Should().Be(RemoteOperationStatus.Dispatched);
        (await GetAsync<List<AgentOperation>>(notebook.Session, RemoteRoutes.AgentOperations)).Should().ContainSingle();
        RemoteJws.TryVerify<RemoteOperationRequest>(delivered.Request, RemoteJws.OperationType, DevicePublicKey.Es256Of(phone.Key.ExportSubjectPublicKeyInfo()), out _)
            .Should().BeTrue();

        await MoveAsync(notebook, request.OperationId, RemoteOperationStatus.Accepted);
        await MoveAsync(notebook, request.OperationId, RemoteOperationStatus.Running);
        var done = await MoveAsync(notebook, request.OperationId, RemoteOperationStatus.Succeeded, summary: "Git 2.51, sem Claude‮");

        done.Status.Should().Be(RemoteOperationStatus.Succeeded);
        done.ResultSummary.Should().Be("Git 2.51, sem Claude", "o resumo chega sem formatação bidi");
        (await GetAsync<RemoteOperationView>(phone.Session, RemoteRoutes.Operation(request.OperationId))).Status.Should().Be(RemoteOperationStatus.Succeeded);
        (await GetAsync<List<AgentOperation>>(notebook.Session, RemoteRoutes.AgentOperations)).Should().BeEmpty("a terminada não volta");
    }

    [Fact]
    public async Task TheSameEnvelopeTwice_IsTheSameOperation_AndAnotherRequestWithTheSameId_IsRefused()
    {
        var (phone, notebook, workspace) = await PairAsync();
        var request = Inspection(workspace, phone, notebook);
        var envelope = Sign(phone.Key, request, RemoteJws.OperationType);

        var first = await SendAsync<RemoteOperationView>(phone.Session, HttpMethod.Post, RemoteRoutes.Operations, envelope, HttpStatusCode.Created);
        var again = await SendAsync<RemoteOperationView>(phone.Session, HttpMethod.Post, RemoteRoutes.Operations, envelope, HttpStatusCode.Created);

        again.Should().Be(first);

        var other = Sign(phone.Key, request with { NotAfter = request.NotAfter.AddMinutes(1) }, RemoteJws.OperationType);
        (await ErrorAsync(phone.Session, HttpMethod.Post, RemoteRoutes.Operations, other))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.IdempotencyKeyReuse));
        (await GetAsync<List<RemoteOperationView>>(phone.Session, RemoteRoutes.Operations)).Should().ContainSingle();
    }

    /// <summary>Só a chave ativa de quem chama assina: a de outro dispositivo, outra chave com o kid dele, ou outro tipo não passam.</summary>
    [Fact]
    public async Task ASignatureThatIsNotTheCallersActiveKey_IsRefused()
    {
        var (phone, notebook, workspace) = await PairAsync();
        var request = Inspection(workspace, phone, notebook);
        var phoneKid = DevicePublicKey.FingerprintOf(phone.Key.ExportSubjectPublicKeyInfo());

        object[] forged =
        [
            Sign(notebook.Key, request, RemoteJws.OperationType),
            Sign(_kit.NewKey(), request, RemoteJws.OperationType, kid: phoneKid),
            Sign(phone.Key, request, RemoteJws.ApprovalType),
            new SignedEnvelope("não é JWS"),
        ];

        foreach (var envelope in forged)
        {
            (await ErrorAsync(phone.Session, HttpMethod.Post, RemoteRoutes.Operations, envelope))
                .Should().Be((HttpStatusCode.Forbidden, RemoteErrorCodes.UntrustedSignature));
        }

        // Assinado pelo celular, mas dizendo ser do notebook.
        (await ErrorAsync(phone.Session, HttpMethod.Post, RemoteRoutes.Operations,
                Sign(phone.Key, request with { RequestedByDeviceId = notebook.DeviceId }, RemoteJws.OperationType)))
            .Status.Should().Be(HttpStatusCode.Forbidden);

        (await GetAsync<List<RemoteOperationView>>(phone.Session, RemoteRoutes.Operations)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheRulesTheWorkspaceAndTheTarget_AreCheckedBeforeQueueing()
    {
        var (phone, notebook, workspace) = await PairAsync();
        var otherPhone = await _kit.DeviceAsync("Outro celular", DeviceRoles.Controller);
        var strangerWorkspace = await (await _kit.DeviceAsync("PC da Bia", DeviceRoles.Executor, "bia")).Session.WorkspaceAsync();
        var now = DateTimeOffset.UtcNow;

        async Task<(HttpStatusCode, string?)> Create(RemoteOperationRequest request) =>
            await ErrorAsync(phone.Session, HttpMethod.Post, RemoteRoutes.Operations, Sign(phone.Key, request, RemoteJws.OperationType));

        (await Create(Inspection(workspace, phone, notebook) with { Kind = "shell.anything" }))
            .Should().Be((HttpStatusCode.BadRequest, RemoteErrorCodes.UnknownKind));
        (await Create(Inspection(workspace, phone, notebook) with { CreatedAt = now.AddMinutes(-20), NotAfter = now.AddMinutes(-1) }))
            .Should().Be((HttpStatusCode.BadRequest, RemoteErrorCodes.AlreadyExpired));
        (await Create(Inspection(workspace, phone, notebook) with { NotAfter = now.AddDays(2) }))
            .Should().Be((HttpStatusCode.BadRequest, RemoteErrorCodes.WindowTooLong));
        (await Create(Inspection(strangerWorkspace, phone, notebook)))
            .Should().Be((HttpStatusCode.Forbidden, RemoteErrorCodes.WrongWorkspace));
        (await Create(Inspection(workspace, phone, otherPhone)))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.NotAnExecutor));

        // Um controlador sem chave registrada não cria nada.
        var unregistered = await _api.LoginAsync("ana", deviceName: "Sem chave", scope: WithRemote);
        (await ErrorAsync(unregistered, HttpMethod.Post, RemoteRoutes.Operations, Sign(phone.Key, Inspection(workspace, phone, notebook), RemoteJws.OperationType)))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.NotRegistered));
    }

    [Fact]
    public async Task TheLifecycle_RefusesRegressionsAndTheServersOwnMoves_AndRepeatingIsANoOp()
    {
        var (phone, notebook, workspace) = await PairAsync();
        var operation = await CreateAsync(phone, Inspection(workspace, phone, notebook));
        var id = operation.OperationId;

        await GetAsync<List<AgentOperation>>(notebook.Session, RemoteRoutes.AgentOperations);
        (await ErrorAsync(notebook.Session, HttpMethod.Post, RemoteRoutes.OperationStatus(id), new OperationStatusUpdate(RemoteOperationStatus.Running)))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.InvalidTransition));
        (await ErrorAsync(notebook.Session, HttpMethod.Post, RemoteRoutes.OperationStatus(id), new OperationStatusUpdate(RemoteOperationStatus.Expired)))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.InvalidTransition), "expirar é do servidor");

        await MoveAsync(notebook, id, RemoteOperationStatus.Accepted);
        (await MoveAsync(notebook, id, RemoteOperationStatus.Accepted)).Status.Should().Be(RemoteOperationStatus.Accepted, "repetir não muda nada");
        await MoveAsync(notebook, id, RemoteOperationStatus.Running);
        await MoveAsync(notebook, id, RemoteOperationStatus.Failed, RemoteErrorCodes.ExecutionFailed, "git saiu com 128");

        (await ErrorAsync(notebook.Session, HttpMethod.Post, RemoteRoutes.OperationStatus(id), new OperationStatusUpdate(RemoteOperationStatus.Running)))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.InvalidTransition), "um estado final não volta");

        // Só a máquina alvo fala pela operação.
        (await ErrorAsync(phone.Session, HttpMethod.Post, RemoteRoutes.OperationStatus(id), new OperationStatusUpdate(RemoteOperationStatus.Succeeded)))
            .Should().Be((HttpStatusCode.Forbidden, RemoteErrorCodes.WrongTarget));

        var final = await GetAsync<RemoteOperationView>(phone.Session, RemoteRoutes.Operation(id));
        final.Status.Should().Be(RemoteOperationStatus.Failed);
        final.ResultCode.Should().Be(RemoteErrorCodes.ExecutionFailed);
        final.ResultSummary.Should().Be("git saiu com 128");
    }

    [Fact]
    public async Task Events_AreKeptOncePerSeq_AndTheAckStopsAtTheFirstGap()
    {
        var (phone, notebook, workspace) = await PairAsync();
        var id = (await CreateAsync(phone, Inspection(workspace, phone, notebook))).OperationId;

        (await AppendAsync(notebook, id, Event(id, 1), Event(id, 2), Event(id, 4))).LastContiguousSeq.Should().Be(2);
        (await AppendAsync(notebook, id, Event(id, 1, "repetido"), Event(id, 2))).LastContiguousSeq.Should().Be(2, "o repetido é ignorado");
        (await AppendAsync(notebook, id, Event(id, 3, new string('x', RemoteLimits.MaxEventTextLength + 10)))).LastContiguousSeq.Should().Be(4);

        var page = await GetAsync<RemoteEventPage>(phone.Session, RemoteRoutes.OperationEvents(id));
        page.Events.Select(item => item.Seq).Should().Equal(1, 2, 3, 4);
        page.Events[0].Text.Should().Be("linha 1", "o primeiro gravado fica");
        page.Events[2].Text.Should().HaveLength(RemoteLimits.MaxEventTextLength);
        page.Events[3].Data!.Value.GetProperty("exitCode").GetInt32().Should().Be(0);
        page.LastContiguousSeq.Should().Be(4);
        page.HasMore.Should().BeFalse();

        var next = await GetAsync<RemoteEventPage>(phone.Session, RemoteRoutes.OperationEvents(id) + "?after=2&limit=1");
        next.Events.Should().ContainSingle().Which.Seq.Should().Be(3);
        next.HasMore.Should().BeTrue();

        (await GetAsync<RemoteOperationView>(phone.Session, RemoteRoutes.Operation(id))).LastContiguousSeq.Should().Be(4);

        // Só a máquina alvo manda eventos, e de no máximo um lote cheio.
        (await ErrorAsync(phone.Session, HttpMethod.Post, RemoteRoutes.OperationEvents(id), new EventBatch(id, [Event(id, 5)])))
            .Should().Be((HttpStatusCode.Forbidden, RemoteErrorCodes.WrongTarget));
        (await ErrorAsync(notebook.Session, HttpMethod.Post, RemoteRoutes.OperationEvents(id),
                new EventBatch(id, [.. Enumerable.Range(5, RemoteLimits.MaxEventsPerBatch + 1).Select(seq => Event(id, seq))])))
            .Status.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AboveTheCeiling_OutputIsAcknowledgedButDropped_AndTheRestIsKept()
    {
        var (phone, notebook, workspace) = await PairAsync();
        var id = (await CreateAsync(phone, Inspection(workspace, phone, notebook))).OperationId;

        await _kit.WithDatabaseAsync(database => database.RemoteOperations
            .Where(row => row.OperationId == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.EventCount, RemoteLimits.MaxEventsPerOperation), Ct));

        var output = Event(id, 1) with { Kind = RemoteEventKind.Output };
        var result = Event(id, 2) with { Kind = RemoteEventKind.Result };

        (await AppendAsync(notebook, id, output, result)).LastContiguousSeq.Should().Be(2);
        (await GetAsync<RemoteEventPage>(phone.Session, RemoteRoutes.OperationEvents(id))).Events.Should().ContainSingle()
            .Which.Kind.Should().Be(RemoteEventKind.Result);
    }

    /// <summary>Entregue sem aceite a tempo volta para a fila; o agente que já a tinha ainda aceita.</summary>
    [Fact]
    public async Task ADispatchWithoutAcceptance_GoesBackToTheQueue_AndCanStillBeAccepted()
    {
        var (phone, notebook, workspace) = await PairAsync();
        var id = (await CreateAsync(phone, Inspection(workspace, phone, notebook))).OperationId;
        await GetAsync<List<AgentOperation>>(notebook.Session, RemoteRoutes.AgentOperations);

        await _kit.WithDatabaseAsync(database => database.RemoteOperations
            .Where(row => row.OperationId == id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.DispatchedAt, DateTimeOffset.UtcNow - RemoteLimits.DispatchTimeout - TimeSpan.FromSeconds(1)), Ct));

        (await SweepAsync()).Requeued.Should().ContainSingle().Which.OperationId.Should().Be(id);
        (await GetAsync<RemoteOperationView>(phone.Session, RemoteRoutes.Operation(id))).Status.Should().Be(RemoteOperationStatus.Queued);

        (await MoveAsync(notebook, id, RemoteOperationStatus.Accepted)).Status.Should().Be(RemoteOperationStatus.Accepted);
    }

    [Fact]
    public async Task PastTheDeadline_TheOperationExpires_AndIsNoLongerDelivered()
    {
        var (phone, notebook, workspace) = await PairAsync();
        var onTime = (await CreateAsync(phone, Inspection(workspace, phone, notebook))).OperationId;
        var dispatched = (await CreateAsync(phone, Inspection(workspace, phone, notebook))).OperationId;
        var late = (await CreateAsync(phone, Inspection(workspace, phone, notebook))).OperationId;

        await GetAsync<List<AgentOperation>>(notebook.Session, RemoteRoutes.AgentOperations);
        var waiting = (await CreateAsync(phone, Inspection(workspace, phone, notebook))).OperationId;

        await _kit.WithDatabaseAsync(database => database.RemoteOperations
            .Where(row => row.OperationId == waiting || row.OperationId == dispatched || row.OperationId == late)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.NotAfter, DateTimeOffset.UtcNow.AddSeconds(-1)), Ct));

        // Aceitar depois do prazo, antes da varredura: vence na hora.
        (await ErrorAsync(notebook.Session, HttpMethod.Post, RemoteRoutes.OperationStatus(late), new OperationStatusUpdate(RemoteOperationStatus.Accepted)))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.AlreadyExpired));

        (await SweepAsync()).Expired.Select(operation => operation.OperationId).Should().BeEquivalentTo([waiting, dispatched]);

        foreach (var id in new[] { waiting, dispatched, late })
        {
            (await GetAsync<RemoteOperationView>(phone.Session, RemoteRoutes.Operation(id))).Status.Should().Be(RemoteOperationStatus.Expired);
        }

        (await GetAsync<List<AgentOperation>>(notebook.Session, RemoteRoutes.AgentOperations)).Should().ContainSingle("só a que ainda está no prazo")
            .Which.Status.Should().Be(RemoteOperationStatus.Dispatched);
        (await GetAsync<RemoteOperationView>(phone.Session, RemoteRoutes.Operation(onTime))).Status.Should().Be(RemoteOperationStatus.Dispatched);
        (await ErrorAsync(notebook.Session, HttpMethod.Post, RemoteRoutes.OperationStatus(dispatched), new OperationStatusUpdate(RemoteOperationStatus.Accepted)))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.InvalidTransition));
    }

    [Fact]
    public async Task CancelingBeforeAcceptance_Cancels_AndAfterwards_OnlyAsksTheAgent()
    {
        var (phone, notebook, workspace) = await PairAsync();
        var early = (await CreateAsync(phone, Inspection(workspace, phone, notebook))).OperationId;

        (await SendAsync<RemoteOperationView>(phone.Session, HttpMethod.Post, RemoteRoutes.CancelOperation(early), null))
            .Status.Should().Be(RemoteOperationStatus.Canceled);
        (await ErrorAsync(notebook.Session, HttpMethod.Post, RemoteRoutes.OperationStatus(early), new OperationStatusUpdate(RemoteOperationStatus.Accepted)))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.InvalidTransition));

        var running = (await CreateAsync(phone, Inspection(workspace, phone, notebook))).OperationId;
        await GetAsync<List<AgentOperation>>(notebook.Session, RemoteRoutes.AgentOperations);
        await MoveAsync(notebook, running, RemoteOperationStatus.Accepted);
        await MoveAsync(notebook, running, RemoteOperationStatus.Running);

        var asked = await SendAsync<RemoteOperationView>(phone.Session, HttpMethod.Post, RemoteRoutes.CancelOperation(running), null);
        asked.Status.Should().Be(RemoteOperationStatus.Running, "depois do aceite, quem para é o agente");
        asked.CancelRequestedAt.Should().NotBeNull();

        (await GetAsync<List<AgentOperation>>(notebook.Session, RemoteRoutes.AgentOperations)).Should().ContainSingle()
            .Which.CancelRequestedAt.Should().Be(asked.CancelRequestedAt);
        (await MoveAsync(notebook, running, RemoteOperationStatus.Canceled)).Status.Should().Be(RemoteOperationStatus.Canceled);

        // Cancelar o que já terminou não muda nada.
        (await SendAsync<RemoteOperationView>(phone.Session, HttpMethod.Post, RemoteRoutes.CancelOperation(running), null))
            .Status.Should().Be(RemoteOperationStatus.Canceled);
    }

    [Fact]
    public async Task AnotherAccount_SeesNothing()
    {
        var (phone, notebook, workspace) = await PairAsync();
        var id = (await CreateAsync(phone, Inspection(workspace, phone, notebook))).OperationId;
        var stranger = await _kit.DeviceAsync("PC da Bia", DeviceRoles.Controller | DeviceRoles.Executor, "bia");

        (await GetAsync<List<RemoteOperationView>>(stranger.Session, RemoteRoutes.Operations)).Should().BeEmpty();
        (await GetAsync<List<AgentOperation>>(stranger.Session, RemoteRoutes.AgentOperations)).Should().BeEmpty();

        string[] paths = [RemoteRoutes.Operation(id), RemoteRoutes.OperationEvents(id)];

        foreach (var path in paths)
        {
            (await ErrorAsync(stranger.Session, HttpMethod.Get, path, null)).Status.Should().Be(HttpStatusCode.NotFound);
        }

        (await ErrorAsync(stranger.Session, HttpMethod.Post, RemoteRoutes.CancelOperation(id), null)).Status.Should().Be(HttpStatusCode.NotFound);
        (await ErrorAsync(stranger.Session, HttpMethod.Post, RemoteRoutes.OperationStatus(id), new OperationStatusUpdate(RemoteOperationStatus.Accepted)))
            .Status.Should().Be(HttpStatusCode.NotFound);

        // O mesmo id, assinado por outra conta, não serve.
        var copy = Inspection(await stranger.Session.WorkspaceAsync(), stranger, stranger) with { OperationId = id };
        (await ErrorAsync(stranger.Session, HttpMethod.Post, RemoteRoutes.Operations, Sign(stranger.Key, copy, RemoteJws.OperationType)))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.IdempotencyKeyReuse));
    }

    [Fact]
    public async Task ARevokedMachine_IsNeitherATargetNorAnAgent()
    {
        var (phone, notebook, workspace) = await PairAsync();
        await CreateAsync(phone, Inspection(workspace, phone, notebook));

        await phone.Session.SendAsync<object, DeviceInfo>(HttpMethod.Post, ApiRoutes.RevokeDevice(notebook.DeviceId), new { });

        (await ErrorAsync(notebook.Session, HttpMethod.Get, RemoteRoutes.AgentOperations, null))
            .Should().Be((HttpStatusCode.Forbidden, SyncErrorCodes.DeviceRevoked));
        (await ErrorAsync(phone.Session, HttpMethod.Post, RemoteRoutes.Operations, Sign(phone.Key, Inspection(workspace, phone, notebook), RemoteJws.OperationType)))
            .Status.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task OldEvents_AreSweptAway()
    {
        var (phone, notebook, workspace) = await PairAsync();
        var id = (await CreateAsync(phone, Inspection(workspace, phone, notebook))).OperationId;
        await AppendAsync(notebook, id, Event(id, 1), Event(id, 2));

        await _kit.WithDatabaseAsync(database => database.RemoteOperationEvents
            .Where(row => row.OperationId == id && row.Seq == 1)
            .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.ReceivedAt, DateTimeOffset.UtcNow.AddDays(-31)), Ct));

        (await SweepAsync()).PrunedEvents.Should().Be(1);
        (await GetAsync<RemoteEventPage>(phone.Session, RemoteRoutes.OperationEvents(id))).Events.Should().ContainSingle().Which.Seq.Should().Be(2);
    }

    private async Task<(RemoteDevice Phone, RemoteDevice Notebook, Guid Workspace)> PairAsync()
    {
        var phone = await _kit.DeviceAsync("Celular", DeviceRoles.Controller);
        var notebook = await _kit.DeviceAsync("Notebook", DeviceRoles.Executor);

        return (phone, notebook, await phone.Session.WorkspaceAsync());
    }

    private async Task<RemoteSweepResult> SweepAsync()
    {
        await using var scope = _api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<RemoteOperationUseCases>().SweepAsync(TimeSpan.FromDays(30), Ct);
    }

    private static Task<EventBatchAck> AppendAsync(RemoteDevice agent, Guid operationId, params RemoteOperationEvent[] events) =>
        SendAsync<EventBatchAck>(agent.Session, HttpMethod.Post, RemoteRoutes.OperationEvents(operationId), new EventBatch(operationId, events));

    private static RemoteOperationEvent Event(Guid operationId, long seq, string? text = null) => new(
        operationId,
        seq,
        DateTimeOffset.UtcNow,
        RemoteEventKind.StepCompleted,
        "inspect",
        null,
        text ?? $"linha {seq}",
        JsonSerializer.SerializeToElement(new { exitCode = 0 }));
}
