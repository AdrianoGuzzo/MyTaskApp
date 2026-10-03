using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace MyTaskApp.Infrastructure.Jira;

/// <summary>O que deu errado numa ida ao Jira, sem o detalhe que não pode vazar.</summary>
internal enum JiraHttpFailure
{
    Network,
    Timeout,
    Unauthorized,
    Forbidden,
    NotFound,
    BadRequest,
    RateLimited,
    Server,
}

/// <summary>
/// A chamada falhou. A mensagem tem o tipo de falha e o status — nunca a URL
/// completa, o corpo ou cabeçalho, onde moram token e e-mail.
/// </summary>
internal sealed class JiraHttpException(JiraHttpFailure failure, int? status = null)
    : Exception($"Jira: {failure}{(status is null ? string.Empty : $" ({status})")}")
{
    public JiraHttpFailure Failure { get; } = failure;

    public int? Status { get; } = status;
}

/// <summary>
/// O único lugar que fala HTTP com a Atlassian (ADR-045): timeout por
/// chamada, erro traduzido e um log que não registra segredo.
/// </summary>
/// <remarks>
/// <para>
/// <b>O log.</b> Uma linha por chamada: método, <b>caminho</b> (sem a query,
/// que leva o texto que o usuário digitou), status e tempo. Nunca cabeçalho,
/// nunca corpo. O <c>HttpClient</c> é cru, sem o <c>IHttpClientFactory</c>,
/// cujo log padrão registra cabeçalhos em nível de depuração.
/// </para>
/// <para>
/// <b>Timeout e cancelamento são coisas diferentes.</b> O cancelamento é do
/// chamador (a busca foi superada por outra tecla) e sobe como
/// <see cref="OperationCanceledException"/>. O timeout é do Jira demorar e vira
/// <see cref="JiraHttpFailure.Timeout"/>.
/// </para>
/// </remarks>
internal sealed class JiraHttp(HttpClient client, JiraOptions options, ILogger<JiraHttp> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<JsonDocument> GetAsync(
        Uri uri,
        AuthenticationHeaderValue? authorization,
        CancellationToken cancellationToken) =>
        SendAsync(HttpMethod.Get, uri, authorization, null, cancellationToken);

    public Task<JsonDocument> PostJsonAsync(
        Uri uri,
        object body,
        AuthenticationHeaderValue? authorization,
        CancellationToken cancellationToken) =>
        SendAsync(
            HttpMethod.Post,
            uri,
            authorization,
            new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
            cancellationToken);

    private async Task<JsonDocument> SendAsync(
        HttpMethod method,
        Uri uri,
        AuthenticationHeaderValue? authorization,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        request.Headers.Authorization = authorization;
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.RequestTimeout);

        var watch = Stopwatch.StartNew();
        int? status = null;

        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = (int)response.StatusCode;

            if (!response.IsSuccessStatusCode)
            {
                throw new JiraHttpException(FailureFor(response.StatusCode), status);
            }

            await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
            return await JsonDocument.ParseAsync(stream, cancellationToken: timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new JiraHttpException(JiraHttpFailure.Timeout);
        }
        catch (HttpRequestException exception)
        {
            // A mensagem do HttpRequestException pode trazer o host; o log leva
            // só o tipo do erro de socket, que é o que diagnostica.
            logger.LogWarning("JiraRequestFailed {Method} {Path} {Error}", method, uri.AbsolutePath, exception.HttpRequestError);
            throw new JiraHttpException(JiraHttpFailure.Network);
        }
        catch (JsonException)
        {
            throw new JiraHttpException(JiraHttpFailure.Server, status);
        }
        finally
        {
            logger.LogInformation(
                "JiraRequest {Method} {Path} {Status} {ElapsedMs}",
                method,
                uri.AbsolutePath,
                status,
                watch.ElapsedMilliseconds);
        }
    }

    private static JiraHttpFailure FailureFor(HttpStatusCode status) => status switch
    {
        HttpStatusCode.Unauthorized => JiraHttpFailure.Unauthorized,
        HttpStatusCode.Forbidden => JiraHttpFailure.Forbidden,
        HttpStatusCode.NotFound => JiraHttpFailure.NotFound,
        HttpStatusCode.BadRequest => JiraHttpFailure.BadRequest,
        HttpStatusCode.TooManyRequests => JiraHttpFailure.RateLimited,
        _ => JiraHttpFailure.Server,
    };
}
