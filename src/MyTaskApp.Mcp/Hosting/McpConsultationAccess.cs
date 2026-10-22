using System.Security.Claims;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Consultations;

namespace MyTaskApp.Mcp.Hosting;

/// <summary>
/// Quem está do outro lado de uma requisição MCP (ADR-081): o cliente que o
/// usuário configurou, com o token do servidor, ou o Claude de uma Consulta
/// Inteligente, com o segredo daquela sessão. Vai no
/// <see cref="ClaimsPrincipal"/> da requisição — o SDK o entrega aos filtros —
/// e, enquanto a requisição corre, em <see cref="Current"/>.
/// </summary>
public static class McpCaller
{
    public const string AuthenticationType = "MyTaskApp";

    private const string KindClaim = "mytaskapp:caller";

    private const string SessionClaim = "mytaskapp:consultation-session";

    private const string TaskClaim = "mytaskapp:consultation-task";

    private const string Application = "app";

    private const string Consultation = "consultation";

    private static readonly AsyncLocal<ConsultationAccess?> CurrentConsultation = new();

    /// <summary>A consulta que fez a requisição em curso; <c>null</c> = o cliente do token do servidor.</summary>
    public static ConsultationAccess? Current => CurrentConsultation.Value;

    public static ClaimsPrincipal ForApplication() =>
        new(new ClaimsIdentity([new Claim(KindClaim, Application)], AuthenticationType));

    public static ClaimsPrincipal ForConsultation(ConsultationAccess access) =>
        new(new ClaimsIdentity(
            [
                new Claim(KindClaim, Consultation),
                new Claim(SessionClaim, access.SessionId.ToString()),
                new Claim(TaskClaim, access.TaskId.ToString()),
            ],
            AuthenticationType));

    /// <summary>A consulta do principal; <c>null</c> para o cliente do token do servidor (ou sem principal).</summary>
    public static ConsultationAccess? ConsultationOf(ClaimsPrincipal? user) =>
        user?.FindFirst(KindClaim)?.Value == Consultation
        && Guid.TryParse(user.FindFirst(SessionClaim)?.Value, out var session)
        && Guid.TryParse(user.FindFirst(TaskClaim)?.Value, out var task)
            ? new ConsultationAccess(session, task)
            : null;

    /// <summary>Até o descarte, <see cref="Current"/> é <paramref name="access"/>.</summary>
    public static IDisposable Enter(ConsultationAccess? access)
    {
        var previous = CurrentConsultation.Value;
        CurrentConsultation.Value = access;
        return new Scope(previous);
    }

    private sealed class Scope(ConsultationAccess? previous) : IDisposable
    {
        public void Dispose() => CurrentConsultation.Value = previous;
    }
}

/// <summary>
/// Confere o segredo de uma Consulta Inteligente (ADR-081): pelo hash, contra
/// as sessões de consulta ativas. Fora do <see cref="McpGateway"/> e da fila
/// dele: é só uma leitura, e acontece antes de qualquer ferramenta.
/// </summary>
public sealed class McpConsultationAccess(IUseCaseRunner runner, ILogger<McpConsultationAccess> logger)
{
    public async Task<ConsultationAccess?> ResolveAsync(string? token, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        try
        {
            return await runner.RunAsync<ResolveConsultationAccessHandler, ConsultationAccess?>(
                (handler, cancel) => handler.HandleAsync(new ResolveConsultationAccess(token), cancel),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Na dúvida, não entra: o pedido recebe 401, e o motivo fica no log.
            logger.LogWarning(exception, "McpConsultationAccessFailed");
            return null;
        }
    }
}

/// <summary>
/// O servidor MCP visto pela Consulta Inteligente: o endereço enquanto está no
/// ar, e nada quando não.
/// </summary>
public sealed class McpConsultationEndpoint(IMcpServerManager manager) : IConsultationToolsEndpoint
{
    public Uri? Address => manager.Status is { IsRunning: true, Endpoint: { } endpoint } ? endpoint : null;
}
