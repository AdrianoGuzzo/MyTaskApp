using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.LocalPeers;
using MyTaskApp.Application.Sync;
using NSubstitute;

namespace MyTaskApp.Application.Tests;

/// <summary>
/// A ponte entre os avisos deste processo e os do outro (ADR-085, T-11): o
/// que acontece aqui atravessa, o que chega de lá é repassado e não volta.
/// </summary>
public sealed class LocalPeerBridgeTests : IAsyncDisposable
{
    private readonly FakeChannel _channel = new();
    private readonly DataChangeNotifier _notifier = new();
    private readonly SyncTrigger _trigger = new();
    private readonly SyncScheduler _scheduler;
    private readonly LocalPeerBridge _bridge;

    public LocalPeerBridgeTests()
    {
        _scheduler = new SyncScheduler(
            Substitute.For<ISyncCycleRunner>(),
            _trigger,
            TimeProvider.System,
            NullLogger<SyncScheduler>.Instance);
        _bridge = new LocalPeerBridge(_channel, _notifier, _trigger, _scheduler, NullLogger<LocalPeerBridge>.Instance);
        _bridge.Start();
    }

    public async ValueTask DisposeAsync()
    {
        await _bridge.DisposeAsync();
        await _scheduler.DisposeAsync();
    }

    [Fact]
    public void WhatHappensHere_GoesToTheOtherProcess()
    {
        _notifier.NotifyChanged(DataArea.Tasks);
        _trigger.Signal();
        _scheduler.RequestNow();

        _channel.Sent.Should().Equal(
            new LocalPeerMessage(LocalPeerMessageKinds.DataChanged, DataArea.Tasks),
            new LocalPeerMessage(LocalPeerMessageKinds.SyncWanted),
            new LocalPeerMessage(LocalPeerMessageKinds.SyncNow),
            new LocalPeerMessage(LocalPeerMessageKinds.SyncWanted));
    }

    [Fact]
    public void AChangeFromTheOtherProcess_ReloadsHere_AndDoesNotEchoBack()
    {
        DataArea? reloaded = null;
        _notifier.Changed += areas => reloaded = areas;

        _channel.Receive(new LocalPeerMessage(LocalPeerMessageKinds.DataChanged, DataArea.Time | DataArea.Tags));

        reloaded.Should().Be(DataArea.Time | DataArea.Tags);
        _channel.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task ASyncWishFromTheOtherProcess_WakesTheLoopHere_AndDoesNotEchoBack()
    {
        _channel.Receive(new LocalPeerMessage(LocalPeerMessageKinds.SyncWanted));

        (await WaitAsync(_trigger)).Should().BeTrue();
        _channel.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task SyncNowFromTheOtherProcess_RunsHere_AndDoesNotEchoBack()
    {
        var requested = false;
        _scheduler.NowRequested += () => requested = true;

        _channel.Receive(new LocalPeerMessage(LocalPeerMessageKinds.SyncNow));

        requested.Should().BeTrue();
        (await WaitAsync(_trigger)).Should().BeTrue();
        _channel.Sent.Should().BeEmpty();
    }

    [Fact]
    public void WithoutTheOtherProcess_NothingIsSent()
    {
        _channel.IsConnected = false;

        _notifier.NotifyChanged(DataArea.Tasks);
        _trigger.Signal();

        _channel.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task AfterDisposing_TheBridgeNoLongerForwards()
    {
        await _bridge.DisposeAsync();

        _notifier.NotifyChanged(DataArea.Tasks);

        _channel.Sent.Should().BeEmpty();
        _channel.Disposed.Should().BeTrue();
    }

    private static async Task<bool> WaitAsync(SyncTrigger trigger)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            await trigger.WaitAsync(timeout.Token);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private sealed class FakeChannel : ILocalPeerChannel
    {
        public List<LocalPeerMessage> Sent { get; } = [];

        public bool IsConnected { get; set; } = true;

        public bool Disposed { get; private set; }

        public event Action<LocalPeerMessage>? Received;

        public void Receive(LocalPeerMessage message) => Received?.Invoke(message);

        public void Start()
        {
        }

        public Task SendAsync(LocalPeerMessage message, CancellationToken cancellationToken)
        {
            Sent.Add(message);
            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
