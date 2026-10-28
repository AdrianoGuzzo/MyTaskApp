using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Remote;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>
/// O ritmo das telas de controle (ADR-094): com o hub, o aviso acorda a leitura
/// na hora e o relógio só cobre o aviso perdido; sem o hub, o intervalo da tela.
/// </summary>
public sealed class RemotePaceTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 28, 12, 0, 0, TimeSpan.Zero));

    private readonly FakeRemoteWatch _watch = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task WithoutTheHub_ItWaitsTheScreenInterval()
    {
        await using var pace = new RemotePace(_watch, _time);
        await pace.StartAsync(Ct);

        var wait = pace.WaitAsync(Interval, Ct);
        _time.Advance(Interval - TimeSpan.FromMilliseconds(1));
        wait.IsCompleted.Should().BeFalse();

        _time.Advance(TimeSpan.FromMilliseconds(1));
        await wait.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task WithTheHub_ANoticeWakesAtOnce_AndTheClockOnlyCoversTheLostOne()
    {
        _watch.IsConnected = true;
        await using var pace = new RemotePace(_watch, _time);
        await pace.StartAsync(Ct);

        var wait = pace.WaitAsync(Interval, Ct);
        _time.Advance(Interval);
        wait.IsCompleted.Should().BeFalse("com o hub, o relógio vai devagar");

        _watch.Notify(RemoteNoticeKind.Events, Guid.CreateVersion7());
        await wait.WaitAsync(TimeSpan.FromSeconds(5), Ct);

        var next = pace.WaitAsync(Interval, Ct);
        _time.Advance(RemotePace.LiveFallback);
        await next.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task ANoticeForSomethingElse_DoesNotWake_ButTheReconnectionAlwaysDoes()
    {
        _watch.IsConnected = true;
        var mine = Guid.CreateVersion7();
        await using var pace = new RemotePace(_watch, _time, notice => notice.Id == mine);
        await pace.StartAsync(Ct);

        var wait = pace.WaitAsync(Interval, Ct);
        _watch.Notify(RemoteNoticeKind.Events, Guid.CreateVersion7());
        await Task.Delay(20, Ct);
        wait.IsCompleted.Should().BeFalse();

        _watch.Notify(RemoteNoticeKind.Reconnected, Guid.Empty);
        await wait.WaitAsync(TimeSpan.FromSeconds(5), Ct);
    }

    [Fact]
    public async Task TheNoticesAreTakenOnce_WithTheirKinds()
    {
        await using var pace = new RemotePace(_watch, _time);
        await pace.StartAsync(Ct);

        _watch.Notify(RemoteNoticeKind.Session, Guid.CreateVersion7());
        _watch.Notify(RemoteNoticeKind.Approval, Guid.CreateVersion7());

        pace.TakeNotices().Should().BeEquivalentTo([RemoteNoticeKind.Session, RemoteNoticeKind.Approval]);
        pace.TakeNotices().Should().BeEmpty();
    }

    [Fact]
    public async Task EachScreenHoldsTheConnection_AndTheLastOneToCloseReleasesIt()
    {
        var first = new RemotePace(_watch, _time);
        var second = new RemotePace(_watch, _time);

        await first.StartAsync(Ct);
        await first.StartAsync(Ct);
        await second.StartAsync(Ct);
        _watch.Watchers.Should().Be(2);

        await first.DisposeAsync();
        await first.DisposeAsync();
        _watch.Watchers.Should().Be(1);

        await second.DisposeAsync();
        _watch.Watchers.Should().Be(0);

        _watch.Notify(RemoteNoticeKind.Session, Guid.CreateVersion7());
        first.TakeNotices().Should().BeEmpty("fechada, a tela não ouve mais");
    }
}

/// <summary>O hub de mentira: o teste diz se está ligado e manda os avisos.</summary>
internal sealed class FakeRemoteWatch : IRemoteWatch
{
    public event Action<RemoteNotice>? Noticed;

    public bool IsConnected { get; set; }

    public int Watchers { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Watchers++;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Watchers--;
        return Task.CompletedTask;
    }

    public void Notify(RemoteNoticeKind kind, Guid id) => Noticed?.Invoke(new RemoteNotice(kind, id));
}
