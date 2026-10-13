using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using MyTaskApp.Server.Application;
using MyTaskApp.Server.Infrastructure.Persistence;
using MyTaskApp.Sync.Contracts;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyTaskApp.Server.Api;

/// <summary>
/// A API v1 (ADR-060). Só Bearer do OpenIddict: o cookie da sessão de login
/// nunca autoriza nada aqui. Toda rota passa pela conferência do dispositivo
/// (não revogado), e as do workspace pela de membro.
/// </summary>
internal static class SyncApiEndpoints
{
    public static void MapSyncApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup(ApiRoutes.Prefix)
            .RequireAuthorization(ServerSetup.SyncPolicy)
            .AddEndpointFilter<DeviceFilter>();

        api.MapGet("/me", MeAsync).RequireRateLimiting(ServerSetup.ApiLimiter);

        api.MapGet("/workspaces", (ClaimsPrincipal user, SyncQueries queries, CancellationToken cancellationToken) =>
                queries.WorkspacesAsync(Caller(user), cancellationToken))
            .RequireRateLimiting(ServerSetup.ApiLimiter);

        var devices = api.MapGroup("/devices").RequireRateLimiting(ServerSetup.ApiLimiter);

        devices.MapPut("/current", (DeviceHello hello, ClaimsPrincipal user, DeviceUseCases useCases, CancellationToken cancellationToken) =>
            useCases.HelloAsync(Caller(user), hello, cancellationToken));

        devices.MapGet("/", (ClaimsPrincipal user, DeviceUseCases useCases, CancellationToken cancellationToken) =>
            useCases.ListAsync(Caller(user), cancellationToken));

        devices.MapPatch("/{deviceId:guid}", (Guid deviceId, RenameDeviceRequest request, ClaimsPrincipal user, DeviceUseCases useCases, CancellationToken cancellationToken) =>
            useCases.RenameAsync(Caller(user), deviceId, request.Nickname, cancellationToken));

        devices.MapPost("/{deviceId:guid}/revoke", async (Guid deviceId, ClaimsPrincipal user, DeviceUseCases useCases, DeviceGate gate, CancellationToken cancellationToken) =>
        {
            var caller = Caller(user);
            var revoked = await useCases.RevokeAsync(caller, deviceId, cancellationToken);
            gate.Forget(caller.UserId, deviceId);
            return revoked;
        });

        var sync = api.MapGroup("/workspaces/{workspaceId:guid}/sync");

        sync.MapPost("/push", (Guid workspaceId, PushRequest request, ClaimsPrincipal user, PushProcessor push, CancellationToken cancellationToken) =>
                push.PushAsync(Caller(user), workspaceId, request, cancellationToken))
            .RequireRateLimiting(ServerSetup.SyncLimiter);

        sync.MapGet("/pull", (Guid workspaceId, string? cursor, int? limit, ClaimsPrincipal user, SyncQueries queries, CancellationToken cancellationToken) =>
                queries.PullAsync(Caller(user), workspaceId, cursor, limit, cancellationToken))
            .RequireRateLimiting(ServerSetup.SyncLimiter);

        sync.MapPost("/fetch", (Guid workspaceId, FetchRequest request, ClaimsPrincipal user, SyncQueries queries, CancellationToken cancellationToken) =>
                queries.FetchAsync(Caller(user), workspaceId, request, cancellationToken))
            .RequireRateLimiting(ServerSetup.SyncLimiter);

        sync.MapGet("/status", (Guid workspaceId, ClaimsPrincipal user, SyncQueries queries, CancellationToken cancellationToken) =>
                queries.StatusAsync(Caller(user), workspaceId, cancellationToken))
            .RequireRateLimiting(ServerSetup.ApiLimiter);
    }

    private static async Task<MeResponse> MeAsync(
        ClaimsPrincipal user,
        UserManager<ApplicationUser> users,
        IDeviceRegistry devices,
        CancellationToken cancellationToken)
    {
        var caller = Caller(user);
        var account = await users.FindByIdAsync(caller.UserId.ToString()) ?? throw SyncServerException.NotFound();
        var device = await devices.FindAsync(caller.UserId, caller.DeviceId, cancellationToken);

        return new MeResponse(
            new UserInfo(account.Id, account.DisplayName, account.Email),
            device?.ToInfo(caller.DeviceId));
    }

    /// <summary>Quem chama, pelas claims do token. A política já garantiu que as duas existem.</summary>
    public static SyncCaller Caller(ClaimsPrincipal user) => new(
        Guid.Parse(user.GetClaim(Claims.Subject)!),
        Guid.Parse(user.GetClaim(SyncSchema.DeviceIdClaim)!));
}
