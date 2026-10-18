using System.Diagnostics;

namespace MyTaskApp.Server.Api;

/// <summary>
/// Uma linha por requisição: método, caminho, status e tempo. Nunca a query
/// (no <c>/connect/authorize</c> ela leva o <c>code_challenge</c> e o
/// <c>state</c>), nem cabeçalho (o Bearer), nem corpo (os dados do usuário).
/// Mesma regra do <c>JiraHttp</c> do desktop (ADR-045). O caminho vai
/// reescapado, como veio na URL: decodificado, um <c>%0d%0a</c> forjava uma
/// linha do log e um <c>%1b</c> mandava sequência ao terminal (SEC-019).
/// </summary>
internal sealed class RequestLog(RequestDelegate next, ILogger<RequestLog> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var started = Stopwatch.GetTimestamp();

        try
        {
            await next(context);
        }
        finally
        {
            logger.LogInformation(
                "HTTP {Method} {Path} {Status} {ElapsedMs:0}",
                context.Request.Method,
                context.Request.Path.ToUriComponent(),
                context.Response.StatusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}
