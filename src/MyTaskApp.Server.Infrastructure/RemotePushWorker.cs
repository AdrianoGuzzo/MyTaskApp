using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Application;
using MyTaskApp.Server.Infrastructure.Persistence;

namespace MyTaskApp.Server.Infrastructure;

internal sealed class RemotePushWorker(IServiceScopeFactory scopes, IOptions<RemotePushOptions> settings, TimeProvider time, ILogger<RemotePushWorker> logger, IFcmAuthorization? fcm = null) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var client = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(10) };
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await DeliverAsync(client, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception) { logger.LogWarning("RemotePushDeliveryFailed {Error}", exception.GetType().Name); }
            await Task.Delay(TimeSpan.FromSeconds(2), time, stoppingToken);
        }
    }

    internal async Task DeliverAsync(HttpClient client, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        var context = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
        var now = time.GetUtcNow();
        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        // SKIP LOCKED distribui a fila entre instâncias e evita dois envios simultâneos do mesmo aviso.
        var deliveries = await context.Set<RemotePushDeliveryRow>().FromSqlInterpolated($"SELECT * FROM \"RemotePushDeliveries\" WHERE \"DueAt\" <= {now} ORDER BY \"DueAt\" LIMIT 10 FOR UPDATE SKIP LOCKED").ToListAsync(cancellationToken);
        foreach (var delivery in deliveries)
        {
            var destination = await context.Set<RemotePushDestinationRow>().FindAsync([delivery.UserId, delivery.DeviceId], cancellationToken);
            var device = await scope.ServiceProvider.GetRequiredService<IDeviceRegistry>().FindAsync(delivery.UserId, delivery.DeviceId, cancellationToken);
            if (destination is null || device is null || device.IsRevoked || delivery.ExpiresAt <= now)
            { context.Remove(delivery); continue; }
            if (delivery.Kind is "waiting" or "review")
            {
                var latest = await context.Set<TerminalHistoryRow>().AsNoTracking().Where(row => row.UserId == delivery.UserId && row.SessionId == delivery.SessionId)
                    .OrderByDescending(row => row.Sequence).FirstOrDefaultAsync(cancellationToken);
                if (latest is not null && latest.EventId != delivery.EventId) { context.Remove(delivery); continue; }
            }
            try
            {
                using var request = await RequestAsync(destination, delivery, now, cancellationToken);
                using var response = await client.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode) context.Remove(delivery);
                else if (await InvalidDestinationAsync(response, destination.Provider, cancellationToken))
                { context.Remove(destination); context.Remove(delivery); }
                else Retry(delivery, now);
            }
            catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            { Retry(delivery, now); logger.LogWarning("RemotePushSendFailed {Error}", exception.GetType().Name); }
        }
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task<bool> InvalidDestinationAsync(HttpResponseMessage response, RemotePushProvider provider, CancellationToken cancellationToken)
    {
        if (provider == RemotePushProvider.Fcm)
        {
            if (response.StatusCode != HttpStatusCode.NotFound && response.StatusCode != HttpStatusCode.BadRequest) return false;
            try
            {
                using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
                return json.RootElement.TryGetProperty("error", out var error) && error.TryGetProperty("details", out var details) &&
                    details.ValueKind == JsonValueKind.Array && details.EnumerateArray().Any(detail => detail.TryGetProperty("errorCode", out var code) && code.GetString() == "UNREGISTERED");
            }
            catch (JsonException) { return false; }
        }
        if (response.StatusCode is HttpStatusCode.Gone or HttpStatusCode.NotFound) return true;
        if (provider != RemotePushProvider.Apns || response.StatusCode != HttpStatusCode.BadRequest) return false;
        try
        {
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken));
            return json.RootElement.TryGetProperty("reason", out var reason) && reason.GetString() is "BadDeviceToken" or "DeviceTokenNotForTopic" or "Unregistered";
        }
        catch (JsonException) { return false; }
    }

    private static void Retry(RemotePushDeliveryRow delivery, DateTimeOffset now)
    {
        delivery.Attempts++;
        delivery.DueAt = now.AddSeconds(Math.Min(3600, Math.Pow(2, Math.Min(delivery.Attempts, 12))));
    }

    private async Task<HttpRequestMessage> RequestAsync(RemotePushDestinationRow destination, RemotePushDeliveryRow delivery, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var options = settings.Value;
        var text = delivery.Kind switch { "failed" => "Um terminal parou com erro.", "exited" => "Um terminal foi encerrado.", "review" => "Uma resposta está pronta para revisão.", _ => "Um terminal aguarda você." };
        if (destination.Provider == RemotePushProvider.Fcm)
        {
            if (fcm is null) throw new InvalidOperationException("Autenticação Firebase não configurada.");
            var message = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(options.FcmProjectId)}/messages:send")
            {
                Content = JsonContent.Create(new
                {
                    message = new
                    {
                        token = destination.Address,
                        notification = new { title = "MyTaskApp", body = text },
                        data = new { eventId = delivery.EventId.ToString("D"), taskId = delivery.TaskId.ToString("D"), text },
                        android = new { priority = "HIGH", ttl = $"{Math.Clamp((int)(delivery.ExpiresAt - now).TotalSeconds, 0, 86400)}s", notification = new { channel_id = "remote", tag = delivery.EventId.ToString("N"), icon = "notification_icon" } }
                    }
                })
            };
            message.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await fcm.GetAccessTokenAsync(cancellationToken));
            return message;
        }
        if (destination.Provider != RemotePushProvider.Apns) throw new InvalidOperationException("Provedor de notificações removido.");
        var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.ApnsBaseAddress), "3/device/" + destination.Address))
        {
            Version = HttpVersion.Version20,
            VersionPolicy = HttpVersionPolicy.RequestVersionExact,
            Content = JsonContent.Create(new { aps = new { alert = new { title = "MyTaskApp", body = text }, sound = "default" }, eventId = delivery.EventId, taskId = delivery.TaskId })
        };
        request.Headers.Add("authorization", "bearer " + Token(options, now));
        request.Headers.Add("apns-topic", options.ApnsTopic);
        request.Headers.Add("apns-push-type", "alert");
        request.Headers.Add("apns-priority", "10");
        request.Headers.Add("apns-id", delivery.EventId.ToString());
        request.Headers.Add("apns-collapse-id", delivery.EventId.ToString());
        return request;
    }

    private static string Token(RemotePushOptions options, DateTimeOffset now)
    {
        static string Encode(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var header = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "ES256", kid = options.ApnsKeyId }));
        var claims = Encode(JsonSerializer.SerializeToUtf8Bytes(new { iss = options.ApnsTeamId, iat = now.ToUnixTimeSeconds() }));
        var data = header + "." + claims;
        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(options.ApnsKeyFile));
        return data + "." + Encode(key.SignData(Encoding.ASCII.GetBytes(data), HashAlgorithmName.SHA256));
    }
}
