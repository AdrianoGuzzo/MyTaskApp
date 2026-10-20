using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Sync;

namespace MyTaskApp.Infrastructure.Sync.Http;

/// <summary>
/// O endereço em tempo de execução (ADR-078), sobre o mesmo
/// <see cref="SyncOptions"/> que o <see cref="SyncHttp"/> lê a cada pedido:
/// trocar aqui vale no pedido seguinte, sem recriar o <c>HttpClient</c>.
/// </summary>
internal sealed class SyncServerAddress(
    SyncOptions options,
    SyncAuthenticationService authenticator,
    ILogger<SyncServerAddress> logger) : ISyncServerAddress
{
    public Uri? Current => options.BaseUri;

    public bool AllowHttpLoopback => options.AllowHttpLoopback;

    public event Action? Changed;

    public void Set(Uri? server)
    {
        if (server is not null && !SyncServerUrl.Check(server.AbsoluteUri, options.AllowHttpLoopback).IsValid)
        {
            throw new ArgumentException("Endereço de servidor inválido.", nameof(server));
        }

        var changed = !SyncServerUrl.SameServer(options.BaseUri, server) && (options.BaseUri is not null || server is not null);
        options.ApiBaseUrl = server?.AbsoluteUri;

        if (!changed)
        {
            return;
        }

        // O access token em memória é do servidor anterior.
        authenticator.ForgetAccessToken();
        logger.LogInformation("SyncServerAddressChanged {Configured}", server is not null);
        Changed?.Invoke();
    }
}
