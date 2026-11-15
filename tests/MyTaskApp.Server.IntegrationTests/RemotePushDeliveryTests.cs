using System.Net;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Application;
using MyTaskApp.Server.Infrastructure;
using MyTaskApp.Server.Infrastructure.Persistence;
namespace MyTaskApp.Server.IntegrationTests;

public sealed class RemotePushDeliveryTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Theory]
    [InlineData(HttpStatusCode.OK, false, false)]
    [InlineData(HttpStatusCode.NotFound, false, true)]
    [InlineData(HttpStatusCode.NotFound, false, false)]
    [InlineData(HttpStatusCode.ServiceUnavailable, false, false)]
    [InlineData(HttpStatusCode.OK, true, false)]
    public async Task FcmDeliveryAuthenticatesRetriesAndChecksRevocation(HttpStatusCode status, bool revoked, bool unregistered)
    {
        var connection = await postgres.CreateDatabaseAsync(Ct);
        var clock = new FakeTimeProvider();
        var user = Guid.NewGuid();
        var device = new DeviceRecord { UserId = user, DeviceId = Guid.NewGuid(), Nickname = "Celular", RevokedAt = revoked ? clock.GetUtcNow() : null };
        await using var services = new ServiceCollection().AddDbContext<ServerDbContext>(builder => builder.UseNpgsql(connection))
            .AddSingleton<IDeviceRegistry>(new Registry(device)).BuildServiceProvider();
        var eventId = Guid.NewGuid();
        await using (var scope = services.CreateAsyncScope())
        {
            var context = scope.ServiceProvider.GetRequiredService<ServerDbContext>();
            context.Add(new RemotePushDestinationRow { UserId = user, DeviceId = device.DeviceId, Provider = RemotePushProvider.Fcm,
                Address = "fake-registration-id" });
            context.Add(new RemotePushDeliveryRow { UserId = user, DeviceId = device.DeviceId, EventId = eventId, TaskId = Guid.NewGuid(), SessionId = Guid.NewGuid(), Kind = "exited",
                DueAt = clock.GetUtcNow(), ExpiresAt = clock.GetUtcNow().AddHours(24) });
            await context.SaveChangesAsync(Ct);
        }
        var handler = new Handler(status, unregistered);
        using var client = new HttpClient(handler);
        using var worker = new RemotePushWorker(services.GetRequiredService<IServiceScopeFactory>(), Options.Create(new RemotePushOptions { FcmProjectId = "example-project" }), clock, NullLogger<RemotePushWorker>.Instance, new Authorization());
        await worker.DeliverAsync(client, Ct);
        handler.Count.Should().Be(revoked ? 0 : 1);
        if (!revoked)
        {
            handler.Authorization.Should().Be("Bearer fake-oauth-access");
            using var json = JsonDocument.Parse(handler.Payload!);
            var message = json.RootElement.GetProperty("message");
            message.GetProperty("data").GetProperty("eventId").GetGuid().Should().Be(eventId);
            message.GetProperty("token").GetString().Should().Be("fake-registration-id");
            message.GetProperty("notification").GetProperty("body").GetString().Should().Be("Um terminal foi encerrado.");
        }
        await using var check = services.CreateAsyncScope();
        var db = check.ServiceProvider.GetRequiredService<ServerDbContext>();
        if ((status == HttpStatusCode.ServiceUnavailable || (status == HttpStatusCode.NotFound && !unregistered)) && !revoked)
        {
            var pending = await db.Set<RemotePushDeliveryRow>().SingleAsync(Ct);
            pending.Attempts.Should().Be(1);
            pending.DueAt.Should().BeAfter(clock.GetUtcNow());
        }
        else (await db.Set<RemotePushDeliveryRow>().CountAsync(Ct)).Should().Be(0);
        if (unregistered) (await db.Set<RemotePushDestinationRow>().CountAsync(Ct)).Should().Be(0);
    }
    private sealed class Handler(HttpStatusCode status, bool unregistered) : HttpMessageHandler
    {
        public int Count { get; private set; }
        public byte[]? Payload { get; private set; }
        public string? Authorization { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            Payload = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
            Authorization = request.Headers.Authorization?.ToString();
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(unregistered ? "{\"error\":{\"details\":[{\"errorCode\":\"UNREGISTERED\"}]}}" : "{}")
            };
        }
    }
    private sealed class Authorization : IFcmAuthorization
    {
        public Task<string> GetAccessTokenAsync(CancellationToken cancellationToken) => Task.FromResult("fake-oauth-access");
    }
    private sealed class Registry(DeviceRecord device) : IDeviceRegistry
    {
        public Task<DeviceRecord?> FindAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken) => Task.FromResult<DeviceRecord?>(device);
        public Task<IReadOnlyList<DeviceRecord>> ListAsync(Guid userId, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<DeviceRecord>>([device]);
        public Task SaveAsync(DeviceRecord item, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
