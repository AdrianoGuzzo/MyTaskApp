namespace MyTaskApp.Server.Api;

/// <summary>
/// <c>nosniff</c> em toda resposta, e <c>no-store</c> nas rotas com dado de
/// conta ou de protocolo (<c>/api/v1</c>, <c>/connect</c>, <c>/account</c>):
/// nenhum proxy ou CDN no caminho guarda o <c>/me</c> ou um pull. O discovery e
/// o JWKS ficam de fora e continuam cacheáveis (SEC-009). Os headers entram no
/// <c>OnStarting</c> porque o tratamento de exceção limpa os que já estavam na
/// resposta.
/// </summary>
internal sealed class SecurityHeaders(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";

            if (IsPrivate(context.Request.Path))
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        });

        return next(context);
    }

    private static bool IsPrivate(PathString path) =>
        path.StartsWithSegments("/api/v1") || path.StartsWithSegments("/connect") || path.StartsWithSegments("/account");
}
