using System.Collections.Concurrent;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Application.Tests.Terminals;

/// <summary>
/// O miolo das vistas do terminal (ADR-101), o que o desktop e o celular usam
/// igual. O resto do comportamento é conferido pela vista do desktop
/// (<c>TerminalSessionViewModelTests</c>), que delega a ele.
/// </summary>
public sealed class TerminalSessionClientTests : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly ConcurrentQueue<Action> _ui = new();

    private readonly List<FakeTerminalChannel> _channels = [];

    private TerminalSessionClient? _client;

    private FakeTerminalChannel Channel => _channels[^1];

    public async ValueTask DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }
    }

    [Fact]
    public async Task APrompt_GoesOnlyWithTheClaudeStopped_AndWithTheTurnFromAfar()
    {
        var client = await ConnectedAsync(TerminalClientKind.Remote);
        var you = Guid.NewGuid();

        Channel.Send(new TerminalStateMessage(FakeTerminalChannel.Status(activity: AgentActivity.WaitingReview) with { YourClientId = you }));
        await WaitUntil(() => client.Status is not null);
        client.CanPrompt.Should().BeFalse("de longe, sem a vez, só olha");

        Channel.Send(new TerminalStateMessage(FakeTerminalChannel.Status(
            activity: AgentActivity.WaitingReview,
            lease: new TerminalLease(you, TerminalClientKind.Remote, "Celular", DateTimeOffset.UnixEpoch)) with { YourClientId = you }));
        await WaitUntil(() => client.HasControl);
        client.CanPrompt.Should().BeTrue();

        await client.PromptAsync("Rode os testes");
        await client.PromptAsync("   ");
        Sent<TerminalPrompt>().Should().ContainSingle().Which.Text.Should().Be("Rode os testes");

        Channel.Send(new TerminalStateMessage(FakeTerminalChannel.Status(
            activity: AgentActivity.Working,
            lease: new TerminalLease(you, TerminalClientKind.Remote, "Celular", DateTimeOffset.UnixEpoch)) with { YourClientId = you }));
        await WaitUntil(() => client.Status?.Activity == AgentActivity.Working);
        client.CanPrompt.Should().BeFalse("trabalhando, o prompt espera; o Esc interrompe");
        client.CanInterrupt.Should().BeTrue();
    }

    [Fact]
    public async Task WithoutMonitoring_OnlyTheKeyboard()
    {
        var client = await ConnectedAsync(TerminalClientKind.Embedded);
        Channel.Send(new TerminalStateMessage(FakeTerminalChannel.Status(monitored: false)));
        await WaitUntil(() => client.Status is not null);

        client.CanPrompt.Should().BeFalse();
        client.MonitoringWarning.Should().NotBeNull();
    }

    [Fact]
    public async Task TheEvents_SayWhatChanged()
    {
        var screens = 0;
        var states = 0;
        var client = Create(TerminalClientKind.Embedded);
        client.ScreenChanged += () => screens++;
        client.StateChanged += () => states++;

        await client.ConnectAsync();
        states.Should().BeGreaterThan(0, "conectar muda o estado");

        Channel.Snapshot("olá");
        await WaitUntil(() => client.Screen is not null);
        screens.Should().Be(1);
    }

    /// <summary>Ir para segundo plano no celular solta a ligação sem encerrar o Claude, e religa depois.</summary>
    [Fact]
    public async Task Disconnecting_DropsTheChannel_AndReconnectingOpensAnother()
    {
        var client = await ConnectedAsync(TerminalClientKind.Remote);
        var first = Channel;

        await client.DisconnectAsync("Em segundo plano.");

        first.Disposed.Should().BeTrue();
        first.Stops.Should().BeEmpty("soltar a vista não encerra a sessão");
        client.IsDisconnected.Should().BeTrue();
        client.Message.Should().Be("Em segundo plano.");

        await client.ConnectAsync();
        _channels.Should().HaveCount(2);
        client.IsConnected.Should().BeTrue();
    }

    private TerminalSessionClient Create(TerminalClientKind kind)
    {
        _client = new TerminalSessionClient(
            Guid.NewGuid(),
            kind,
            (_, _) =>
            {
                var channel = new FakeTerminalChannel();
                _channels.Add(channel);
                return Task.FromResult<ITerminalChannel>(channel);
            },
            _ui.Enqueue,
            NullLogger.Instance);
        return _client;
    }

    private async Task<TerminalSessionClient> ConnectedAsync(TerminalClientKind kind)
    {
        var client = Create(kind);
        await client.ConnectAsync();
        return client;
    }

    private List<T> Sent<T>()
        where T : TerminalInput
    {
        lock (Channel.Sent)
        {
            return [.. Channel.Sent.OfType<T>()];
        }
    }

    private async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (true)
        {
            while (_ui.TryDequeue(out var action))
            {
                action();
            }

            if (condition())
            {
                return;
            }

            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("O cliente não chegou lá a tempo.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }
}
