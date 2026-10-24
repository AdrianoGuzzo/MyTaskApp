using System.Security.Claims;
using System.Text.Json;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Application;

namespace MyTaskApp.Server.Api;

/// <summary>
/// A orquestração remota (fase 2, ADR-084): o Bearer com <c>sync</c> e
/// <c>remote</c>, o dispositivo não revogado e uma cota própria, separada da do
/// sync. O JSON é o do <see cref="RemoteJson"/>, mais estrito que o do resto da
/// API: campo desconhecido ou obrigatório ausente é recusado.
/// </summary>
internal static class RemoteApiEndpoints
{
    public static void MapRemoteApi(this IEndpointRouteBuilder app)
    {
        var remote = app.MapGroup(RemoteRoutes.Prefix)
            .RequireAuthorization(ServerSetup.RemotePolicy)
            .AddEndpointFilter<DeviceFilter>()
            .RequireRateLimiting(ServerSetup.RemoteLimiter);

        remote.MapPut("/devices/current", async (HttpRequest request, ClaimsPrincipal user, RemoteDeviceUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.RegisterAsync(SyncApiEndpoints.Caller(user), await ReadAsync<DeviceRegistration>(request, cancellationToken), cancellationToken)));

        remote.MapGet("/devices", async (ClaimsPrincipal user, RemoteDeviceUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.ListAsync(SyncApiEndpoints.Caller(user), cancellationToken)));
    }

    private static async Task<T> ReadAsync<T>(HttpRequest request, CancellationToken cancellationToken)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(request.Body, RemoteJson.Options, cancellationToken)
                ?? throw SyncServerException.BadRequest("Pedido vazio.");
        }
        catch (JsonException)
        {
            throw SyncServerException.BadRequest("O pedido não segue o contrato da orquestração remota.");
        }
    }

    private static IResult Json<T>(T value) => TypedResults.Json(value, RemoteJson.Options);
}
