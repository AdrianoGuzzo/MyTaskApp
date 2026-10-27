using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Application.Tests;

/// <summary>
/// O lease segurado enquanto o trabalho dura (ADR-085): renova sozinho, avisa
/// quando outro processo o tomou, solta ou guarda ao terminar, e espera a vez.
/// </summary>
public sealed class ProcessLeaseHoldTests
{
    private static readonly TimeSpan Duration = TimeSpan.FromSeconds(30);

    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    private readonly LeaseTable _table = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WhileHeld_ItRenewsEveryThirdOfTheDuration()
    {
        var desktop = _table.Process("desktop");

        await using var hold = await ProcessLeaseHold.TryTakeAsync(desktop, "sync", Duration, _clock, releaseOnDispose: true, Ct);

        hold.Should().NotBeNull();
        desktop.Acquires.Should().Be(1);

        _clock.Advance(Duration / 3);
        _clock.Advance(Duration / 3);

        desktop.Acquires.Should().Be(3);
        hold!.Lost.IsCancellationRequested.Should().BeFalse();
    }

    [Fact]
    public async Task HeldByAnotherProcess_ItIsNotTaken()
    {
        await ProcessLeaseHold.TryTakeAsync(_table.Process("agent"), "sync", Duration, _clock, releaseOnDispose: false, Ct);

        var hold = await ProcessLeaseHold.TryTakeAsync(_table.Process("desktop"), "sync", Duration, _clock, releaseOnDispose: true, Ct);

        hold.Should().BeNull();
    }

    [Fact]
    public async Task WhenAnotherProcessTookIt_LostCancels()
    {
        await using var hold = await ProcessLeaseHold.TryTakeAsync(_table.Process("desktop"), "sync", Duration, _clock, releaseOnDispose: true, Ct);

        _table.Owners["sync"] = "agent";
        _clock.Advance(Duration / 3);

        hold!.Lost.IsCancellationRequested.Should().BeTrue();
    }

    [Fact]
    public async Task ARenewalThatFails_IsNotALoss()
    {
        var desktop = _table.Process("desktop");
        await using var hold = await ProcessLeaseHold.TryTakeAsync(desktop, "sync", Duration, _clock, releaseOnDispose: true, Ct);

        desktop.Failing = true;
        _clock.Advance(Duration / 3);

        hold!.Lost.IsCancellationRequested.Should().BeFalse("o banco ocupado não quer dizer que outro tomou");
    }

    [Fact]
    public async Task Disposing_ReleasesOnlyWhenAsked()
    {
        var desktop = _table.Process("desktop");

        await (await ProcessLeaseHold.TryTakeAsync(desktop, "sync-token", Duration, _clock, releaseOnDispose: true, Ct))!.DisposeAsync();
        await (await ProcessLeaseHold.TryTakeAsync(desktop, "sync", Duration, _clock, releaseOnDispose: false, Ct))!.DisposeAsync();

        _table.Owners.Should().NotContainKey("sync-token");
        _table.Owners["sync"].Should().Be("desktop", "quem sincronizava continua com a preferência");
    }

    [Fact]
    public async Task AfterDisposing_ItStopsRenewing()
    {
        var desktop = _table.Process("desktop");
        await (await ProcessLeaseHold.TryTakeAsync(desktop, "sync", Duration, _clock, releaseOnDispose: false, Ct))!.DisposeAsync();

        _clock.Advance(Duration);

        desktop.Acquires.Should().Be(1);
    }

    [Fact]
    public async Task Taking_WaitsForTheOtherProcessToRelease()
    {
        var agent = _table.Process("agent");
        await agent.TryAcquireAsync("sync-token", Duration, Ct);

        var releasing = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(300), Ct);
            await agent.ReleaseAsync("sync-token", Ct);
        }, Ct);

        await using var hold = await ProcessLeaseHold.TakeAsync(
            _table.Process("desktop"), "sync-token", Duration, TimeSpan.FromSeconds(10), TimeProvider.System, Ct);

        await releasing;
        _table.Owners["sync-token"].Should().Be("desktop");
    }

    [Fact]
    public async Task Taking_GivesUpAfterTheWait()
    {
        await _table.Process("agent").TryAcquireAsync("sync-token", Duration, Ct);

        var take = () => ProcessLeaseHold.TakeAsync(
            _table.Process("desktop"), "sync-token", Duration, TimeSpan.FromMilliseconds(300), TimeProvider.System, Ct);

        await take.Should().ThrowAsync<TimeoutException>();
    }

    /// <summary>A tabela dos leases, sem validade: só quem é o dono de cada nome.</summary>
    private sealed class LeaseTable
    {
        private readonly Lock _gate = new();

        public Dictionary<string, string> Owners { get; } = [];

        public FakeProcess Process(string name) => new(this, name);

        public bool TryAcquire(string lease, string process)
        {
            lock (_gate)
            {
                if (Owners.TryGetValue(lease, out var owner) && owner != process)
                {
                    return false;
                }

                Owners[lease] = process;
                return true;
            }
        }

        public void Release(string lease, string process)
        {
            lock (_gate)
            {
                if (Owners.TryGetValue(lease, out var owner) && owner == process)
                {
                    Owners.Remove(lease);
                }
            }
        }
    }

    private sealed class FakeProcess(LeaseTable table, string name) : IProcessLeases
    {
        public int Acquires { get; private set; }

        public bool Failing { get; set; }

        public Task<bool> TryAcquireAsync(string lease, TimeSpan duration, CancellationToken cancellationToken)
        {
            if (Failing)
            {
                throw new InvalidOperationException("database is locked");
            }

            Acquires++;
            return Task.FromResult(table.TryAcquire(lease, name));
        }

        public Task ReleaseAsync(string lease, CancellationToken cancellationToken)
        {
            table.Release(lease, name);
            return Task.CompletedTask;
        }
    }
}
