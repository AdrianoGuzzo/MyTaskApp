using System.Text.Json;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Mobile.Core.Tests.Fakes;

/// <summary>
/// O servidor da orquestração em memória, do lado do controlador: guarda o
/// registro do celular e, nos pedidos e nas respostas, confere a assinatura
/// com a chave registrada, como o de verdade. As conversas do Claude
/// (ADR-091) são as que o teste põe, e o prompt e o parar entram nelas pelo
/// payload, como no servidor.
/// </summary>
internal sealed class FakeRemoteServer(TimeProvider time) : IRemoteApi, IRemoteOperationsApi, IRemoteSessionsApi
{
    public const string Nonce = "bm9uY2U";

    public List<DeviceRegistration> Registrations { get; } = [];

    /// <summary>As outras máquinas da conta.</summary>
    public List<RemoteDeviceInfo> Devices { get; } = [];

    public Dictionary<Guid, PairingInfo> Pairings { get; } = [];

    public Dictionary<Guid, AgentCatalog> Catalogs { get; } = [];

    /// <summary>Os pedidos que chegaram, já conferidos.</summary>
    public List<RemoteOperationRequest> Requests { get; } = [];

    public Dictionary<Guid, RemoteOperationView> Operations { get; } = [];

    public Dictionary<Guid, List<RemoteOperationEvent>> Events { get; } = [];

    /// <summary>Eventos por página, como o servidor de verdade limita.</summary>
    public int EventPageSize { get; set; } = RemoteLimits.MaxEventPageSize;

    public Dictionary<Guid, ApprovalView> Approvals { get; } = [];

    /// <summary>As respostas que chegaram, já conferidas.</summary>
    public List<ApprovalResponse> Responses { get; } = [];

    public List<RemoteAgentSessionView> Sessions { get; } = [];

    /// <summary>O último relato do agente de cada máquina.</summary>
    public Dictionary<Guid, DateTimeOffset?> AgentSeenAt { get; } = [];

    /// <summary>As operações de cada conversa, na ordem em que chegaram.</summary>
    public Dictionary<Guid, List<Guid>> SessionOperations { get; } = [];

    /// <summary>Um servidor de antes das conversas: as rotas não existem.</summary>
    public bool WithoutSessions { get; set; }

    private DevicePublicKey Key => Registrations.LastOrDefault()?.Key
        ?? throw new SyncApiException(SyncFailureKind.Unauthorized, "O dispositivo não se registrou.", "device_unknown");

    public static RemoteDeviceInfo Executor(Guid deviceId, string nickname, DateTimeOffset? heartbeat) =>
        new(
            deviceId,
            nickname,
            "windows",
            "1.0.0",
            false,
            DeviceRoles.Controller | DeviceRoles.Executor,
            "fp-" + nickname,
            new DeviceCapabilities(RemoteLimits.ProtocolVersion, [], "windows", new ToolStatus(true, "2.47"), ToolStatus.Missing, ToolStatus.Missing, ToolStatus.Missing, 2, "hash"),
            heartbeat);

    public Task<RemoteDeviceInfo> RegisterAsync(DeviceRegistration registration, CancellationToken cancellationToken)
    {
        Registrations.Add(registration);
        return Task.FromResult(This(registration));
    }

    public Task<IReadOnlyList<RemoteDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<RemoteDeviceInfo>>([This(Registrations[^1]), .. Devices]);

    public Task<PairingInfo> RequestPairingAsync(Guid executorDeviceId, CancellationToken cancellationToken)
    {
        var executor = Devices.Single(device => device.DeviceId == executorDeviceId);
        var pairing = new PairingInfo(
            Guid.CreateVersion7(), Registrations[^1].DeviceId, "Pixel de teste", Key, executorDeviceId,
            executor.KeyFingerprint!, Nonce, PairingStatus.Pending, time.GetUtcNow() + RemoteLimits.PairingLifetime);
        Pairings[pairing.PairingId] = pairing;
        return Task.FromResult(pairing);
    }

    public Task<PairingInfo> GetPairingAsync(Guid pairingId, CancellationToken cancellationToken) =>
        Task.FromResult(Pairings[pairingId]);

    public Task<IReadOnlyList<PairingInfo>> ListOpenPairingsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<PairingInfo>>([]);

    public Task<PairingInfo> ApprovePairingAsync(Guid pairingId, PairingApproval approval, CancellationToken cancellationToken) =>
        throw new NotSupportedException("O celular não executa: não aprova pareamento.");

    public Task<RemoteOperationView> CreateAsync(SignedEnvelope request, CancellationToken cancellationToken)
    {
        if (!RemoteJws.TryVerify<RemoteOperationRequest>(request, RemoteJws.OperationType, Key, out var payload))
        {
            throw new SyncApiException(SyncFailureKind.Rejected, "A assinatura não confere.", "signature_invalid");
        }

        Requests.Add(payload);
        var view = new RemoteOperationView(
            payload.OperationId, payload.WorkspaceId, payload.TargetDeviceId, payload.RequestedByDeviceId, payload.Kind, payload.TaskId,
            RemoteOperationStatus.Queued, payload.CreatedAt, payload.NotAfter, payload.CreatedAt, null, null, null, 0);
        Operations[view.OperationId] = view;
        Events[view.OperationId] = [];

        if (SessionOf(payload) is { } sessionId)
        {
            Link(sessionId, view.OperationId);
        }

        return Task.FromResult(view);
    }

    public void Link(Guid sessionId, Guid operationId)
    {
        if (!SessionOperations.TryGetValue(sessionId, out var operations))
        {
            SessionOperations[sessionId] = operations = [];
        }

        operations.Add(operationId);
    }

    public Task<RemoteAgentSessionAck> ReportSessionsAsync(RemoteAgentSessionBatch batch, CancellationToken cancellationToken) =>
        throw new NotSupportedException("O celular não relata conversas.");

    public Task<RemoteAgentSessionList> ListSessionsAsync(Guid? deviceId, bool openOnly, int? limit, CancellationToken cancellationToken)
    {
        if (WithoutSessions)
        {
            throw new SyncApiException(SyncFailureKind.NotFound, "Não encontrado no servidor.");
        }

        var agents = Devices.Where(device => device.Roles.HasFlag(DeviceRoles.Executor))
            .Select(device => new RemoteAgentPresence(device.DeviceId, AgentSeenAt.GetValueOrDefault(device.DeviceId)));

        return Task.FromResult(new RemoteAgentSessionList(
            [.. agents],
            [.. Sessions.Where(session => deviceId is null || session.DeviceId == deviceId).OrderByDescending(session => session.UpdatedAt)]));
    }

    public Task<RemoteAgentSessionDetail> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = Sessions.SingleOrDefault(item => item.SessionId == sessionId)
            ?? throw new SyncApiException(SyncFailureKind.NotFound, "Não existe.", "session_not_found");

        return Task.FromResult(new RemoteAgentSessionDetail(
            session,
            new RemoteAgentPresence(session.DeviceId, AgentSeenAt.GetValueOrDefault(session.DeviceId)),
            [.. SessionOperations.GetValueOrDefault(sessionId, []).Select(id => Operations[id])]));
    }

    private static Guid? SessionOf(RemoteOperationRequest request) => request.Kind switch
    {
        RemoteOperationKinds.AgentPrompt => request.Payload.Deserialize<PromptRequest>(RemoteJson.Options)?.AgentSessionId,
        RemoteOperationKinds.AgentStop => request.Payload.Deserialize<AgentStopRequest>(RemoteJson.Options)?.AgentSessionId,
        _ => null,
    };

    public Task<RemoteOperationView> GetAsync(Guid operationId, CancellationToken cancellationToken) =>
        Operations.TryGetValue(operationId, out var view)
            ? Task.FromResult(view)
            : throw new SyncApiException(SyncFailureKind.NotFound, "Não existe.", "operation_not_found");

    public Task<RemoteEventPage> ListEventsAsync(Guid operationId, long after, int? limit, CancellationToken cancellationToken)
    {
        var events = Events.GetValueOrDefault(operationId) ?? [];
        var newer = events.Where(item => item.Seq > after).ToList();
        return Task.FromResult(new RemoteEventPage([.. newer.Take(EventPageSize)], events.Count == 0 ? 0 : events[^1].Seq, newer.Count > EventPageSize));
    }

    public Task<RemoteOperationView> CancelAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var view = Operations[operationId] with { CancelRequestedAt = time.GetUtcNow() };
        Operations[operationId] = view;
        return Task.FromResult(view);
    }

    public Task<AgentCatalog> GetCatalogAsync(Guid deviceId, CancellationToken cancellationToken) =>
        Catalogs.TryGetValue(deviceId, out var catalog)
            ? Task.FromResult(catalog)
            : throw new SyncApiException(SyncFailureKind.NotFound, "Sem catálogo.", "catalog_not_found");

    public Task<ApprovalView> GetApprovalAsync(Guid approvalId, CancellationToken cancellationToken) =>
        Task.FromResult(Approvals[approvalId]);

    public Task<IReadOnlyList<ApprovalView>> ListPendingApprovalsAsync(CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<ApprovalView>>([.. Approvals.Values.Where(view => view.State == ApprovalState.Pending)]);

    public Task<ApprovalView> RespondApprovalAsync(Guid approvalId, SignedEnvelope response, CancellationToken cancellationToken)
    {
        if (!RemoteJws.TryVerify<ApprovalResponse>(response, RemoteJws.ApprovalType, Key, out var payload))
        {
            throw new SyncApiException(SyncFailureKind.Rejected, "A assinatura não confere.", "signature_invalid");
        }

        Responses.Add(payload);
        var view = Approvals[approvalId] with
        {
            State = payload.Decision == ApprovalDecision.Allow ? ApprovalState.Allowed : ApprovalState.Denied,
            Response = response,
            RespondedAt = time.GetUtcNow(),
        };
        Approvals[approvalId] = view;
        return Task.FromResult(view);
    }

    /// <summary>A máquina de lá pede uma aprovação, selada como o agente sela.</summary>
    public ApprovalView Ask(
        Guid operationId,
        Guid executor,
        ApprovalKind kind,
        string summary,
        string? typed = null,
        TimeSpan? lifetime = null,
        object? details = null)
    {
        var request = ApprovalHash.Seal(new ApprovalRequest(
            Guid.CreateVersion7(), operationId, kind, summary, JsonSerializer.SerializeToElement(details ?? new { }), typed,
            time.GetUtcNow() + (lifetime ?? RemoteLimits.DefaultApprovalLifetime), string.Empty));
        var view = new ApprovalView(request, executor, ApprovalState.Pending, null, null, null);
        Approvals[request.ApprovalId] = view;
        return view;
    }

    public Task<IReadOnlyList<AgentOperation>> ListAgentOperationsAsync(CancellationToken cancellationToken) =>
        throw new NotSupportedException("O celular não executa.");

    public Task<RemoteOperationView> UpdateStatusAsync(Guid operationId, OperationStatusUpdate update, CancellationToken cancellationToken) =>
        throw new NotSupportedException("O celular não executa.");

    public Task<EventBatchAck> AppendEventsAsync(EventBatch batch, CancellationToken cancellationToken) =>
        throw new NotSupportedException("O celular não executa.");

    public Task<AgentCatalog> PublishCatalogAsync(AgentCatalog catalog, CancellationToken cancellationToken) =>
        throw new NotSupportedException("O celular não executa.");

    public Task<ApprovalView> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("O celular não executa.");

    private static RemoteDeviceInfo This(DeviceRegistration registration) =>
        new(registration.DeviceId, "Pixel de teste", registration.Capabilities.OperatingSystem, "1.0.0", true, registration.Roles,
            registration.Key.Fingerprint, registration.Capabilities, null);
}
