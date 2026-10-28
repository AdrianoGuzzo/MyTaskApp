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

        // O catálogo (ADR-088): o agente publica o desta máquina, e a conta lê o de cada uma.
        remote.MapPut("/devices/current/catalog", async (HttpRequest request, ClaimsPrincipal user, RemoteCatalogUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.PublishAsync(SyncApiEndpoints.Caller(user), await ReadAsync<AgentCatalog>(request, cancellationToken), cancellationToken)));

        remote.MapGet("/devices/{deviceId:guid}/catalog", async (Guid deviceId, ClaimsPrincipal user, RemoteCatalogUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.GetAsync(SyncApiEndpoints.Caller(user), deviceId, cancellationToken)));

        // O pareamento (ADR-087): o controlador pede, o executor vê os pedidos dele e aprova assinando.
        remote.MapPost("/pairings", async (HttpRequest request, ClaimsPrincipal user, RemotePairingUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.CreateAsync(SyncApiEndpoints.Caller(user), await ReadAsync<PairingRequest>(request, cancellationToken), cancellationToken)));

        remote.MapGet("/pairings", async (ClaimsPrincipal user, RemotePairingUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.ListOpenAsync(SyncApiEndpoints.Caller(user), cancellationToken)));

        remote.MapGet("/pairings/{pairingId:guid}", async (Guid pairingId, ClaimsPrincipal user, RemotePairingUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.GetAsync(SyncApiEndpoints.Caller(user), pairingId, cancellationToken)));

        remote.MapPost("/pairings/{pairingId:guid}/approve", async (Guid pairingId, HttpRequest request, ClaimsPrincipal user, RemotePairingUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.ApproveAsync(SyncApiEndpoints.Caller(user), pairingId, await ReadAsync<PairingApproval>(request, cancellationToken), cancellationToken)));

        // A fila de operações (ADR-086): o controlador cria assinando; o agente alvo busca, muda o estado e conta o andamento.
        remote.MapPost("/operations", async (HttpRequest request, ClaimsPrincipal user, RemoteOperationUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.CreateAsync(SyncApiEndpoints.Caller(user), await ReadAsync<SignedEnvelope>(request, cancellationToken), cancellationToken), StatusCodes.Status201Created));

        remote.MapGet("/operations", async (int? limit, ClaimsPrincipal user, RemoteOperationUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.ListAsync(SyncApiEndpoints.Caller(user), limit, cancellationToken)));

        remote.MapGet("/operations/{operationId:guid}", async (Guid operationId, ClaimsPrincipal user, RemoteOperationUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.GetAsync(SyncApiEndpoints.Caller(user), operationId, cancellationToken)));

        remote.MapGet("/agent/operations", async (ClaimsPrincipal user, RemoteOperationUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.AgentOperationsAsync(SyncApiEndpoints.Caller(user), cancellationToken)));

        // As conversas do Claude (ADR-091): o agente relata as dele, e a conta lê a lista e a linha do tempo.
        remote.MapPut("/agent/sessions", async (HttpRequest request, ClaimsPrincipal user, RemoteAgentSessionUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.ReportAsync(SyncApiEndpoints.Caller(user), await ReadAsync<RemoteAgentSessionBatch>(request, cancellationToken), cancellationToken)));

        remote.MapGet("/sessions", async (Guid? deviceId, bool? openOnly, int? limit, ClaimsPrincipal user, RemoteAgentSessionUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.ListAsync(SyncApiEndpoints.Caller(user), deviceId, openOnly ?? false, limit, cancellationToken)));

        remote.MapGet("/sessions/{sessionId:guid}", async (Guid sessionId, ClaimsPrincipal user, RemoteAgentSessionUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.GetAsync(SyncApiEndpoints.Caller(user), sessionId, cancellationToken)));

        remote.MapPost("/operations/{operationId:guid}/status", async (Guid operationId, HttpRequest request, ClaimsPrincipal user, RemoteOperationUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.UpdateStatusAsync(SyncApiEndpoints.Caller(user), operationId, await ReadAsync<OperationStatusUpdate>(request, cancellationToken), cancellationToken)));

        remote.MapPost("/operations/{operationId:guid}/events", async (Guid operationId, HttpRequest request, ClaimsPrincipal user, RemoteOperationUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.AppendEventsAsync(SyncApiEndpoints.Caller(user), operationId, await ReadAsync<EventBatch>(request, cancellationToken), cancellationToken)));

        remote.MapGet("/operations/{operationId:guid}/events", async (Guid operationId, long? after, int? limit, ClaimsPrincipal user, RemoteOperationUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.ListEventsAsync(SyncApiEndpoints.Caller(user), operationId, after, limit, cancellationToken)));

        remote.MapPost("/operations/{operationId:guid}/cancel", async (Guid operationId, ClaimsPrincipal user, RemoteOperationUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.CancelAsync(SyncApiEndpoints.Caller(user), operationId, cancellationToken)));

        // As aprovações: o agente alvo pede, a conta vê as pendentes, e um controlador responde assinando.
        remote.MapPost("/operations/{operationId:guid}/approvals", async (Guid operationId, HttpRequest request, ClaimsPrincipal user, RemoteApprovalUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.RequestAsync(SyncApiEndpoints.Caller(user), operationId, await ReadAsync<ApprovalRequest>(request, cancellationToken), cancellationToken), StatusCodes.Status201Created));

        remote.MapGet("/approvals", async (ClaimsPrincipal user, RemoteApprovalUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.ListPendingAsync(SyncApiEndpoints.Caller(user), cancellationToken)));

        remote.MapGet("/approvals/{approvalId:guid}", async (Guid approvalId, ClaimsPrincipal user, RemoteApprovalUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.GetAsync(SyncApiEndpoints.Caller(user), approvalId, cancellationToken)));

        remote.MapPost("/approvals/{approvalId:guid}/response", async (Guid approvalId, HttpRequest request, ClaimsPrincipal user, RemoteApprovalUseCases useCases, CancellationToken cancellationToken) =>
            Json(await useCases.RespondAsync(SyncApiEndpoints.Caller(user), approvalId, await ReadAsync<SignedEnvelope>(request, cancellationToken), cancellationToken)));
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

    private static IResult Json<T>(T value, int statusCode = StatusCodes.Status200OK) => TypedResults.Json(value, RemoteJson.Options, statusCode: statusCode);
}
