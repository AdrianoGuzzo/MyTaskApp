using MyTaskApp.Application.Sync;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Infrastructure.Sync.Http;

/// <summary>
/// O cliente tipado da API v1 (ADR-060). Toda chamada leva o access token; um
/// 401 esquece o token, renova e tenta uma vez — o access pode ter vencido
/// entre a conferência e a chegada.
/// </summary>
internal sealed class SyncApiClient(SyncHttp http, ISyncAuthenticator authenticator) : ISyncApi
{
    public Task<MeResponse> MeAsync(CancellationToken cancellationToken) =>
        CallAsync<MeResponse>(HttpMethod.Get, ApiRoutes.Me, null, cancellationToken);

    public async Task<IReadOnlyList<WorkspaceInfo>> WorkspacesAsync(CancellationToken cancellationToken) =>
        await CallAsync<List<WorkspaceInfo>>(HttpMethod.Get, ApiRoutes.Workspaces, null, cancellationToken);

    public Task<DeviceInfo> HelloAsync(DeviceHello hello, CancellationToken cancellationToken) =>
        CallAsync<DeviceInfo>(HttpMethod.Put, ApiRoutes.CurrentDevice, hello, cancellationToken);

    public async Task<IReadOnlyList<DeviceInfo>> DevicesAsync(CancellationToken cancellationToken) =>
        await CallAsync<List<DeviceInfo>>(HttpMethod.Get, ApiRoutes.Devices, null, cancellationToken);

    public Task<DeviceInfo> RenameDeviceAsync(Guid deviceId, string nickname, CancellationToken cancellationToken) =>
        CallAsync<DeviceInfo>(HttpMethod.Patch, ApiRoutes.Device(deviceId), new RenameDeviceRequest(nickname), cancellationToken);

    public Task<DeviceInfo> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken) =>
        CallAsync<DeviceInfo>(HttpMethod.Post, ApiRoutes.RevokeDevice(deviceId), null, cancellationToken);

    public Task<PushResponse> PushAsync(Guid workspaceId, PushRequest request, CancellationToken cancellationToken) =>
        CallAsync<PushResponse>(HttpMethod.Post, ApiRoutes.Push(workspaceId), request, cancellationToken);

    public Task<PullResponse> PullAsync(Guid workspaceId, string cursor, int limit, CancellationToken cancellationToken) =>
        CallAsync<PullResponse>(
            HttpMethod.Get,
            $"{ApiRoutes.Pull(workspaceId)}?cursor={Uri.EscapeDataString(cursor)}&limit={limit}",
            null,
            cancellationToken);

    public Task<FetchResponse> FetchAsync(Guid workspaceId, FetchRequest request, CancellationToken cancellationToken) =>
        CallAsync<FetchResponse>(HttpMethod.Post, ApiRoutes.Fetch(workspaceId), request, cancellationToken);

    private async Task<T> CallAsync<T>(HttpMethod method, string path, object? body, CancellationToken cancellationToken)
    {
        var token = await authenticator.GetAccessTokenAsync(cancellationToken);

        try
        {
            return await http.SendAsync<T>(method, path, body, token, cancellationToken);
        }
        catch (SyncApiException exception) when (exception.Kind == SyncFailureKind.Unauthorized)
        {
            authenticator.ForgetAccessToken();
            token = await authenticator.GetAccessTokenAsync(cancellationToken);
            return await http.SendAsync<T>(method, path, body, token, cancellationToken);
        }
    }
}
