using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Application;
using static MyTaskApp.Server.IntegrationTests.RemoteTestKit;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// As aprovações (fase 2, T-19; ADR-086), de ponta a ponta: o agente pede
/// selando o que a pessoa vai ver, o celular responde assinando, e a
/// resposta é de uso único, para aquele pedido e dentro do prazo.
/// </summary>
public sealed class RemoteApprovalTests(PostgresFixture postgres) : IAsyncLifetime
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
    public async Task TheAgentAsks_TheAccountSees_AndTheSignedAnswerReachesTheAgentAsItWasSent()
    {
        var (phone, notebook, operationId) = await RunningAsync();
        await using var agent = await _kit.ListenAsync(notebook.Session);
        await using var controller = await _kit.ListenAsync(phone.Session);
        var request = Request(operationId);

        var asked = await AskAsync(notebook, request);
        asked.State.Should().Be(ApprovalState.Pending);
        asked.TargetDeviceId.Should().Be(notebook.DeviceId);
        await controller.ExpectAsync(RemoteHubMessages.ApprovalRequested, request.ApprovalId, operationId);

        var pending = (await GetAsync<List<ApprovalView>>(phone.Session, RemoteRoutes.Approvals)).Should().ContainSingle().Subject;
        pending.Request.Summary.Should().Be(request.Summary);
        ApprovalHash.IsSealed(pending.Request).Should().BeTrue("o pedido volta com os mesmos bytes que o agente selou");

        var answer = Answer(phone, request, ApprovalDecision.Allow);
        var answered = await SendAsync<ApprovalView>(phone.Session, HttpMethod.Post, RemoteRoutes.ApprovalResponse(request.ApprovalId), answer);

        answered.State.Should().Be(ApprovalState.Allowed);
        answered.RespondedByDeviceId.Should().Be(phone.DeviceId);
        await agent.ExpectAsync(RemoteHubMessages.ApprovalResolved, request.ApprovalId, operationId);

        // O agente confere a assinatura com a chave em que confia, e não a palavra do servidor.
        var seen = await GetAsync<ApprovalView>(notebook.Session, RemoteRoutes.Approval(request.ApprovalId));
        seen.Response.Should().Be(answer);
        RemoteJws.TryVerify<ApprovalResponse>(seen.Response!, RemoteJws.ApprovalType, DevicePublicKey.Es256Of(phone.Key.ExportSubjectPublicKeyInfo()), out var read)
            .Should().BeTrue();
        read.Decision.Should().Be(ApprovalDecision.Allow);
        (await GetAsync<List<ApprovalView>>(phone.Session, RemoteRoutes.Approvals)).Should().BeEmpty();
    }

    [Fact]
    public async Task AnAnswer_IsSingleUse_ButResendingTheSameOneIsHarmless()
    {
        var (phone, notebook, operationId) = await RunningAsync();
        var otherPhone = await _kit.DeviceAsync("Outro celular", DeviceRoles.Controller);
        var request = Request(operationId);
        await AskAsync(notebook, request);

        var allow = Answer(phone, request, ApprovalDecision.Allow);
        await SendAsync<ApprovalView>(phone.Session, HttpMethod.Post, RemoteRoutes.ApprovalResponse(request.ApprovalId), allow);

        (await SendAsync<ApprovalView>(phone.Session, HttpMethod.Post, RemoteRoutes.ApprovalResponse(request.ApprovalId), allow))
            .State.Should().Be(ApprovalState.Allowed, "a resposta que se perdeu, reenviada");
        (await ErrorAsync(phone.Session, HttpMethod.Post, RemoteRoutes.ApprovalResponse(request.ApprovalId), Answer(phone, request, ApprovalDecision.Deny)))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.ApprovalAnswered));
        (await ErrorAsync(otherPhone.Session, HttpMethod.Post, RemoteRoutes.ApprovalResponse(request.ApprovalId), Answer(otherPhone, request, ApprovalDecision.Deny)))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.ApprovalAnswered));

        // O mesmo pedido de novo devolve a aprovação; outro com o mesmo id, não.
        (await AskAsync(notebook, request)).State.Should().Be(ApprovalState.Allowed);
        (await ErrorAsync(notebook.Session, HttpMethod.Post, RemoteRoutes.OperationApprovals(operationId), ApprovalHash.Seal(request with { Summary = "Outro" })))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.IdempotencyKeyReuse));
    }

    /// <summary>A resposta tem de ser para este pedido, deste tipo, e assinada pela chave ativa de quem manda.</summary>
    [Fact]
    public async Task AnAnswerForAnotherRequest_OrNotSignedByTheCaller_IsRefused()
    {
        var (phone, notebook, operationId) = await RunningAsync();
        var request = Request(operationId);
        await AskAsync(notebook, request);
        var path = RemoteRoutes.ApprovalResponse(request.ApprovalId);
        var response = new ApprovalResponse(request.ApprovalId, operationId, request.RequestHash, ApprovalDecision.Allow, null, false, DateTimeOffset.UtcNow);

        (await ErrorAsync(phone.Session, HttpMethod.Post, path, Sign(phone.Key, response with { RequestHash = new string('0', 64) }, RemoteJws.ApprovalType)))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.HashMismatch));
        (await ErrorAsync(phone.Session, HttpMethod.Post, path, Sign(phone.Key, response with { OperationId = Guid.CreateVersion7() }, RemoteJws.ApprovalType)))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.HashMismatch));
        (await ErrorAsync(phone.Session, HttpMethod.Post, path, Sign(phone.Key, response with { ApprovalId = Guid.CreateVersion7() }, RemoteJws.ApprovalType)))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.HashMismatch));
        (await ErrorAsync(phone.Session, HttpMethod.Post, path, Sign(notebook.Key, response, RemoteJws.ApprovalType)))
            .Should().Be((HttpStatusCode.Forbidden, RemoteErrorCodes.UntrustedSignature));
        (await ErrorAsync(phone.Session, HttpMethod.Post, path, Sign(phone.Key, response, RemoteJws.OperationType)))
            .Should().Be((HttpStatusCode.Forbidden, RemoteErrorCodes.UntrustedSignature), "uma operação assinada nunca vale de aprovação");

        (await GetAsync<ApprovalView>(phone.Session, RemoteRoutes.Approval(request.ApprovalId))).State.Should().Be(ApprovalState.Pending);
    }

    [Fact]
    public async Task OnlyASealedRequest_FromTheTarget_ForARunningOperation_IsAccepted()
    {
        var (phone, notebook, operationId) = await RunningAsync();
        var path = RemoteRoutes.OperationApprovals(operationId);
        var request = Request(operationId);

        (await ErrorAsync(notebook.Session, HttpMethod.Post, path, request with { Summary = "Rodar: outra coisa" }))
            .Should().Be((HttpStatusCode.BadRequest, RemoteErrorCodes.HashMismatch), "o resumo mudou depois de selado");
        (await ErrorAsync(notebook.Session, HttpMethod.Post, path, ApprovalHash.Seal(request with { ExpiresAt = DateTimeOffset.UtcNow.AddHours(2) })))
            .Should().Be((HttpStatusCode.BadRequest, RemoteErrorCodes.WindowTooLong));
        (await ErrorAsync(notebook.Session, HttpMethod.Post, path, ApprovalHash.Seal(request with { Summary = new string('x', RemoteLimits.MaxApprovalSummaryLength + 1) })))
            .Status.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(notebook.Session, HttpMethod.Post, path, ApprovalHash.Seal(request with { Summary = "Rodar‮: tset tendod" })))
            .Status.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorAsync(phone.Session, HttpMethod.Post, path, request))
            .Should().Be((HttpStatusCode.Forbidden, RemoteErrorCodes.WrongTarget));

        await MoveAsync(notebook, operationId, RemoteOperationStatus.Succeeded);
        (await ErrorAsync(notebook.Session, HttpMethod.Post, path, request))
            .Should().Be((HttpStatusCode.Conflict, RemoteErrorCodes.InvalidTransition), "a operação já terminou");

        (await GetAsync<List<ApprovalView>>(phone.Session, RemoteRoutes.Approvals)).Should().BeEmpty();
    }

    [Fact]
    public async Task PastItsDeadline_TheApprovalExpires_AndCannotBeAnswered()
    {
        var (phone, notebook, operationId) = await RunningAsync();
        var answered = Request(operationId, TimeSpan.FromSeconds(1));
        var swept = Request(operationId, TimeSpan.FromSeconds(1));
        await AskAsync(notebook, answered);
        await AskAsync(notebook, swept);

        await Task.Delay(TimeSpan.FromSeconds(1.5), Ct);

        (await GetAsync<List<ApprovalView>>(phone.Session, RemoteRoutes.Approvals)).Should().BeEmpty();
        (await ErrorAsync(phone.Session, HttpMethod.Post, RemoteRoutes.ApprovalResponse(answered.ApprovalId), Answer(phone, answered, ApprovalDecision.Allow)))
            .Should().Be((HttpStatusCode.Gone, RemoteErrorCodes.ApprovalExpired));

        await using (var scope = _api.Services.CreateAsyncScope())
        {
            await scope.ServiceProvider.GetRequiredService<RemoteApprovalUseCases>().SweepAsync(Ct);
        }

        foreach (var id in new[] { answered.ApprovalId, swept.ApprovalId })
        {
            var view = await GetAsync<ApprovalView>(notebook.Session, RemoteRoutes.Approval(id));
            view.State.Should().Be(ApprovalState.Expired);
            view.Response.Should().BeNull();
        }

        (await ErrorAsync(phone.Session, HttpMethod.Post, RemoteRoutes.ApprovalResponse(swept.ApprovalId), Answer(phone, swept, ApprovalDecision.Allow)))
            .Should().Be((HttpStatusCode.Gone, RemoteErrorCodes.ApprovalExpired));
    }

    /// <summary>
    /// A operação terminou (aqui, cancelada) com a pergunta aberta: ela fecha
    /// como vencida, sai da lista de todos e não aceita mais resposta.
    /// </summary>
    [Fact]
    public async Task WhenTheOperationEnds_ItsPendingApprovalCloses_IsNotListed_AndCannotBeAnswered()
    {
        var (phone, notebook, operationId) = await RunningAsync();
        var request = Request(operationId);
        await AskAsync(notebook, request);
        await using var controller = await _kit.ListenAsync(phone.Session);

        await MoveAsync(notebook, operationId, RemoteOperationStatus.Canceled);

        await controller.ExpectAsync(RemoteHubMessages.ApprovalResolved, request.ApprovalId, operationId);
        (await GetAsync<List<ApprovalView>>(phone.Session, RemoteRoutes.Approvals)).Should().BeEmpty();
        (await GetAsync<ApprovalView>(notebook.Session, RemoteRoutes.Approval(request.ApprovalId))).State.Should().Be(ApprovalState.Expired);
        (await ErrorAsync(phone.Session, HttpMethod.Post, RemoteRoutes.ApprovalResponse(request.ApprovalId), Answer(phone, request, ApprovalDecision.Allow)))
            .Should().Be((HttpStatusCode.Gone, RemoteErrorCodes.ApprovalExpired));
    }

    /// <summary>Uma pendente gravada antes desta versão, de operação já terminada, não aparece nem é respondida.</summary>
    [Fact]
    public async Task AStaleApprovalOfAFinishedOperation_IsNotListed_AndAnsweringItClosesIt()
    {
        var (phone, notebook, operationId) = await RunningAsync();
        var request = Request(operationId);
        await AskAsync(notebook, request);
        await _kit.WithDatabaseAsync(async context =>
        {
            var operation = await context.RemoteOperations.SingleAsync(row => row.OperationId == operationId, Ct);
            operation.Status = (int)RemoteOperationStatus.Failed;
            await context.SaveChangesAsync(Ct);
        });

        (await GetAsync<List<ApprovalView>>(phone.Session, RemoteRoutes.Approvals)).Should().BeEmpty();
        (await ErrorAsync(phone.Session, HttpMethod.Post, RemoteRoutes.ApprovalResponse(request.ApprovalId), Answer(phone, request, ApprovalDecision.Allow)))
            .Should().Be((HttpStatusCode.Gone, RemoteErrorCodes.ApprovalExpired));
        (await GetAsync<ApprovalView>(notebook.Session, RemoteRoutes.Approval(request.ApprovalId))).State.Should().Be(ApprovalState.Expired);
    }

    [Fact]
    public async Task AnotherAccount_SeesNothing()
    {
        var (_, notebook, operationId) = await RunningAsync();
        var stranger = await _kit.DeviceAsync("PC da Bia", DeviceRoles.Controller | DeviceRoles.Executor, "bia");
        var request = Request(operationId);
        await AskAsync(notebook, request);

        (await GetAsync<List<ApprovalView>>(stranger.Session, RemoteRoutes.Approvals)).Should().BeEmpty();
        (await ErrorAsync(stranger.Session, HttpMethod.Get, RemoteRoutes.Approval(request.ApprovalId), null)).Status.Should().Be(HttpStatusCode.NotFound);
        (await ErrorAsync(stranger.Session, HttpMethod.Post, RemoteRoutes.ApprovalResponse(request.ApprovalId), Answer(stranger, request, ApprovalDecision.Allow)))
            .Status.Should().Be(HttpStatusCode.NotFound);
        (await ErrorAsync(stranger.Session, HttpMethod.Post, RemoteRoutes.OperationApprovals(operationId), Request(operationId)))
            .Status.Should().Be(HttpStatusCode.NotFound);
    }

    /// <summary>Uma operação aceita e rodando no notebook, pedida pelo celular.</summary>
    private async Task<(RemoteDevice Phone, RemoteDevice Notebook, Guid OperationId)> RunningAsync()
    {
        var phone = await _kit.DeviceAsync("Celular", DeviceRoles.Controller);
        var notebook = await _kit.DeviceAsync("Notebook", DeviceRoles.Executor);
        var operation = await CreateAsync(phone, Inspection(await phone.Session.WorkspaceAsync(), phone, notebook));

        await GetAsync<List<AgentOperation>>(notebook.Session, RemoteRoutes.AgentOperations);
        await MoveAsync(notebook, operation.OperationId, RemoteOperationStatus.Accepted);
        await MoveAsync(notebook, operation.OperationId, RemoteOperationStatus.Running);

        return (phone, notebook, operation.OperationId);
    }

    private static ApprovalRequest Request(Guid operationId, TimeSpan? lifetime = null) => ApprovalHash.Seal(new ApprovalRequest(
        Guid.CreateVersion7(),
        operationId,
        ApprovalKind.CommandConfirmation,
        "Rodar: dotnet test",
        JsonSerializer.SerializeToElement(new { command = "dotnet test", directory = "api" }),
        null,
        DateTimeOffset.UtcNow + (lifetime ?? RemoteLimits.DefaultApprovalLifetime),
        string.Empty));

    private static Task<ApprovalView> AskAsync(RemoteDevice agent, ApprovalRequest request) =>
        SendAsync<ApprovalView>(agent.Session, HttpMethod.Post, RemoteRoutes.OperationApprovals(request.OperationId), request, HttpStatusCode.Created);

    private static SignedEnvelope Answer(RemoteDevice controller, ApprovalRequest request, ApprovalDecision decision) =>
        Sign(controller.Key, new ApprovalResponse(request.ApprovalId, request.OperationId, request.RequestHash, decision, null, false, DateTimeOffset.UtcNow), RemoteJws.ApprovalType);
}
