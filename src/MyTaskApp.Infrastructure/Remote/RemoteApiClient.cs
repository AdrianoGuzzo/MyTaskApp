using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Infrastructure.Sync.Http;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Infrastructure.Remote;

/// <summary>
/// As rotas da orquestração (ADR-084) pelo mesmo HTTP e pelo mesmo login do
/// sync: o token que vale é o do dispositivo, com o escopo <c>remote</c>. O JSON
/// é o do <see cref="RemoteJson"/>, o estrito do contrato remoto.
/// </summary>
internal sealed class RemoteApiClient(SyncHttp http, ISyncAuthenticator authenticator) : IRemoteApi, IRemoteOperationsApi, IRemoteSessionsApi
{
    public Task<RemoteDeviceInfo> RegisterAsync(DeviceRegistration registration, CancellationToken cancellationToken) =>
        CallAsync<RemoteDeviceInfo>(HttpMethod.Put, RemoteRoutes.CurrentDevice, registration, cancellationToken);

    public async Task<IReadOnlyList<RemoteDeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken) =>
        await CallAsync<List<RemoteDeviceInfo>>(HttpMethod.Get, RemoteRoutes.Devices, null, cancellationToken);

    public Task<PairingInfo> RequestPairingAsync(Guid executorDeviceId, CancellationToken cancellationToken) =>
        CallAsync<PairingInfo>(HttpMethod.Post, RemoteRoutes.Pairings, new PairingRequest(executorDeviceId), cancellationToken);

    public Task<PairingInfo> GetPairingAsync(Guid pairingId, CancellationToken cancellationToken) =>
        CallAsync<PairingInfo>(HttpMethod.Get, RemoteRoutes.Pairing(pairingId), null, cancellationToken);

    public async Task<IReadOnlyList<PairingInfo>> ListOpenPairingsAsync(CancellationToken cancellationToken) =>
        await CallAsync<List<PairingInfo>>(HttpMethod.Get, RemoteRoutes.Pairings, null, cancellationToken);

    public Task<PairingInfo> ApprovePairingAsync(Guid pairingId, PairingApproval approval, CancellationToken cancellationToken) =>
        CallAsync<PairingInfo>(HttpMethod.Post, RemoteRoutes.ApprovePairing(pairingId), approval, cancellationToken);

    public async Task<IReadOnlyList<AgentOperation>> ListAgentOperationsAsync(CancellationToken cancellationToken) =>
        await CallAsync<List<AgentOperation>>(HttpMethod.Get, RemoteRoutes.AgentOperations, null, cancellationToken);

    public Task<RemoteOperationView> UpdateStatusAsync(Guid operationId, OperationStatusUpdate update, CancellationToken cancellationToken) =>
        CallAsync<RemoteOperationView>(HttpMethod.Post, RemoteRoutes.OperationStatus(operationId), update, cancellationToken);

    public Task<EventBatchAck> AppendEventsAsync(EventBatch batch, CancellationToken cancellationToken) =>
        CallAsync<EventBatchAck>(HttpMethod.Post, RemoteRoutes.OperationEvents(batch.OperationId), batch, cancellationToken);

    public Task<RemoteOperationView> CreateAsync(SignedEnvelope request, CancellationToken cancellationToken) =>
        CallAsync<RemoteOperationView>(HttpMethod.Post, RemoteRoutes.Operations, request, cancellationToken);

    public Task<RemoteOperationView> GetAsync(Guid operationId, CancellationToken cancellationToken) =>
        CallAsync<RemoteOperationView>(HttpMethod.Get, RemoteRoutes.Operation(operationId), null, cancellationToken);

    public Task<RemoteEventPage> ListEventsAsync(Guid operationId, long after, int? limit, CancellationToken cancellationToken) =>
        CallAsync<RemoteEventPage>(
            HttpMethod.Get,
            $"{RemoteRoutes.OperationEvents(operationId)}?after={after}" + (limit is { } size ? $"&limit={size}" : string.Empty),
            null,
            cancellationToken);

    public Task<RemoteOperationView> CancelAsync(Guid operationId, CancellationToken cancellationToken) =>
        CallAsync<RemoteOperationView>(HttpMethod.Post, RemoteRoutes.CancelOperation(operationId), null, cancellationToken);

    public Task<AgentCatalog> PublishCatalogAsync(AgentCatalog catalog, CancellationToken cancellationToken) =>
        CallAsync<AgentCatalog>(HttpMethod.Put, RemoteRoutes.CurrentCatalog, catalog, cancellationToken);

    public Task<AgentCatalog> GetCatalogAsync(Guid deviceId, CancellationToken cancellationToken) =>
        CallAsync<AgentCatalog>(HttpMethod.Get, RemoteRoutes.Catalog(deviceId), null, cancellationToken);

    public Task<ApprovalView> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken) =>
        CallAsync<ApprovalView>(HttpMethod.Post, RemoteRoutes.OperationApprovals(request.OperationId), request, cancellationToken);

    public Task<ApprovalView> GetApprovalAsync(Guid approvalId, CancellationToken cancellationToken) =>
        CallAsync<ApprovalView>(HttpMethod.Get, RemoteRoutes.Approval(approvalId), null, cancellationToken);

    public async Task<IReadOnlyList<ApprovalView>> ListPendingApprovalsAsync(CancellationToken cancellationToken) =>
        await CallAsync<List<ApprovalView>>(HttpMethod.Get, RemoteRoutes.Approvals, null, cancellationToken);

    public Task<ApprovalView> RespondApprovalAsync(Guid approvalId, SignedEnvelope response, CancellationToken cancellationToken) =>
        CallAsync<ApprovalView>(HttpMethod.Post, RemoteRoutes.ApprovalResponse(approvalId), response, cancellationToken);

    public Task<RemoteAgentSessionAck> ReportSessionsAsync(RemoteAgentSessionBatch batch, CancellationToken cancellationToken) =>
        CallAsync<RemoteAgentSessionAck>(HttpMethod.Put, RemoteRoutes.AgentSessions, batch, cancellationToken);

    public Task<RemoteAgentSessionList> ListSessionsAsync(Guid? deviceId, bool openOnly, int? limit, CancellationToken cancellationToken) =>
        CallAsync<RemoteAgentSessionList>(HttpMethod.Get, RemoteRoutes.SessionsQuery(deviceId, openOnly, limit), null, cancellationToken);

    public Task<RemoteAgentSessionDetail> GetSessionAsync(Guid sessionId, CancellationToken cancellationToken) =>
        CallAsync<RemoteAgentSessionDetail>(HttpMethod.Get, RemoteRoutes.Session(sessionId), null, cancellationToken);

    private async Task<T> CallAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var token = await authenticator.GetAccessTokenAsync(cancellationToken);

        try
        {
            return await http.SendAsync<T>(method, path, body, token, cancellationToken, RemoteJson.Options);
        }
        catch (SyncApiException exception) when (exception.Kind == SyncFailureKind.Unauthorized)
        {
            authenticator.ForgetAccessToken();
            token = await authenticator.GetAccessTokenAsync(cancellationToken);
            return await http.SendAsync<T>(method, path, body, token, cancellationToken, RemoteJson.Options);
        }
    }
}
