using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Sync;

namespace MyTaskApp.Infrastructure.Sync.Http;

/// <summary>
/// "Testar conexão" (ADR-078): o <c>/health</c> e o discovery do OAuth do
/// servidor, sem login e sem token. Diz o que deu errado com palavras de quem
/// configura — DNS, recusa, certificado, tempo, redirecionamento — e nunca
/// troca o endereço configurado pelo que o servidor anuncia.
/// </summary>
internal sealed class SyncServerProbe(SyncHttp http, ILogger<SyncServerProbe> logger) : ISyncServerProbe
{
    public const string HealthPath = "health";

    public const string DiscoveryPath = ".well-known/openid-configuration";

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(10);

    public async Task<SyncServerProbeResult> TestAsync(Uri server, CancellationToken cancellationToken)
    {
        var result = await RunAsync(server, cancellationToken);
        logger.LogInformation("SyncServerProbe {Kind}", result.Kind);
        return result;
    }

    private async Task<SyncServerProbeResult> RunAsync(Uri server, CancellationToken cancellationToken)
    {
        try
        {
            using (var health = await GetAsync(new Uri(server, HealthPath), cancellationToken))
            {
                if (Failure(health.StatusCode) is { } failure)
                {
                    return failure;
                }

                if (!string.Equals(Read(await health.Content.ReadAsStringAsync(cancellationToken), "status"), "ok", StringComparison.OrdinalIgnoreCase))
                {
                    return NotMyTaskApp;
                }
            }

            using var discovery = await GetAsync(new Uri(server, DiscoveryPath), cancellationToken);

            if (Failure(discovery.StatusCode) is { } discoveryFailure)
            {
                return discoveryFailure;
            }

            if (!Uri.TryCreate(Read(await discovery.Content.ReadAsStringAsync(cancellationToken), "authorization_endpoint"), UriKind.Absolute, out var authorize))
            {
                return NotMyTaskApp;
            }

            // Só um aviso: o login abre no endereço configurado, nunca no anunciado.
            return string.Equals(authorize.Host, server.Host, StringComparison.OrdinalIgnoreCase) && authorize.Port == server.Port
                ? new(SyncServerProbeKind.Ok, "Conectado. O servidor respondeu e aceita o login do MyTaskApp.")
                : new(
                    SyncServerProbeKind.IssuerMismatch,
                    $"O servidor respondeu, mas anuncia o login em outro endereço ({authorize.Authority}). "
                    + "Confira se este é o endereço público do servidor (Sync__Issuer).");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new(SyncServerProbeKind.Timeout, "O servidor demorou demais para responder.");
        }
        catch (HttpRequestException exception)
        {
            return exception.HttpRequestError switch
            {
                HttpRequestError.NameResolutionError =>
                    new(SyncServerProbeKind.Dns, "Endereço não encontrado. Confira o nome do servidor e a conexão com a internet."),
                HttpRequestError.ConnectionError =>
                    new(SyncServerProbeKind.Refused, "Não foi possível conectar. O servidor pode estar desligado ou fora de alcance."),
                HttpRequestError.SecureConnectionError =>
                    new(SyncServerProbeKind.Tls, "A conexão segura falhou: o certificado do servidor não é válido para este endereço ou não é confiável."),
                _ => new(SyncServerProbeKind.Unexpected, "Falha de rede ao falar com o servidor."),
            };
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException)
        {
            return NotMyTaskApp;
        }
    }

    private static SyncServerProbeResult NotMyTaskApp { get; } =
        new(SyncServerProbeKind.NotMyTaskApp, "Este endereço respondeu, mas não parece um servidor de sincronização do MyTaskApp.");

    private async Task<HttpResponseMessage> GetAsync(Uri url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        // Sem Authorization: o teste vem antes de qualquer login neste servidor.
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        return await http.Client.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
    }

    private static SyncServerProbeResult? Failure(HttpStatusCode status) => (int)status switch
    {
        >= 200 and < 300 => null,
        >= 300 and < 400 => new(SyncServerProbeKind.Redirect, SyncHttp.RedirectMessage),
        404 => NotMyTaskApp,
        >= 500 => new(SyncServerProbeKind.ServerError, $"O servidor respondeu com erro ({(int)status}). Tente mais tarde."),
        _ => new(SyncServerProbeKind.Unexpected, $"Resposta inesperada do servidor ({(int)status})."),
    };

    private static string? Read(string json, string property)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(property, out var value)
            && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }
}
