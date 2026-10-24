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
internal sealed class RemoteApiClient(SyncHttp http, ISyncAuthenticator authenticator) : IRemoteApi
{
    public Task<PairingInfo> GetPairingAsync(Guid pairingId, CancellationToken cancellationToken) =>
        CallAsync<PairingInfo>(HttpMethod.Get, RemoteRoutes.Pairing(pairingId), null, cancellationToken);

    public async Task<IReadOnlyList<PairingInfo>> ListOpenPairingsAsync(CancellationToken cancellationToken) =>
        await CallAsync<List<PairingInfo>>(HttpMethod.Get, RemoteRoutes.Pairings, null, cancellationToken);

    public Task<PairingInfo> ApprovePairingAsync(Guid pairingId, PairingApproval approval, CancellationToken cancellationToken) =>
        CallAsync<PairingInfo>(HttpMethod.Post, RemoteRoutes.ApprovePairing(pairingId), approval, cancellationToken);

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
