using Microsoft.AspNetCore.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using MyTaskApp.Server.Application;
using MyTaskApp.Server.Infrastructure.Persistence;
using MyTaskApp.Sync.Contracts;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace MyTaskApp.Server.Api;

/// <summary>
/// "Este dispositivo ainda vale?", com memória curta: cada chamada não precisa
/// ir ao banco, e revogar esquece na hora a resposta guardada neste servidor.
/// </summary>
internal sealed class DeviceGate(DeviceUseCases devices, IMemoryCache cache, SyncServerOptions options)
{
    public async Task<bool> IsActiveAsync(SyncCaller caller, CancellationToken cancellationToken)
    {
        if (cache.TryGetValue(Key(caller.UserId, caller.DeviceId), out bool active))
        {
            return active;
        }

        active = await devices.IsActiveAsync(caller, cancellationToken);
        cache.Set(Key(caller.UserId, caller.DeviceId), active, options.DeviceCacheLifetime);

        return active;
    }

    public void Forget(Guid userId, Guid deviceId) => cache.Remove(Key(userId, deviceId));

    private static string Key(Guid userId, Guid deviceId) => $"device:{userId:N}:{deviceId:N}";
}

/// <summary>Toda rota da API: o dispositivo do token não pode ter sido revogado.</summary>
internal sealed class DeviceFilter(DeviceGate gate) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var caller = SyncApiEndpoints.Caller(context.HttpContext.User);

        if (!await gate.IsActiveAsync(caller, context.HttpContext.RequestAborted))
        {
            throw SyncServerException.DeviceRevoked();
        }

        return await next(context);
    }
}

/// <summary>
/// A recusa do caso de uso vira ProblemDetails com o código do contrato; o
/// resto vira 500 sem detalhe, com a pilha só no log do servidor.
/// </summary>
internal sealed class SyncExceptionHandler(IProblemDetailsService problems, ILogger<SyncExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, code, title) = exception switch
        {
            SyncServerException sync => (sync.Status, sync.Code, sync.Message),
            BadHttpRequestException bad => (bad.StatusCode, SyncErrorCodes.BadRequest, "Pedido inválido."),
            _ => (StatusCodes.Status500InternalServerError, "server_error", "Erro inesperado no servidor."),
        };

        if (status >= 500)
        {
            logger.LogError(exception, "SyncServerUnhandled {Path}", httpContext.Request.Path);
        }

        httpContext.Response.StatusCode = status;

        return await problems.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails =
            {
                Status = status,
                Title = title,
                Extensions = { ["code"] = code },
            },
        });
    }
}

/// <summary>Revoga, no OpenIddict, a autorização viva do dispositivo e os tokens dela.</summary>
internal sealed class OpenIddictAuthorizationRevoker(
    ServerDbContext database,
    IOpenIddictAuthorizationManager authorizations,
    IOpenIddictTokenManager tokens) : IAuthorizationRevoker
{
    public async Task RevokeDeviceAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken)
    {
        var device = await database.Devices.AsNoTracking()
            .SingleOrDefaultAsync(row => row.UserId == userId && row.DeviceId == deviceId, cancellationToken);

        if (device?.CurrentAuthorizationId is not { } authorizationId)
        {
            return;
        }

        if (await authorizations.FindByIdAsync(authorizationId, cancellationToken) is { } authorization)
        {
            await authorizations.TryRevokeAsync(authorization, cancellationToken);
        }

        await tokens.RevokeByAuthorizationIdAsync(authorizationId, cancellationToken);
    }
}

/// <summary>
/// Cria (ou atualiza) os clientes públicos ao subir: sem secret, com PKCE
/// obrigatório e só os redirects configurados — loopback para o desktop, o
/// esquema próprio do app para o celular (ADR-065). Cada cliente só aceita os
/// seus: um código emitido para um não vira token no outro.
/// </summary>
internal sealed class PublicClientSeeder(IServiceProvider services, SyncServerOptions options) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();

        await SeedAsync(applications, SyncSchema.DesktopClientId, "MyTaskApp (desktop)", options.DesktopRedirectUris, cancellationToken);

        if (options.MobileRedirectUris.Length > 0)
        {
            await SeedAsync(applications, SyncSchema.MobileClientId, "MyTaskApp (celular)", options.MobileRedirectUris, cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task SeedAsync(
        IOpenIddictApplicationManager applications,
        string clientId,
        string displayName,
        IEnumerable<string> redirectUris,
        CancellationToken cancellationToken)
    {
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = clientId,
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            DisplayName = displayName,
            Permissions =
            {
                Permissions.Endpoints.Authorization,
                Permissions.Endpoints.Token,
                Permissions.Endpoints.Revocation,
                Permissions.GrantTypes.AuthorizationCode,
                Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code,
                Permissions.Scopes.Email,
                Permissions.Scopes.Profile,
                Permissions.Prefixes.Scope + SyncSchema.Scope,
            },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange },
        };

        foreach (var uri in redirectUris)
        {
            descriptor.RedirectUris.Add(new Uri(uri));
        }

        if (await applications.FindByClientIdAsync(clientId, cancellationToken) is { } existing)
        {
            await applications.UpdateAsync(existing, descriptor, cancellationToken);
        }
        else
        {
            await applications.CreateAsync(descriptor, cancellationToken);
        }
    }
}

/// <summary>Esquece os resultados de operação antigos: depois de 90 dias, nenhum cliente reenvia.</summary>
internal sealed class ProcessedOperationsPruner(IServiceProvider services, SyncServerOptions options, TimeProvider timeProvider, ILogger<ProcessedOperationsPruner> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6), timeProvider);

        do
        {
            try
            {
                await using var scope = services.CreateAsyncScope();
                var database = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
                var limit = timeProvider.GetUtcNow() - options.ProcessedOperationRetention;
                var removed = await database.ProcessedOperations.Where(row => row.ProcessedAt < limit).ExecuteDeleteAsync(stoppingToken);

                if (removed > 0)
                {
                    logger.LogInformation("SyncProcessedOperationsPruned {Count}", removed);
                }
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                logger.LogWarning(exception, "SyncProcessedOperationsPruneFailed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
