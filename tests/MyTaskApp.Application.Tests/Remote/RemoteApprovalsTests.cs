using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Remote;
using MyTaskApp.Remote.Contracts;
using NSubstitute;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>
/// A aprovação do lado do agente (seção 4.3, T-19): a operação para em
/// Esperando aprovação, e só segue com a resposta assinada por um controlador
/// pareado para <b>este</b> pedido. Sem resposta, ou com uma que não confere, é negar.
/// </summary>
public sealed class RemoteApprovalsTests
{
    private static readonly RemoteApprovalAsk RemoveWorktree = new(
        ApprovalKind.WorktreeRemoval,
        "Apagar o worktree de ECO-123 (2 arquivos não salvos).",
        JsonSerializer.SerializeToElement(new { files = 2 }));

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AnAllowSignedByThePairedPhoneLetsTheOperationGoOn()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var outcome = Asking(kit, RemoveWorktree);
        var request = await QueuedAsync(kit);

        var asked = await kit.Server.AskedAsync(request.OperationId);
        Assert.Equal(RemoteOperationStatus.WaitingApproval, kit.Server.StatusOf(request.OperationId));
        Assert.True(ApprovalHash.IsSealed(asked));
        Assert.Equal(RemoveWorktree.Summary, asked.Summary);
        Assert.Equal(kit.Time.GetUtcNow() + RemoteLimits.DefaultApprovalLifetime, asked.ExpiresAt);

        await kit.Server.RespondApprovalAsync(asked.ApprovalId, kit.Phone.Answer(asked, ApprovalDecision.Allow, kit.Time.GetUtcNow()), Ct);
        kit.Hub.RingApproval(asked.ApprovalId);

        Assert.Equal(RemoteOperationStatus.Succeeded, await kit.WaitFinishedAsync(request.OperationId));
        var result = await outcome.Task;
        Assert.Equal(RemoteApprovalOutcome.Allowed, result.Outcome);
        Assert.Equal("Pixel 8", result.ControllerNickname);
        Assert.Equal(
            [RemoteOperationStatus.Accepted, RemoteOperationStatus.Running, RemoteOperationStatus.WaitingApproval, RemoteOperationStatus.Running, RemoteOperationStatus.Succeeded],
            kit.Server.StatusesOf(request.OperationId));

        var events = kit.Server.EventsOf(request.OperationId);
        Assert.Contains(events, item => item.Kind == RemoteEventKind.ApprovalRequested && item.Text == RemoveWorktree.Summary);
        Assert.Contains(events, item => item.Kind == RemoteEventKind.ApprovalResolved && item.Text == "Permitido por Pixel 8.");
    }

    [Fact]
    public async Task ADenyEndsTheOperationWithTheReason()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var outcome = Asking(kit, RemoveWorktree);
        var request = await QueuedAsync(kit);

        var asked = await kit.Server.AskedAsync(request.OperationId);
        await kit.Server.RespondApprovalAsync(asked.ApprovalId, kit.Phone.Answer(asked, ApprovalDecision.Deny, kit.Time.GetUtcNow()), Ct);
        kit.Hub.RingApproval(asked.ApprovalId);

        Assert.Equal(RemoteOperationStatus.Failed, await kit.WaitFinishedAsync(request.OperationId));
        Assert.Equal(RemoteApprovalOutcome.Denied, (await outcome.Task).Outcome);
        var last = kit.Server.Updates[request.OperationId][^1];
        Assert.Equal(RemoteErrorCodes.ApprovalDenied, last.Code);
        Assert.Equal("Apagar: negado por Pixel 8.", last.Summary);
    }

    [Fact]
    public async Task WithoutTheHubTheAnswerArrivesByAskingAgain()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        _ = Asking(kit, RemoveWorktree);
        var request = await QueuedAsync(kit);

        var asked = await kit.Server.AskedAsync(request.OperationId);
        await kit.Server.RespondApprovalAsync(asked.ApprovalId, kit.Phone.Answer(asked, ApprovalDecision.Allow, kit.Time.GetUtcNow()), Ct);

        Assert.Equal(RemoteOperationStatus.Succeeded, await AdvanceUntilFinishedAsync(kit, request.OperationId, RemoteOperationDispatcher.ApprovalPollInterval));
        Assert.True(kit.Server.ApprovalReads >= 1);
    }

    [Fact]
    public async Task NoAnswerUntilTheDeadlineIsADeny()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var outcome = Asking(kit, RemoveWorktree);
        var request = await QueuedAsync(kit);
        _ = await kit.Server.AskedAsync(request.OperationId);

        Assert.Equal(RemoteOperationStatus.Failed, await AdvanceUntilFinishedAsync(kit, request.OperationId, TimeSpan.FromMinutes(1)));
        Assert.Equal(RemoteApprovalOutcome.Expired, (await outcome.Task).Outcome);
        Assert.Equal(RemoteErrorCodes.ApprovalExpired, kit.Server.Updates[request.OperationId][^1].Code);
    }

    [Fact]
    public async Task TheServerSayingItExpiredIsADenyRightAway()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var outcome = Asking(kit, RemoveWorktree);
        var request = await QueuedAsync(kit);

        var asked = await kit.Server.AskedAsync(request.OperationId);
        kit.Server.ExpireApproval(asked.ApprovalId);
        kit.Hub.RingApproval(asked.ApprovalId);

        Assert.Equal(RemoteOperationStatus.Failed, await kit.WaitFinishedAsync(request.OperationId));
        Assert.Equal(RemoteApprovalOutcome.Expired, (await outcome.Task).Outcome);
    }

    public static TheoryData<string> Forgeries => ["outro pedido", "outra aprovação", "fora do prazo", "chave desconhecida", "controlador revogado"];

    /// <summary>O servidor (ou quem o tomou) não fabrica um "permitir": cada um destes volta negado.</summary>
    [Theory]
    [MemberData(nameof(Forgeries))]
    public async Task AnAllowThatDoesNotMatchThisRequestIsADeny(string forgery)
    {
        await using var kit = new RemoteDispatchKit();
        var trusted = kit.Trust(kit.Phone);
        using var stranger = new Controller("Intruso");
        var outcome = Asking(kit, RemoveWorktree);
        var request = await QueuedAsync(kit);
        var asked = await kit.Server.AskedAsync(request.OperationId);
        var now = kit.Time.GetUtcNow();

        var answer = forgery switch
        {
            "outro pedido" => kit.Phone.Answer(asked, ApprovalDecision.Allow, now, change: response => response with { RequestHash = new string('0', 64) }),
            "outra aprovação" => kit.Phone.Answer(asked, ApprovalDecision.Allow, now, change: response => response with { ApprovalId = Guid.CreateVersion7() }),
            "fora do prazo" => kit.Phone.Answer(asked, ApprovalDecision.Allow, asked.ExpiresAt.AddSeconds(1)),
            "chave desconhecida" => stranger.Answer(asked, ApprovalDecision.Allow, now),
            _ => kit.Phone.Answer(asked, ApprovalDecision.Allow, now),
        };

        if (forgery == "controlador revogado")
        {
            trusted.Revoke(now);
        }

        await kit.Server.RespondApprovalAsync(asked.ApprovalId, answer, Ct);
        kit.Hub.RingApproval(asked.ApprovalId);

        Assert.Equal(RemoteOperationStatus.Failed, await kit.WaitFinishedAsync(request.OperationId));
        Assert.Equal(RemoteApprovalOutcome.Invalid, (await outcome.Task).Outcome);
        Assert.Contains(kit.Server.EventsOf(request.OperationId), item => item.Text == "A resposta não conferiu: negado.");
    }

    [Fact]
    public async Task ACancelWhileWaitingEndsAsCanceled()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        _ = Asking(kit, RemoveWorktree);
        var request = await QueuedAsync(kit);
        _ = await kit.Server.AskedAsync(request.OperationId);

        kit.Server.RequestCancel(request.OperationId);
        await kit.Dispatcher.PollAsync(Ct);

        Assert.Equal(RemoteOperationStatus.Canceled, await kit.WaitFinishedAsync(request.OperationId));
        Assert.Equal(RemoteOperationStatus.Canceled, kit.Server.StatusOf(request.OperationId));
    }

    [Fact]
    public async Task OfflineAtFirstTheRequestIsSentWhenTheNetworkComesBack()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Executor.Behavior = async (execution, token) =>
        {
            kit.Server.Offline = true;
            started.TrySetResult();
            var result = await execution.RequestApprovalAsync(RemoveWorktree, token);
            return result.IsAllowed ? RemoteOperationResult.Success("Apagado.") : result.AsFailure("Apagar");
        };
        var request = await QueuedAsync(kit);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(10), Ct);

        kit.Server.Offline = false;
        var asking = kit.Server.AskedAsync(request.OperationId);

        while (!asking.IsCompleted)
        {
            kit.Time.Advance(RemoteOperationDispatcher.ApprovalPollInterval);
            await Task.WhenAny(asking, Task.Delay(20, Ct));
        }

        var asked = await asking;
        await kit.Server.RespondApprovalAsync(asked.ApprovalId, kit.Phone.Answer(asked, ApprovalDecision.Allow, kit.Time.GetUtcNow()), Ct);
        kit.Hub.RingApproval(asked.ApprovalId);

        Assert.Equal(RemoteOperationStatus.Succeeded, await AdvanceUntilFinishedAsync(kit, request.OperationId, RemoteOperationDispatcher.ApprovalPollInterval));
    }

    [Fact]
    public void ProductionNeedsTheTypedNameToCountAsAllow()
    {
        using var phone = new Controller("Pixel 8");
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var asked = ApprovalHash.Seal(new ApprovalRequest(
            Guid.CreateVersion7(), Guid.CreateVersion7(), ApprovalKind.ProductionDatabase, "Copiar de eco_prod.",
            JsonSerializer.SerializeToElement(new { }), "eco_prod", now.AddMinutes(10), string.Empty));
        var trusted = Trusting(phone, now);

        Assert.Equal(RemoteApprovalOutcome.Invalid, Verify(asked, phone.Answer(asked, ApprovalDecision.Allow, now), ApprovalState.Allowed, trusted).Outcome);
        Assert.Equal(RemoteApprovalOutcome.Invalid, Verify(asked, phone.Answer(asked, ApprovalDecision.Allow, now, "ECO_PROD"), ApprovalState.Allowed, trusted).Outcome);
        Assert.Equal(RemoteApprovalOutcome.Allowed, Verify(asked, phone.Answer(asked, ApprovalDecision.Allow, now, "eco_prod"), ApprovalState.Allowed, trusted).Outcome);
    }

    [Fact]
    public void AStateThatContradictsTheSignedDecisionIsNotTrusted()
    {
        using var phone = new Controller("Pixel 8");
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var asked = ApprovalHash.Seal(new ApprovalRequest(
            Guid.CreateVersion7(), Guid.CreateVersion7(), ApprovalKind.CommandConfirmation, "Rodar os testes.",
            JsonSerializer.SerializeToElement(new { }), null, now.AddMinutes(10), string.Empty));
        var trusted = Trusting(phone, now);

        Assert.Equal(RemoteApprovalOutcome.Invalid, Verify(asked, phone.Answer(asked, ApprovalDecision.Deny, now), ApprovalState.Allowed, trusted).Outcome);
        Assert.Equal(RemoteApprovalOutcome.Invalid, Verify(asked, phone.Answer(asked, ApprovalDecision.Allow, now), ApprovalState.Denied, trusted).Outcome);
        Assert.Null(RemoteApprovalVerifier.Verify(asked, new ApprovalView(asked, Guid.Empty, ApprovalState.Pending, null, null, null), trusted));
        Assert.Equal(
            RemoteApprovalOutcome.Expired,
            RemoteApprovalVerifier.Verify(asked, new ApprovalView(asked, Guid.Empty, ApprovalState.Allowed, null, null, null), trusted)!.Outcome);
    }

    /// <summary>Esta máquina como controladora: a resposta que ela assina é a que o agente de lá aceita.</summary>
    [Fact]
    public async Task TheAnswerThisMachineSignsIsOneTheOtherAgentAccepts()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        using var signer = new FakeDeviceSigner();
        var server = new FakeOperationQueue();
        var operationId = Guid.CreateVersion7();
        server.Enqueue(operationId, new SignedEnvelope("x.y.z"));
        var asked = (await server.RequestApprovalAsync(ApprovalHash.Seal(new ApprovalRequest(
            Guid.CreateVersion7(), operationId, ApprovalKind.CommandConfirmation, "Rodar os testes.",
            JsonSerializer.SerializeToElement(new { }), null, now.AddMinutes(10), string.Empty)), Ct)).Request;

        var answered = await new AnswerApprovalHandler(server, signer, time).HandleAsync(
            new AnswerApproval(asked.ApprovalId, ApprovalDecision.Allow, RememberForThisSession: true), Ct);

        var me = TrustedController.Approve(Guid.CreateVersion7(), "Desktop", signer.PublicKey.Fingerprint, signer.PublicKey.SubjectPublicKeyInfo, Guid.CreateVersion7(), now);
        var result = RemoteApprovalVerifier.Verify(asked, answered, [me])!;
        Assert.Equal(RemoteApprovalOutcome.Allowed, result.Outcome);
        Assert.True(result.RememberForThisSession);

        // Respondida, não se responde de novo; e um pedido adulterado não se responde.
        await Assert.ThrowsAsync<DomainException>(() =>
            new AnswerApprovalHandler(server, signer, time).HandleAsync(new AnswerApproval(asked.ApprovalId, ApprovalDecision.Deny), Ct));

        var tampered = asked with { ApprovalId = Guid.CreateVersion7(), Summary = "Outra coisa." };
        server.Approvals[tampered.ApprovalId] = new ApprovalView(tampered, Guid.Empty, ApprovalState.Pending, null, null, null);
        await Assert.ThrowsAsync<DomainException>(() =>
            new AnswerApprovalHandler(server, signer, time).HandleAsync(new AnswerApproval(tampered.ApprovalId, ApprovalDecision.Allow), Ct));
        Assert.DoesNotContain(tampered, (await new GetPendingApprovalsHandler(server).HandleAsync(new GetPendingApprovals(), Ct)).Select(view => view.Request));
    }

    /// <summary>As respostas a uma pergunta do Claude (ADR-093) vão assinadas e chegam ao agente como foram.</summary>
    [Fact]
    public async Task TheAnswersToAQuestion_AreSigned_AndReachTheAgent()
    {
        var now = new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(now);
        using var signer = new FakeDeviceSigner();
        var server = new FakeOperationQueue();
        var operationId = Guid.CreateVersion7();
        server.Enqueue(operationId, new SignedEnvelope("x.y.z"));
        var asked = (await server.RequestApprovalAsync(ApprovalHash.Seal(new ApprovalRequest(
            Guid.CreateVersion7(), operationId, ApprovalKind.ToolUse, "O Claude pergunta: Qual banco?",
            JsonSerializer.SerializeToElement(new { kind = "question" }), null, now.AddMinutes(10), string.Empty)), Ct)).Request;

        var answered = await new AnswerApprovalHandler(server, signer, time).HandleAsync(
            new AnswerApproval(asked.ApprovalId, ApprovalDecision.Allow, Answers: ["Docker"]), Ct);

        var me = TrustedController.Approve(Guid.CreateVersion7(), "Desktop", signer.PublicKey.Fingerprint, signer.PublicKey.SubjectPublicKeyInfo, Guid.CreateVersion7(), now);
        var result = RemoteApprovalVerifier.Verify(asked, answered, [me])!;
        Assert.Equal(RemoteApprovalOutcome.Allowed, result.Outcome);
        Assert.Equal(["Docker"], MyTaskApp.Application.Agents.HeadlessQuestions.ReadAnswers(result.Answers, 1)!);
    }

    /// <summary>Esta máquina como controladora: o pedido que ela assina é um que o agente de lá admite e roda.</summary>
    [Fact]
    public async Task TheOperationThisMachineSendsIsOneTheOtherAgentRuns()
    {
        await using var kit = new RemoteDispatchKit();
        using var signer = new FakeDeviceSigner();
        var desktop = new FakeDeviceIdentity();
        kit.Trusted.All.Add(TrustedController.Approve(
            desktop.DeviceId, "Desktop", signer.PublicKey.Fingerprint, signer.PublicKey.SubjectPublicKeyInfo, Guid.CreateVersion7(), kit.Time.GetUtcNow()));

        var sent = await new SendRemoteOperationHandler(kit.Server, signer, desktop, kit.Sync, kit.Time).HandleAsync(
            new SendRemoteOperation(
                kit.Identity.DeviceId,
                RemoteOperationKinds.EnvironmentInspect,
                JsonSerializer.SerializeToElement(new EnvironmentInspectionRequest(), RemoteJson.Options)),
            Ct);
        await kit.Dispatcher.PollAsync(Ct);

        Assert.Equal(RemoteOperationStatus.Succeeded, await kit.WaitFinishedAsync(sent.OperationId));
        Assert.Equal("Remoto: Desktop", kit.Executor.SeenOrigin);
    }

    [Fact]
    public async Task SendingWithoutSyncIsRefusedBeforeSigning()
    {
        await using var kit = new RemoteDispatchKit();
        using var signer = new FakeDeviceSigner();
        var sync = Substitute.For<ISyncLocalStore>();
        sync.ReadAsync(Arg.Any<CancellationToken>()).Returns(SyncLocalState.Unlinked);

        await Assert.ThrowsAsync<DomainException>(() => new SendRemoteOperationHandler(kit.Server, signer, new FakeDeviceIdentity(), sync, kit.Time).HandleAsync(
            new SendRemoteOperation(kit.Identity.DeviceId, RemoteOperationKinds.EnvironmentInspect, JsonSerializer.SerializeToElement(new EnvironmentInspectionRequest(), RemoteJson.Options)),
            Ct));
        Assert.Equal(0, signer.Signatures);
    }

    /// <summary>O executor programado pede a aprovação e segue só se permitida; o desfecho fica para o teste ver.</summary>
    private static TaskCompletionSource<RemoteApprovalResult> Asking(RemoteDispatchKit kit, RemoteApprovalAsk ask)
    {
        var outcome = new TaskCompletionSource<RemoteApprovalResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        kit.Executor.Behavior = async (execution, token) =>
        {
            var result = await execution.RequestApprovalAsync(ask, token);
            outcome.TrySetResult(result);
            return result.IsAllowed ? RemoteOperationResult.Success("Apagado.") : result.AsFailure("Apagar");
        };
        return outcome;
    }

    private static async Task<RemoteOperationRequest> QueuedAsync(RemoteDispatchKit kit)
    {
        var request = kit.Request();
        kit.Queue(request);
        await kit.Dispatcher.PollAsync(Ct);
        return request;
    }

    /// <summary>O relógio de mentira anda até a operação terminar: a espera da aprovação é nele.</summary>
    private static async Task<RemoteOperationStatus> AdvanceUntilFinishedAsync(RemoteDispatchKit kit, Guid operationId, TimeSpan step)
    {
        var finished = kit.WaitFinishedAsync(operationId);

        while (!finished.IsCompleted)
        {
            kit.Time.Advance(step);
            await Task.WhenAny(finished, Task.Delay(20, Ct));
        }

        return await finished;
    }

    private static IReadOnlyList<TrustedController> Trusting(Controller controller, DateTimeOffset at) =>
        [TrustedController.Approve(controller.DeviceId, controller.Nickname, controller.Key.Fingerprint, controller.Key.SubjectPublicKeyInfo, Guid.CreateVersion7(), at)];

    private static RemoteApprovalResult Verify(ApprovalRequest asked, SignedEnvelope answer, ApprovalState state, IReadOnlyList<TrustedController> trusted) =>
        RemoteApprovalVerifier.Verify(asked, new ApprovalView(asked, Guid.Empty, state, answer, null, null), trusted)!;
}
