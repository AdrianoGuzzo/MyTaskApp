using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MyTaskApp.Remote.Contracts;
using MyTaskApp.Server.Application;
using MyTaskApp.Server.Infrastructure.Persistence;
namespace MyTaskApp.Server.IntegrationTests;

public sealed class TerminalHistoryPersistenceTests(PostgresFixture postgres)
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task ReplayedAndOutOfOrderEventsAreDurableAndIsolatedByAccount()
    {
        var connection = await postgres.CreateDatabaseAsync(Ct);
        var options = new DbContextOptionsBuilder<ServerDbContext>().UseNpgsql(connection).Options;
        var user = Guid.NewGuid();
        var device = Guid.NewGuid();
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var state = new RemoteTerminalSessionReport(Guid.NewGuid(), Guid.NewGuid(), null, RemoteTerminalProcess.Running, null,
            RemoteAgentActivity.Working, "não persistir", false, true, false, false, now, now, "codex");
        var first = new TerminalLifecycleEvent(Guid.NewGuid(), device, state.SessionId, 1, now, state);
        var last = first with { EventId = Guid.NewGuid(), Sequence = 2, State = state with { Process = RemoteTerminalProcess.Exited, ExitCode = 0 } };
        await using (var context = new ServerDbContext(options))
        {
            var store = new EfTerminalHistoryStore(context);
            await store.IngestAsync(user, device, new TerminalLifecyclePage([last]), now, Ct);
            await store.IngestAsync(user, device, new TerminalLifecyclePage([first, last]), now, Ct);
        }
        await using (var context = new ServerDbContext(options))
        {
            var store = new EfTerminalHistoryStore(context);
            var rows = await store.ListAsync(user, now, Ct);
            rows.Should().ContainSingle().Which.Session.Process.Should().Be(RemoteTerminalProcess.Exited);
            var page = await store.EventsAsync(user, state.SessionId, 0, Ct);
            page.Events.Select(item => item.Sequence).Should().Equal(1, 2);
            page.Events.Should().OnlyContain(item => item.State.ActivityMessage == null);
            (await store.EventsAsync(user, state.SessionId, 1, Ct)).Events.Should().ContainSingle();
            (await store.ListAsync(Guid.NewGuid(), now, Ct)).Should().BeEmpty();
            (await store.EventsAsync(Guid.NewGuid(), state.SessionId, 0, Ct)).Events.Should().BeEmpty();
        }
    }
    [Fact]
    public async Task PushDeliveryIsEnqueuedOncePerEventAndOtherDevice()
    {
        var connection = await postgres.CreateDatabaseAsync(Ct);
        await using var context = new ServerDbContext(new DbContextOptionsBuilder<ServerDbContext>().UseNpgsql(connection).Options);
        var user = Guid.NewGuid();
        var executor = Guid.NewGuid();
        context.Add(new RemotePushDestinationRow { UserId = user, DeviceId = executor, Provider = RemotePushProvider.Apns, Address = "local" });
        context.Add(new RemotePushDestinationRow { UserId = user, DeviceId = Guid.NewGuid(), Provider = RemotePushProvider.Apns, Address = "remote" });
        await context.SaveChangesAsync(Ct);
        var now = new DateTimeOffset(2026, 11, 15, 12, 0, 0, TimeSpan.Zero);
        var state = new RemoteTerminalSessionReport(Guid.NewGuid(), Guid.NewGuid(), null, RemoteTerminalProcess.Exited, 0,
            RemoteAgentActivity.WaitingReview, null, false, true, false, false, now, now);
        var item = new TerminalLifecycleEvent(Guid.NewGuid(), executor, state.SessionId, 1, now, state);
        var store = new EfTerminalHistoryStore(context);
        await store.IngestAsync(user, executor, new TerminalLifecyclePage([item]), now, Ct);
        await store.IngestAsync(user, executor, new TerminalLifecyclePage([item]), now, Ct);
        var deliveries = await context.Set<RemotePushDeliveryRow>().ToListAsync(Ct);
        deliveries.Should().ContainSingle().Which.DeviceId.Should().NotBe(executor);
    }
}
