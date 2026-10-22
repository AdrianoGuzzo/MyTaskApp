using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using MyTaskApp.Application.Consultations;

namespace MyTaskApp.Mcp.Hosting;

/// <summary>
/// A porta de entrada do servidor MCP (ADR-059), antes de o SDK ver a
/// requisição. Quatro barreiras, nesta ordem:
/// <list type="number">
/// <item>só conexão vinda do loopback — o servidor nem escuta fora dele, e isto é a segunda trava;</item>
/// <item><c>Host</c> tem de ser <c>127.0.0.1</c> ou <c>localhost</c> na porta certa — contra DNS rebinding;</item>
/// <item>qualquer <c>Origin</c> é recusado — cliente MCP não é página web, e não há CORS;</item>
/// <item>o token do <c>Authorization: Bearer</c>: o do servidor, ou o segredo de uma Consulta Inteligente aberta (ADR-081).</item>
/// </list>
/// Quem passou vai no <see cref="HttpContext.User"/> (<see cref="McpCaller"/>):
/// é por ele que os filtros deixam a consulta ver só as ferramentas dela.
/// </summary>
internal sealed class McpSecurityMiddleware(
    RequestDelegate next,
    McpAccessGuard guard,
    McpConsultationAccess consultations,
    McpActivityLog activity,
    TimeProvider timeProvider,
    ILogger<McpSecurityMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (Refusal(context) is { } refusal)
        {
            await DenyAsync(context, refusal);
            return;
        }

        var token = context.Request.Headers.Authorization.ToString()["Bearer ".Length..].Trim();
        ConsultationAccess? consultation = null;

        if (!guard.Accepts(token))
        {
            consultation = await consultations.ResolveAsync(token, context.RequestAborted);

            if (consultation is null)
            {
                await DenyAsync(context, (StatusCodes.Status401Unauthorized, "Token de acesso inválido.", "Unauthorized"));
                return;
            }
        }

        context.User = consultation is null ? McpCaller.ForApplication() : McpCaller.ForConsultation(consultation);

        using (McpCaller.Enter(consultation))
        {
            await next(context);
        }
    }

    private async Task DenyAsync(HttpContext context, (int Status, string Reason, string Body) refusal)
    {
        // O motivo vai para o log e para a tela; a resposta diz só o mínimo.
        Record(refusal.Reason, refusal.Status);

        context.Response.StatusCode = refusal.Status;

        if (refusal.Status == StatusCodes.Status401Unauthorized)
        {
            context.Response.Headers[HeaderNames.WWWAuthenticate] = "Bearer";
        }

        await context.Response.WriteAsync(refusal.Body, context.RequestAborted);
    }

    /// <summary>Um registro por intervalo, no máximo: uma página disparando milhares de pedidos não enche o log nem a tela.</summary>
    internal static readonly TimeSpan DenialRecordInterval = TimeSpan.FromSeconds(1);

    private readonly Lock _denials = new();
    private DateTimeOffset _lastDenialRecord = DateTimeOffset.MinValue;
    private int _suppressedDenials;

    private void Record(string reason, int status)
    {
        int suppressed;
        var now = timeProvider.GetUtcNow();

        lock (_denials)
        {
            if (now - _lastDenialRecord < DenialRecordInterval)
            {
                _suppressedDenials++;
                return;
            }

            _lastDenialRecord = now;
            suppressed = _suppressedDenials;
            _suppressedDenials = 0;
        }

        var detail = suppressed == 0 ? reason : $"{reason} (+{suppressed} barrados no último segundo)";

        logger.LogWarning("McpRequestDenied {Reason} {Status} {Suppressed}", reason, status, suppressed);
        activity.Add(new McpActivityEntry(now, "http", McpActivityOutcome.Denied, TimeSpan.Zero, detail));
    }

    private (int Status, string Reason, string Body)? Refusal(HttpContext context)
    {
        var remote = context.Connection.RemoteIpAddress;

        if (remote is null || !IPAddress.IsLoopback(remote))
        {
            return (StatusCodes.Status403Forbidden, "Conexão de fora do loopback.", "Forbidden");
        }

        if (!IsLocalHost(context.Request.Host, context.Connection.LocalPort))
        {
            return (StatusCodes.Status400BadRequest, $"Host não aceito: {Shown(context.Request.Host.Value)}.", "Bad Request");
        }

        if (context.Request.Headers.ContainsKey(HeaderNames.Origin))
        {
            return (StatusCodes.Status403Forbidden, "Requisição com Origin (navegador) recusada.", "Forbidden");
        }

        var authorization = context.Request.Headers.Authorization.ToString();

        // Sem token do servidor definido, ninguém passa — nem uma consulta.
        if (!guard.HasToken
            || !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            || authorization["Bearer ".Length..].Trim().Length == 0)
        {
            return (
                StatusCodes.Status401Unauthorized,
                authorization.Length == 0 ? "Sem token de acesso." : "Token de acesso inválido.",
                "Unauthorized");
        }

        return null;
    }

    private static bool IsLocalHost(HostString host, int localPort) =>
        host.HasValue
        && host.Port == localPort
        && (string.Equals(host.Host, "127.0.0.1", StringComparison.Ordinal)
            || string.Equals(host.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    /// <summary>O host que veio, cortado: é texto do cliente, e vai para o log.</summary>
    private static string Shown(string? value) =>
        string.IsNullOrEmpty(value)
            ? "(vazio)"
            : new string(value.Where(c => !char.IsControl(c)).Take(80).ToArray());
}
