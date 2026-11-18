using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Infrastructure.Remote;
using MyTaskApp.Infrastructure.Sync.Persistence;
using MyTaskApp.Remote.Contracts;
namespace MyTaskApp.Infrastructure.Tests.Persistence;

public sealed class TerminalLifecycleJournalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    [Fact]
    public async Task MissingHostAfterRestartBecomesFailureWithUnknownExitCode()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var time = new FakeTimeProvider();
        await using var context = db.CreateContext();
        var journal = new TerminalLifecycleJournal(context, time);
        var state = new RemoteTerminalSessionReport(Guid.NewGuid(), Guid.NewGuid(), null, RemoteTerminalProcess.Running, null,
            RemoteAgentActivity.Working, null, false, true, false, false, time.GetUtcNow(), time.GetUtcNow());
        await journal.CaptureAsync([state], Ct);
        await journal.RecoverAsync([], Ct);
        var events = (await journal.PendingAsync(Ct)).Events;
        events.Should().HaveCount(2);
        events[1].State.Process.Should().Be(RemoteTerminalProcess.Crashed);
        events[1].State.ExitCode.Should().BeNull();
        await journal.RecoverAsync([], Ct);
        (await journal.PendingAsync(Ct)).Events.Should().HaveCount(2);
    }

    [Fact]
    public async Task SwitchingAccountsDoesNotPublishThePreviousAccountsPendingEvents()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var time = new FakeTimeProvider();
        await using var context = db.CreateContext();
        var link = new SyncStateRow { UserId = Guid.NewGuid(), LinkedDeviceId = Guid.NewGuid(), ServerBaseUrl = "https://sync.example.invalid" };
        context.Add(link);
        await context.SaveChangesAsync(Ct);
        var journal = new TerminalLifecycleJournal(context, time);
        var state = new RemoteTerminalSessionReport(Guid.NewGuid(), Guid.NewGuid(), null, RemoteTerminalProcess.Running, null,
            RemoteAgentActivity.Working, null, false, true, false, false, time.GetUtcNow(), time.GetUtcNow());
        await journal.CaptureAsync([state], Ct);
        (await journal.PendingAsync(Ct)).Events.Should().ContainSingle();
        link.UserId = Guid.NewGuid();
        await context.SaveChangesAsync(Ct);
        (await journal.PendingAsync(Ct)).Events.Should().BeEmpty();
    }

    [Theory]
    [InlineData("claude-code")]
    [InlineData("codex")]
    public async Task OfflineTransitionsSurviveRestartAndAcknowledgementDoesNotResetSequence(string provider)
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var time = new FakeTimeProvider();
        var state = new RemoteTerminalSessionReport(Guid.NewGuid(), Guid.NewGuid(), null, RemoteTerminalProcess.Running, null,
            RemoteAgentActivity.Working, "conteúdo privado", false, true, false, false, time.GetUtcNow(), time.GetUtcNow(), provider);
        Guid[] ids;
        await using (var context = db.CreateContext())
        {
            var journal = new TerminalLifecycleJournal(context, time);
            await journal.CaptureAsync([state], Ct);
            await journal.CaptureAsync([state with { ActivityMessage = "outro texto", UpdatedAt = time.GetUtcNow().AddMinutes(1) }], Ct);
            await journal.CaptureAsync([state with { Activity = RemoteAgentActivity.WaitingForUser }], Ct);
            await journal.CaptureAsync([state with { Activity = RemoteAgentActivity.WaitingReview }], Ct);
            var pending = await journal.PendingAsync(Ct);
            pending.Events.Select(item => item.Sequence).Should().Equal(1, 2, 3);
            pending.Events.Should().OnlyContain(item => item.State.ActivityMessage == null && item.State.ProviderId == provider);
            ids = pending.Events.Select(item => item.EventId).ToArray();
        }
        await using (var context = db.CreateContext())
        {
            var journal = new TerminalLifecycleJournal(context, time);
            (await journal.PendingAsync(Ct)).Events.Select(item => item.EventId).Should().Equal(ids);
            await journal.AcknowledgeAsync(ids, Ct);
            await journal.CaptureAsync([state with { Process = RemoteTerminalProcess.Exited, ExitCode = 0 }], Ct);
            var ended = (await journal.PendingAsync(Ct)).Events.Should().ContainSingle().Subject;
            ended.Sequence.Should().Be(4);
            ended.State.Process.Should().Be(RemoteTerminalProcess.Exited);
        }
    }
}
