using System.Text.Json;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>A validação local da autorização (seção 4.4), item a item, e a primeira que falha é o motivo.</summary>
public sealed class AdmitRemoteOperationTests
{
    [Fact]
    public async Task AcceptsASignedRequestFromATrustedControllerAndJournalsIt()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var request = kit.Request();

        var admission = await kit.AdmitAsync(kit.Phone.Sign(request));

        var admitted = Assert.IsType<RemoteAdmission.Admitted>(admission);
        Assert.Equal(request.OperationId, admitted.Request.OperationId);
        var entry = Assert.Single(kit.Journal.All);
        Assert.Equal(RemoteOperationStatus.Accepted, entry.Status);
        Assert.Null(entry.ReportedStatus);
        Assert.Equal("Pixel 8", entry.ControllerNickname);
    }

    [Fact]
    public async Task RefusesEverythingWhenThisMachineDoesNotAcceptControl()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        kit.Settings.Settings = new RemoteSettings(true, false);

        var admission = await kit.AdmitAsync(kit.Phone.Sign(kit.Request()));

        Assert.Equal(RemoteErrorCodes.RemoteDisabled, Assert.IsType<RemoteAdmission.Refused>(admission).Code);
        Assert.Empty(kit.Journal.All);
    }

    [Fact]
    public async Task RefusesAKeyThisMachineNeverPaired()
    {
        await using var kit = new RemoteDispatchKit();

        var admission = await kit.AdmitAsync(kit.Phone.Sign(kit.Request()));

        var refused = Assert.IsType<RemoteAdmission.Refused>(admission);
        Assert.Equal(RemoteErrorCodes.UntrustedSignature, refused.Code);

        // Sem assinatura que confira, nada vai para o diário: senão o servidor bloquearia ids legítimos.
        Assert.Null(refused.Entry);
        Assert.Empty(kit.Journal.All);
    }

    [Fact]
    public async Task RefusesARevokedController()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone).Revoke(kit.Time.GetUtcNow());

        var admission = await kit.AdmitAsync(kit.Phone.Sign(kit.Request()));

        Assert.Equal(RemoteErrorCodes.UntrustedSignature, Assert.IsType<RemoteAdmission.Refused>(admission).Code);
    }

    [Fact]
    public async Task RefusesASignatureThatDoesNotMatchTheBytes()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var genuine = kit.Phone.Sign(kit.Request()).Jws.Split('.');
        var forged = kit.Phone.Sign(kit.Request(request => request with { Kind = RemoteOperationKinds.WorktreeRemove })).Jws.Split('.');

        // O payload de um pedido com a assinatura de outro: um servidor que troca o pedido no caminho.
        var admission = await kit.AdmitAsync(new SignedEnvelope($"{genuine[0]}.{forged[1]}.{genuine[2]}"));

        Assert.Equal(RemoteErrorCodes.UntrustedSignature, Assert.IsType<RemoteAdmission.Refused>(admission).Code);
    }

    [Fact]
    public async Task RefusesARequestClaimingAnotherControllerThanTheKey()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);

        var admission = await kit.AdmitAsync(kit.Phone.Sign(kit.Request(request => request with { RequestedByDeviceId = Guid.CreateVersion7() })));

        Assert.Equal(RemoteErrorCodes.UntrustedSignature, Assert.IsType<RemoteAdmission.Refused>(admission).Code);
    }

    [Fact]
    public async Task RefusesWithTheEntryWhenTheSignatureHoldsButTheTargetIsAnotherMachine()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);

        var admission = await kit.AdmitAsync(kit.Phone.Sign(kit.Request(request => request with { TargetDeviceId = Guid.CreateVersion7() })));

        var refused = Assert.IsType<RemoteAdmission.Refused>(admission);
        Assert.Equal(RemoteErrorCodes.WrongTarget, refused.Code);
        Assert.Equal(RemoteOperationStatus.Rejected, Assert.Single(kit.Journal.All).Status);
    }

    [Fact]
    public async Task RefusesAnotherAccount()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        kit.LinkedWorkspace = Guid.CreateVersion7();

        var admission = await kit.AdmitAsync(kit.Phone.Sign(kit.Request()));

        Assert.Equal(RemoteErrorCodes.WrongWorkspace, Assert.IsType<RemoteAdmission.Refused>(admission).Code);
    }

    [Fact]
    public async Task RefusesAnExpiredRequest()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var envelope = kit.Phone.Sign(kit.Request());
        kit.Time.Advance(RemoteLimits.DefaultAcceptanceWindow + TimeSpan.FromSeconds(1));

        var admission = await kit.AdmitAsync(envelope);

        Assert.Equal(RemoteErrorCodes.AlreadyExpired, Assert.IsType<RemoteAdmission.Refused>(admission).Code);
    }

    /// <summary>O relógio de quem pediu adiantado: o pedido parece criado no futuro aqui.</summary>
    [Fact]
    public async Task RefusesARequestCreatedAheadOfThisClock()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var ahead = kit.Time.GetUtcNow() + RemoteLimits.MaxClockSkew + TimeSpan.FromMinutes(1);
        var envelope = kit.Phone.Sign(kit.Request() with { CreatedAt = ahead, NotAfter = ahead + RemoteLimits.DefaultAcceptanceWindow });

        var admission = await kit.AdmitAsync(envelope);

        Assert.Equal(RemoteErrorCodes.ClockSkew, Assert.IsType<RemoteAdmission.Refused>(admission).Code);
    }

    [Fact]
    public async Task ASecondDeliveryOfTheSameRequestIsWhatTheJournalSays()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var envelope = kit.Phone.Sign(kit.Request());
        await kit.AdmitAsync(envelope);

        var again = await kit.AdmitAsync(envelope);

        Assert.Equal(RemoteOperationStatus.Accepted, Assert.IsType<RemoteAdmission.Known>(again).Entry.Status);
        Assert.Single(kit.Journal.All);
    }

    [Fact]
    public async Task RefusesAKindThisAgentDoesNotRun()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        kit.Executor.Kind = RemoteOperationKinds.CommandRun;

        var admission = await kit.AdmitAsync(kit.Phone.Sign(kit.Request()));

        Assert.Equal(RemoteErrorCodes.UnknownKind, Assert.IsType<RemoteAdmission.Refused>(admission).Code);
    }

    [Fact]
    public async Task WaitsForTheSyncWhileTheTaskHasNotArrivedAndRefusesNearTheDeadline()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var taskId = Guid.CreateVersion7();
        var envelope = kit.Phone.Sign(kit.Request(request => request with { TaskId = taskId }));

        var early = await kit.AdmitAsync(envelope);

        Assert.Equal([taskId], Assert.IsType<RemoteAdmission.Deferred>(early).Missing);
        Assert.Empty(kit.Journal.All);

        kit.Time.Advance(RemoteLimits.DefaultAcceptanceWindow - AdmitRemoteOperationHandler.EntitiesGrace);
        var late = await kit.AdmitAsync(envelope);

        Assert.Equal(RemoteErrorCodes.EntitiesMissing, Assert.IsType<RemoteAdmission.Refused>(late).Code);
    }

    [Fact]
    public async Task AcceptsOnceTheTaskArrives()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        var taskId = Guid.CreateVersion7();
        kit.Entities.Known.Add(taskId);

        var admission = await kit.AdmitAsync(kit.Phone.Sign(kit.Request(request => request with { TaskId = taskId })));

        Assert.IsType<RemoteAdmission.Admitted>(admission);
    }

    [Fact]
    public async Task RefusesWhatTheCatalogOfThisMachineDoesNotAllow()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Trust(kit.Phone);
        kit.Executor.Refusal = RemoteErrorCodes.NotInCatalog;

        var admission = await kit.AdmitAsync(kit.Phone.Sign(kit.Request()));

        Assert.Equal(RemoteErrorCodes.NotInCatalog, Assert.IsType<RemoteAdmission.Refused>(admission).Code);
    }

    [Fact]
    public async Task RefusesAboveTheConcurrentLimit()
    {
        await using var kit = new RemoteDispatchKit(new RemoteDispatchLimits(MaxConcurrent: 2, MaxPerControllerPerHour: 100));
        kit.Trust(kit.Phone);

        var admission = await kit.AdmitAsync(kit.Phone.Sign(kit.Request()), running: 2);

        Assert.Equal(RemoteErrorCodes.TooManyOperations, Assert.IsType<RemoteAdmission.Refused>(admission).Code);
    }

    [Fact]
    public async Task RefusesAboveTheHourlyLimitOfOneController()
    {
        await using var kit = new RemoteDispatchKit(new RemoteDispatchLimits(MaxConcurrent: 10, MaxPerControllerPerHour: 2));
        kit.Trust(kit.Phone);
        using var laptop = new Controller("Notebook");
        kit.Trust(laptop);

        Assert.IsType<RemoteAdmission.Admitted>(await kit.AdmitAsync(kit.Phone.Sign(kit.Request())));
        Assert.IsType<RemoteAdmission.Admitted>(await kit.AdmitAsync(kit.Phone.Sign(kit.Request())));
        var third = await kit.AdmitAsync(kit.Phone.Sign(kit.Request()));

        Assert.Equal(RemoteErrorCodes.TooManyOperations, Assert.IsType<RemoteAdmission.Refused>(third).Code);

        // O teto é de cada controlador: o outro ainda pode.
        Assert.IsType<RemoteAdmission.Admitted>(await kit.AdmitAsync(laptop.Sign(kit.Request(by: laptop))));

        kit.Time.Advance(TimeSpan.FromHours(1) + TimeSpan.FromSeconds(1));
        Assert.IsType<RemoteAdmission.Admitted>(await kit.AdmitAsync(kit.Phone.Sign(kit.Request())));
    }

    /// <summary>
    /// A sessão gerenciada (ADR-092): o pedido que o desktop desta máquina
    /// assina com a chave do cofre é admitido sem pareamento, e roda com a
    /// origem "este computador".
    /// </summary>
    [Fact]
    public async Task ThisMachineItself_IsTrusted_ByItsOwnKeyAndId()
    {
        await using var kit = new RemoteDispatchKit();

        var sent = await new SendRemoteOperationHandler(kit.Server, kit.Signer, kit.Identity, kit.Sync, kit.Time).HandleAsync(
            new SendRemoteOperation(
                kit.Identity.DeviceId,
                RemoteOperationKinds.EnvironmentInspect,
                JsonSerializer.SerializeToElement(new EnvironmentInspectionRequest(), RemoteJson.Options)),
            Ct);
        await kit.Dispatcher.PollAsync(Ct);

        Assert.Equal(RemoteOperationStatus.Succeeded, await kit.WaitFinishedAsync(sent.OperationId));
        Assert.Equal("Remoto: este computador", kit.Executor.SeenOrigin);
        Assert.Empty(kit.Trusted.All);
    }

    /// <summary>A exceção é estreita: o id desta máquina com outra chave, ou a chave dela dizendo ser outro, não passam.</summary>
    [Fact]
    public async Task ThisMachineId_WithAnotherKey_OrItsKey_ClaimingAnotherId_IsRefused()
    {
        await using var kit = new RemoteDispatchKit();
        using var impostor = new Controller("Impostor");

        var forged = await kit.AdmitAsync(impostor.Sign(kit.Request(request => request with { RequestedByDeviceId = kit.Identity.DeviceId })));

        Assert.Equal(RemoteErrorCodes.UntrustedSignature, Assert.IsType<RemoteAdmission.Refused>(forged).Code);

        var own = await new SendRemoteOperationHandler(kit.Server, kit.Signer, new FakeDeviceIdentity(), kit.Sync, kit.Time).HandleAsync(
            new SendRemoteOperation(
                kit.Identity.DeviceId,
                RemoteOperationKinds.EnvironmentInspect,
                JsonSerializer.SerializeToElement(new EnvironmentInspectionRequest(), RemoteJson.Options)),
            Ct);
        var claiming = await kit.AdmitAsync(kit.Server.Operations[own.OperationId].Envelope);

        Assert.Equal(RemoteErrorCodes.UntrustedSignature, Assert.IsType<RemoteAdmission.Refused>(claiming).Code);
    }

    [Fact]
    public async Task ThisMachineItself_IsRefused_WhenItDoesNotAcceptControl()
    {
        await using var kit = new RemoteDispatchKit();
        kit.Settings.Settings = new RemoteSettings(true, false);

        var sent = await new SendRemoteOperationHandler(kit.Server, kit.Signer, kit.Identity, kit.Sync, kit.Time).HandleAsync(
            new SendRemoteOperation(
                kit.Identity.DeviceId,
                RemoteOperationKinds.EnvironmentInspect,
                JsonSerializer.SerializeToElement(new EnvironmentInspectionRequest(), RemoteJson.Options)),
            Ct);

        Assert.Equal(RemoteErrorCodes.RemoteDisabled, Assert.IsType<RemoteAdmission.Refused>(await kit.AdmitAsync(kit.Server.Operations[sent.OperationId].Envelope)).Code);
    }

    /// <summary>Quem está na frente da máquina responde as perguntas do Claude gerenciado pelo desktop dela.</summary>
    [Fact]
    public async Task TheAnswerOfThisMachineItself_IsOneTheAgentAccepts()
    {
        await using var kit = new RemoteDispatchKit();
        var now = kit.Time.GetUtcNow();
        var operationId = Guid.CreateVersion7();
        kit.Server.Enqueue(operationId, new SignedEnvelope("x.y.z"));
        var asked = (await kit.Server.RequestApprovalAsync(ApprovalHash.Seal(new ApprovalRequest(
            Guid.CreateVersion7(), operationId, ApprovalKind.ToolUse, "O Claude quer usar Bash: dotnet test",
            JsonSerializer.SerializeToElement(new { }), null, now.AddMinutes(10), string.Empty)), Ct)).Request;

        var answered = await new AnswerApprovalHandler(kit.Server, kit.Signer, kit.Time).HandleAsync(new AnswerApproval(asked.ApprovalId, ApprovalDecision.Allow), Ct);
        var trusted = await new RemoteTrust(kit.Trusted, kit.Identity, kit.Signer).ListAsync(Ct);

        Assert.Equal(RemoteApprovalOutcome.Allowed, RemoteApprovalVerifier.Verify(asked, answered, trusted)!.Outcome);
        Assert.Equal(RemoteApprovalOutcome.Invalid, RemoteApprovalVerifier.Verify(asked, answered, kit.Trusted.All)!.Outcome);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;
}
