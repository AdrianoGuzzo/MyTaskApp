using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using MyTaskApp.Application.Remote;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Remote.Contracts;
namespace MyTaskApp.Infrastructure.Remote;

internal sealed class TerminalLifecycleRow
{
    public Guid Id { get; set; }
    public string Scope { get; set; } = string.Empty;
    public Guid SessionId { get; set; }
    public long Sequence { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public required string State { get; set; }
    public bool Sent { get; set; }
}

internal sealed class TerminalLifecycleConfiguration : IEntityTypeConfiguration<TerminalLifecycleRow>
{
    public void Configure(EntityTypeBuilder<TerminalLifecycleRow> builder)
    {
        builder.ToTable("TerminalLifecycleOutbox");
        builder.HasKey(row => row.Id);
        builder.HasIndex(row => new { row.Scope, row.SessionId, row.Sequence }).IsUnique();
    }
}

internal sealed class TerminalLifecycleJournal(MyTaskAppDbContext context, TimeProvider time) : ITerminalLifecycleJournal
{
    private async Task<string> ScopeAsync(CancellationToken cancellationToken)
    {
        var state = await context.SyncState.AsNoTracking().SingleOrDefaultAsync(cancellationToken);
        if (state is null) return string.Empty;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{state.ServerBaseUrl}|{state.UserId}|{state.LinkedDeviceId}")));
    }
    public async Task RecoverAsync(IReadOnlyList<Guid> liveSessionIds, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(cancellationToken);
        var rows = await context.Set<TerminalLifecycleRow>().AsNoTracking().Where(row => row.Scope == scope).ToListAsync(cancellationToken);
        var abandoned = rows.GroupBy(row => row.SessionId).Select(group => group.MaxBy(row => row.Sequence)!)
            .Where(row => !liveSessionIds.Contains(row.SessionId))
            .Select(row => JsonSerializer.Deserialize<RemoteTerminalSessionReport>(row.State, RemoteJson.Options)!)
            .Where(state => state.Process == RemoteTerminalProcess.Running)
            .Select(state => state with { Process = RemoteTerminalProcess.Crashed, ExitCode = null, Activity = RemoteAgentActivity.Failed, HasDialog = false }).ToList();
        await CaptureAsync(abandoned, cancellationToken);
        var obsolete = rows.GroupBy(row => row.SessionId).SelectMany(group => group.OrderByDescending(row => row.Sequence).Skip(1))
            .Where(row => row.Sent && row.OccurredAt < time.GetUtcNow().AddDays(-30)).Select(row => row.Id).Take(1000).ToList();
        if (obsolete.Count > 0)
        {
            context.RemoveRange(await context.Set<TerminalLifecycleRow>().Where(row => obsolete.Contains(row.Id)).ToListAsync(cancellationToken));
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    public async Task CaptureAsync(IReadOnlyList<RemoteTerminalSessionReport> states, CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(cancellationToken);
        foreach (var state in states)
        {
            var previous = await context.Set<TerminalLifecycleRow>().Where(row => row.Scope == scope && row.SessionId == state.SessionId)
                .OrderByDescending(row => row.Sequence).FirstOrDefaultAsync(cancellationToken);
            var safe = state with { ActivityMessage = null, UpdatedAt = state.StartedAt };
            var json = JsonSerializer.Serialize(safe, RemoteJson.Options);
            if (previous is not null)
            {
                var before = JsonSerializer.Deserialize<RemoteTerminalSessionReport>(previous.State, RemoteJson.Options)!;
                if (before.Process == safe.Process && before.ExitCode == safe.ExitCode && before.Activity == safe.Activity && before.HasDialog == safe.HasDialog && before.ProviderId == safe.ProviderId) continue;
            }
            context.Add(new TerminalLifecycleRow { Scope = scope, Id = Guid.CreateVersion7(), SessionId = state.SessionId,
                Sequence = (previous?.Sequence ?? 0) + 1, OccurredAt = time.GetUtcNow(), State = json });
        }
        await context.SaveChangesAsync(cancellationToken);
    }
    public async Task<TerminalLifecyclePage> PendingAsync(CancellationToken cancellationToken)
    {
        var scope = await ScopeAsync(cancellationToken);
        var rows = await context.Set<TerminalLifecycleRow>().AsNoTracking().Where(row => row.Scope == scope && !row.Sent)
            .OrderBy(row => row.Sequence).ThenBy(row => row.Id).Take(100).ToListAsync(cancellationToken);
        return new TerminalLifecyclePage(rows.Select(row => new TerminalLifecycleEvent(row.Id, Guid.Empty, row.SessionId, row.Sequence,
            row.OccurredAt, JsonSerializer.Deserialize<RemoteTerminalSessionReport>(row.State, RemoteJson.Options)!)).ToList());
    }
    public async Task AcknowledgeAsync(IReadOnlyList<Guid> eventIds, CancellationToken cancellationToken)
    {
        var rows = await context.Set<TerminalLifecycleRow>().Where(row => eventIds.Contains(row.Id)).ToListAsync(cancellationToken);
        foreach (var row in rows) row.Sent = true;
        await context.SaveChangesAsync(cancellationToken);
    }
}
