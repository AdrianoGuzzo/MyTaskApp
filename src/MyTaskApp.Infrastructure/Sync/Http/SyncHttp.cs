using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Sync;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Infrastructure.Sync.Http;

/// <summary>
/// O único lugar que fala HTTP com o servidor de sincronização (ADR-061).
/// Uma linha de log por chamada: método, caminho (sem a query), status e
/// tempo. Nunca cabeçalho, corpo, código de autorização nem token. Não segue
/// redirecionamento (ADR-078): um 3xx é erro, e o Bearer fica onde estava.
/// </summary>
/// <remarks>
/// O <c>HttpClient</c> é cru, sem o <c>IHttpClientFactory</c>, pelo mesmo
/// motivo do <c>JiraHttp</c>: o log de depuração da fábrica registra os
/// cabeçalhos, e o Bearer iria junto.
/// </remarks>
internal sealed class SyncHttp(HttpClient client, SyncOptions options, TimeProvider timeProvider, ILogger<SyncHttp> logger)
{
    /// <summary>O mesmo cliente, sem redirecionamento, para o "Testar conexão" (<see cref="SyncServerProbe"/>).</summary>
    public HttpClient Client => client;

    public async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body,
        string accessToken,
        CancellationToken cancellationToken,
        JsonSerializerOptions? json = null)
    {
        // O sync usa o SyncJson; a orquestração remota passa o RemoteJson, mais estrito.
        json ??= SyncJson.Options;

        using var request = new HttpRequestMessage(method, Resolve(path));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType(), options: json);
        }

        using var response = await SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw await FailureAsync(response, cancellationToken);
        }

        return await response.Content.ReadFromJsonAsync<T>(json, cancellationToken)
            ?? throw new SyncApiException(SyncFailureKind.Transient, "O servidor devolveu uma resposta vazia.");
    }

    /// <summary>
    /// Os endpoints do OAuth (<c>/connect/token</c>, <c>/connect/revoke</c>):
    /// formulário, e o erro vem no corpo como <c>error</c>.
    /// </summary>
    public async Task<JsonDocument> PostFormAsync(string path, IReadOnlyDictionary<string, string> form, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, Resolve(path)) { Content = new FormUrlEncodedContent(form) };
        using var response = await SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.IsSuccessStatusCode)
        {
            return text.Length == 0 ? JsonDocument.Parse("{}") : JsonDocument.Parse(text);
        }

        var error = TryRead(text, "error");

        throw error switch
        {
            "invalid_grant" => new SyncApiException(SyncFailureKind.NeedsReconnect, "A conexão com a conta expirou. Conecte de novo.", error),
            _ when (int)response.StatusCode >= 500 || response.StatusCode == HttpStatusCode.TooManyRequests =>
                new SyncApiException(SyncFailureKind.Transient, "O servidor de sincronização está indisponível.", error, RetryAfter(response)),
            _ => new SyncApiException(SyncFailureKind.Rejected, "O servidor recusou o login.", error),
        };
    }

    public const string RedirectMessage = "O servidor redirecionou para outro endereço. Configure o endereço final do servidor.";

    public static bool IsRedirect(HttpStatusCode status) => (int)status is >= 300 and < 400;

    private Uri Resolve(string path) =>
        new(options.BaseUri ?? throw new SyncApiException(SyncFailureKind.Rejected, "Nenhum servidor de sincronização configurado."), path.TrimStart('/'));

    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);

        var started = Stopwatch.GetTimestamp();

        try
        {
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);

            logger.LogInformation(
                "SyncHttp {Method} {Path} {Status} {ElapsedMs:0}",
                request.Method.Method,
                request.RequestUri!.AbsolutePath,
                (int)response.StatusCode,
                Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            if (IsRedirect(response.StatusCode))
            {
                // O cliente não segue redirecionamento (ADR-078): o token e o formulário não vão para outro endereço.
                response.Dispose();
                throw new SyncApiException(SyncFailureKind.Rejected, RedirectMessage);
            }

            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("SyncHttpTimeout {Method} {Path}", request.Method.Method, request.RequestUri!.AbsolutePath);
            throw new SyncApiException(SyncFailureKind.Transient, "O servidor de sincronização demorou demais para responder.");
        }
        catch (HttpRequestException exception)
        {
            // Só o tipo da falha: a mensagem pode trazer o endereço inteiro.
            logger.LogWarning("SyncHttpUnreachable {Method} {Path} {Error}", request.Method.Method, request.RequestUri!.AbsolutePath, exception.HttpRequestError);
            throw new SyncApiException(SyncFailureKind.Transient, "Sem conexão com o servidor de sincronização.");
        }
    }

    private async Task<SyncApiException> FailureAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        var code = TryRead(text, "code");
        var title = TryRead(text, "title");

        return response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new SyncApiException(SyncFailureKind.Unauthorized, "O servidor não aceitou o login.", code),
            HttpStatusCode.Forbidden when code == SyncErrorCodes.DeviceRevoked =>
                new SyncApiException(SyncFailureKind.DeviceRevoked, title ?? "Este dispositivo foi revogado.", code),
            HttpStatusCode.Forbidden => new SyncApiException(SyncFailureKind.Forbidden, title ?? "Sem permissão.", code),
            HttpStatusCode.NotFound => new SyncApiException(SyncFailureKind.NotFound, title ?? "Não encontrado no servidor.", code),
            HttpStatusCode.Gone => new SyncApiException(SyncFailureKind.CursorAhead, title ?? "Sincronize de novo desde o começo.", code),
            HttpStatusCode.UpgradeRequired => new SyncApiException(SyncFailureKind.UpgradeRequired, title ?? "Atualize o MyTaskApp para sincronizar.", code),
            HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests =>
                new SyncApiException(SyncFailureKind.Transient, "O servidor pediu para esperar.", code, RetryAfter(response)),
            _ when (int)response.StatusCode >= 500 =>
                new SyncApiException(SyncFailureKind.Transient, "O servidor de sincronização está com problema.", code, RetryAfter(response)),
            _ => new SyncApiException(SyncFailureKind.Rejected, title ?? "O servidor recusou o pedido.", code),
        };
    }

    private TimeSpan? RetryAfter(HttpResponseMessage response) =>
        response.Headers.RetryAfter?.Delta
        ?? (response.Headers.RetryAfter?.Date is { } date ? date - timeProvider.GetUtcNow() : null);

    private static string? TryRead(string json, string property)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(property, out var value)
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
