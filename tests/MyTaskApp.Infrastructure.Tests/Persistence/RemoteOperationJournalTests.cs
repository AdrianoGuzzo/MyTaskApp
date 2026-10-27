using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Remote;
using MyTaskApp.Infrastructure.Persistence.Repositories;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>O diário do agente no SQLite de verdade, pela migration (ADR-086).</summary>
public sealed class RemoteOperationJournalTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task TheSameOperationIdGoesInOnlyOnce()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var entry = Entry();

        await using (var context = db.CreateContext())
        {
            Assert.True(await new RemoteOperationJournal(context, _time).TryAddAsync(entry, Ct));
        }

        await using (var context = db.CreateContext())
        {
            var journal = new RemoteOperationJournal(context, _time);

            Assert.False(await journal.TryAddAsync(entry with { Kind = RemoteOperationKinds.WorktreeRemove }, Ct));
            Assert.Equal(entry, await journal.FindAsync(entry.OperationId, Ct));
        }
    }

    [Fact]
    public async Task EventsAreNumberedInOrderAndTheStepIsRemembered()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var entry = Entry();
        await using var context = db.CreateContext();
        var journal = new RemoteOperationJournal(context, _time);
        await journal.TryAddAsync(entry, Ct);

        var first = await journal.AppendEventAsync(Draft(entry, RemoteEventKind.StepStarted, step: "worktree"), Ct);
        var second = await journal.AppendEventAsync(
            Draft(entry, RemoteEventKind.Output, "pronto", data: JsonSerializer.SerializeToElement(new { exitCode = 0 })),
            Ct);

        Assert.Equal(1, first.Seq);
        Assert.Equal(2, second.Seq);

        var stored = (await journal.FindAsync(entry.OperationId, Ct))!;
        Assert.Equal(3, stored.NextSeq);
        Assert.Equal("worktree", stored.Step);
        Assert.True(stored.HasUnreported);

        var pending = await journal.PendingEventsAsync(entry.OperationId, 10, Ct);
        Assert.Equal([1L, 2L], pending.Select(item => item.Seq));
        Assert.Equal("pronto", pending[1].Text);
        Assert.Equal(0, pending[1].Data!.Value.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task TheAckDeletesWhatTheServerHasAndNeverGoesBack()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var entry = Entry();
        await using var context = db.CreateContext();
        var journal = new RemoteOperationJournal(context, _time);
        await journal.TryAddAsync(entry, Ct);

        for (var line = 0; line < 3; line++)
        {
            await journal.AppendEventAsync(Draft(entry, RemoteEventKind.Output, $"linha {line}"), Ct);
        }

        await journal.AcknowledgeAsync(entry.OperationId, 2, Ct);
        await journal.AcknowledgeAsync(entry.OperationId, 1, Ct);

        Assert.Equal([3L], (await journal.PendingEventsAsync(entry.OperationId, 10, Ct)).Select(item => item.Seq));
        Assert.Equal(2, (await journal.FindAsync(entry.OperationId, Ct))!.AckedSeq);

        // Quem desiste de enviar confirma tudo, sem passar do último gerado.
        await journal.AcknowledgeAsync(entry.OperationId, long.MaxValue, Ct);

        Assert.Empty(await journal.PendingEventsAsync(entry.OperationId, 10, Ct));
        Assert.Equal(3, (await journal.FindAsync(entry.OperationId, Ct))!.AckedSeq);
    }

    [Fact]
    public async Task StatusAndReportedStatusAreWrittenSeparately()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var entry = Entry();
        await using var context = db.CreateContext();
        var journal = new RemoteOperationJournal(context, _time);
        await journal.TryAddAsync(entry, Ct);

        Assert.Single(await journal.ListUnreportedAsync(Ct));

        await journal.MarkReportedAsync(entry.OperationId, RemoteOperationStatus.Accepted, Ct);
        Assert.Empty(await journal.ListUnreportedAsync(Ct));

        await journal.AppendEventAsync(Draft(entry, RemoteEventKind.Output, "x"), Ct);
        await journal.SetStatusAsync(entry.OperationId, RemoteOperationStatus.Failed, RemoteErrorCodes.ExecutionFailed, "Falhou.", Ct);

        var stored = (await journal.FindAsync(entry.OperationId, Ct))!;
        Assert.Equal(RemoteOperationStatus.Failed, stored.Status);
        Assert.Equal(RemoteOperationStatus.Accepted, stored.ReportedStatus);
        Assert.Equal(2, stored.NextSeq);
        Assert.Equal("Falhou.", stored.ResultSummary);
        Assert.Single(await journal.ListUnreportedAsync(Ct));
        Assert.Empty(await journal.ListUnfinishedAsync(Ct));
    }

    [Fact]
    public async Task CountsPerControllerInTheWindowAndPrunesOnlyWhatIsDoneAndTold()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await using var context = db.CreateContext();
        var journal = new RemoteOperationJournal(context, _time);
        var phone = Guid.CreateVersion7();
        var old = Entry(phone, _time.GetUtcNow().AddDays(-40));
        var told = old with { OperationId = Guid.CreateVersion7(), Status = RemoteOperationStatus.Succeeded, ReportedStatus = RemoteOperationStatus.Succeeded };
        var untold = old with { OperationId = Guid.CreateVersion7(), Status = RemoteOperationStatus.Succeeded };
        var recent = Entry(phone, _time.GetUtcNow());

        foreach (var entry in new[] { old, told, untold, recent })
        {
            await journal.TryAddAsync(entry, Ct);
        }

        await journal.AppendEventAsync(Draft(told, RemoteEventKind.Output, "x"), Ct);

        Assert.Equal(1, await journal.CountFromControllerSinceAsync(phone, _time.GetUtcNow().AddHours(-1), Ct));
        Assert.Equal(0, await journal.CountFromControllerSinceAsync(Guid.CreateVersion7(), _time.GetUtcNow().AddHours(-1), Ct));

        Assert.Equal(1, await journal.PruneAsync(_time.GetUtcNow() - RemoteLimits.ReplayWindow, Ct));

        Assert.Null(await journal.FindAsync(told.OperationId, Ct));
        Assert.Empty(await journal.PendingEventsAsync(told.OperationId, 10, Ct));
        Assert.NotNull(await journal.FindAsync(untold.OperationId, Ct));
        Assert.NotNull(await journal.FindAsync(old.OperationId, Ct));
    }

    private RemoteJournalEntry Entry(Guid? controller = null, DateTimeOffset? at = null)
    {
        var when = at ?? _time.GetUtcNow();
        return new RemoteJournalEntry(
            Guid.CreateVersion7(),
            controller ?? Guid.CreateVersion7(),
            "Pixel 8",
            RemoteOperationKinds.EnvironmentInspect,
            null,
            "a.b.c",
            RemoteOperationStatus.Accepted,
            null,
            null,
            null,
            null,
            1,
            0,
            when,
            when);
    }

    private RemoteOperationEvent Draft(RemoteJournalEntry entry, RemoteEventKind kind, string? text = null, string? step = null, JsonElement? data = null) =>
        new(entry.OperationId, 0, _time.GetUtcNow(), kind, step, null, text, data);
}
