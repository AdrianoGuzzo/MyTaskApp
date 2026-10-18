using System.Net;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
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
            logger.LogError(exception, "SyncServerUnhandled {Path}", httpContext.Request.Path.ToUriComponent());
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

/// <summary>
/// Revoga, no OpenIddict, o refresh token do dispositivo (e o código, se
/// sobrou um). A autorização e o access token ficam: com a validação do token
/// (SEC-010), revogados eles levariam só 401, e o app não saberia que precisa
/// de um id novo. Até vencer, o access leva 403 <c>device_revoked</c> do
/// <see cref="DeviceFilter"/>.
/// </summary>
internal sealed class OpenIddictAuthorizationRevoker(
    ServerDbContext database,
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

        // Lidos antes: revogar no meio da leitura seria um segundo comando na mesma conexão.
        var alive = new List<object>();

        await foreach (var token in tokens.FindByAuthorizationIdAsync(authorizationId, cancellationToken))
        {
            if (await tokens.HasStatusAsync(token, Statuses.Valid, cancellationToken)
                && !await tokens.HasTypeAsync(token, TokenTypeIdentifiers.AccessToken, cancellationToken))
            {
                alive.Add(token);
            }
        }

        foreach (var token in alive)
        {
            await tokens.TryRevokeAsync(token, cancellationToken);
        }
    }
}

/// <summary>
/// Cria (ou atualiza) os clientes públicos ao subir: sem secret, com PKCE
/// obrigatório e só os redirects configurados — loopback para o desktop, o
/// esquema próprio do app para o celular (ADR-067). Cada cliente só aceita os
/// seus: um código emitido para um não vira token no outro.
/// </summary>
/// <remarks>
/// Nativos: com o redirect de loopback registrado sem porta, o OpenIddict
/// aceita qualquer porta nele (RFC 8252 §7.3), e o desktop escuta numa porta
/// efêmera (ADR-074). O resto do endereço continua exato.
/// </remarks>
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
            ApplicationType = ApplicationTypes.Native,
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

/// <summary>
/// A chave do Data Protection gravada em claro (SEC-011, ADR-073): a de antes
/// do certificado, ou a que voltou com um backup antigo. Quem leu o banco a
/// conhece, então ela é revogada ao subir e não assina mais cookie nenhum; a
/// próxima o Data Protection gera já cifrada. Só sobe com o certificado.
/// </summary>
internal sealed class PlaintextKeyRevoker(IServiceProvider services, IKeyManager keys, ILogger<PlaintextKeyRevoker> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var scope = services.CreateAsyncScope();
        var stored = await scope.ServiceProvider.GetRequiredService<ServerDbContext>().DataProtectionKeys
            .AsNoTracking()
            .Select(row => row.Xml)
            .ToListAsync(cancellationToken);

        // As linhas de revogação são <revocation>, e não <key>: ficam de fora.
        var plaintext = stored
            .Where(xml => !string.IsNullOrEmpty(xml))
            .Select(xml => XElement.Parse(xml!))
            .Where(element => element.Name.LocalName == "key"
                && !element.Descendants().Any(child => child.Name.LocalName == "encryptedSecret"))
            .Select(element => Guid.Parse((string)element.Attribute("id")!))
            .ToHashSet();

        if (plaintext.Count == 0)
        {
            return;
        }

        // A já revogada fica como está: revogar de novo só somaria linhas a cada subida.
        var revoked = 0;

        foreach (var key in keys.GetAllKeys().Where(key => !key.IsRevoked && plaintext.Contains(key.KeyId)))
        {
            keys.RevokeKey(key.KeyId, "SEC-011: chave gravada em claro no banco.");
            revoked++;
        }

        if (revoked > 0)
        {
            logger.LogWarning("SyncServerPlaintextKeysRevoked {Count}", revoked);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
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

/// <summary>
/// Development não serve a rede (ADR-070): ali o OAuth anda em HTTP e os
/// certificados são os de desenvolvimento. Depois que o Kestrel abre as portas,
/// um endereço fora do loopback derruba a subida. A exceção é explícita
/// (<see cref="AllowNonLoopbackKey"/>): o contêiner do compose de
/// desenvolvimento escuta em todas as interfaces dele, mas a porta só é
/// publicada no 127.0.0.1 da máquina.
/// </summary>
internal sealed class DevelopmentLoopbackGuard(
    IServer server,
    IHostEnvironment environment,
    IConfiguration configuration,
    ILogger<DevelopmentLoopbackGuard> logger) : IHostedLifecycleService
{
    public const string AllowNonLoopbackKey = "Development:AllowNonLoopback";

    public Task StartedAsync(CancellationToken cancellationToken)
    {
        if (!environment.IsDevelopment() || configuration.GetValue<bool>(AllowNonLoopbackKey))
        {
            return Task.CompletedTask;
        }

        var exposed = (server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [])
            .Where(address => !IsLoopback(address))
            .ToList();

        if (exposed.Count == 0)
        {
            return Task.CompletedTask;
        }

        var refusal = new InvalidOperationException(
            $"Em Development o servidor só escuta no loopback, e está em {string.Join(", ", exposed)}. "
            + "Para servir a rede, use Production atrás de TLS (docs/sync-server.md §5). "
            + "No contêiner de desenvolvimento, com a porta publicada só no 127.0.0.1, Development__AllowNonLoopback=true.");

        logger.LogCritical(refusal, "SyncServerDevelopmentOnNetwork {Addresses}", exposed);

        throw refusal;
    }

    /// <summary><c>localhost</c>, 127.0.0.0/8 e ::1, e os sockets da própria máquina (Unix e pipe nomeado).</summary>
    internal static bool IsLoopback(string address)
    {
        var binding = BindingAddress.Parse(address);

        if (binding.IsUnixPipe || binding.IsNamedPipe)
        {
            return true;
        }

        var host = binding.Host.Trim('[', ']');

        return string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var ip) && IPAddress.IsLoopback(ip));
    }

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
