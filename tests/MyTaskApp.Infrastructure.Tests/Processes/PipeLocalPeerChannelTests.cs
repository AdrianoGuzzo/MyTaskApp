using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.LocalPeers;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Tests.Processes;

/// <summary>
/// O canal local entre o desktop e o agente (ADR-085, T-11): um pipe de
/// verdade, só do usuário, com uma linha de JSON por aviso.
/// </summary>
public sealed class PipeLocalPeerChannelTests
{
    private static readonly TimeSpan Quick = TimeSpan.FromMilliseconds(100);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task TheDesktopAndTheAgent_HearEachOther()
    {
        var name = UniqueName();
        await using var agent = Channel(name, LocalPeerRole.Listen);
        await using var desktop = Channel(name, LocalPeerRole.Connect);
        var heardByAgent = Collect(agent);
        var heardByDesktop = Collect(desktop);

        agent.Start();
        desktop.Start();
        await WaitUntilAsync(() => agent.IsConnected && desktop.IsConnected);

        await desktop.SendAsync(new LocalPeerMessage(LocalPeerMessageKinds.DataChanged, DataArea.Tasks | DataArea.Time), Ct);
        await agent.SendAsync(new LocalPeerMessage(LocalPeerMessageKinds.SyncNow), Ct);

        await WaitUntilAsync(() => !heardByAgent.IsEmpty && !heardByDesktop.IsEmpty);
        heardByAgent.Should().ContainSingle().Which.Should().Be(new LocalPeerMessage(LocalPeerMessageKinds.DataChanged, DataArea.Tasks | DataArea.Time));
        heardByDesktop.Should().ContainSingle().Which.Kind.Should().Be(LocalPeerMessageKinds.SyncNow);
    }

    [Fact]
    public async Task WithoutTheAgent_SendingDoesNothing_AndTheDesktopConnectsWhenItComes()
    {
        var name = UniqueName();
        await using var desktop = Channel(name, LocalPeerRole.Connect);
        desktop.Start();

        await desktop.SendAsync(new LocalPeerMessage(LocalPeerMessageKinds.SyncWanted), Ct);
        desktop.IsConnected.Should().BeFalse();

        await using var agent = Channel(name, LocalPeerRole.Listen);
        var heard = Collect(agent);
        agent.Start();

        await WaitUntilAsync(() => desktop.IsConnected);
        await desktop.SendAsync(new LocalPeerMessage(LocalPeerMessageKinds.SyncWanted), Ct);
        await WaitUntilAsync(() => !heard.IsEmpty);
    }

    [Fact]
    public async Task TheAgentRestarting_TheDesktopReconnects()
    {
        var name = UniqueName();
        await using var desktop = Channel(name, LocalPeerRole.Connect);
        desktop.Start();

        var first = Channel(name, LocalPeerRole.Listen);
        first.Start();
        await WaitUntilAsync(() => desktop.IsConnected);
        await first.DisposeAsync();
        await WaitUntilAsync(() => !desktop.IsConnected);

        await using var second = Channel(name, LocalPeerRole.Listen);
        var heard = Collect(second);
        second.Start();

        await WaitUntilAsync(() => desktop.IsConnected);
        await desktop.SendAsync(new LocalPeerMessage(LocalPeerMessageKinds.DataChanged, DataArea.Tags), Ct);
        await WaitUntilAsync(() => !heard.IsEmpty);
    }

    [Fact]
    public async Task WhatIsNotANotice_IsIgnored_AndAnOversizedLineDropsTheConnection()
    {
        var name = UniqueName();
        await using var agent = Channel(name, LocalPeerRole.Listen);
        var heard = Collect(agent);
        agent.Start();

        await using var raw = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await raw.ConnectAsync(5000, Ct);

        await WriteAsync(raw, "isto não é json\n{\"kind\":\"runCommand\",\"command\":\"del\"}\n{\"kind\":\"dataChanged\",\"areas\":1}\n");
        await WaitUntilAsync(() => !heard.IsEmpty);
        heard.Should().ContainSingle().Which.Should().Be(new LocalPeerMessage(LocalPeerMessageKinds.DataChanged, DataArea.Tasks));

        await WriteAsync(raw, new string('x', PipeLocalPeerChannel.MaxLineLength + 10));
        await WaitUntilAsync(() => !agent.IsConnected);
    }

    [Fact]
    public void AreasThatDoNotExist_AreDropped()
    {
        PipeLocalPeerChannel.Parse("{\"kind\":\"dataChanged\",\"areas\":1025}")
            .Should().Be(new LocalPeerMessage(LocalPeerMessageKinds.DataChanged, DataArea.Tasks));
        PipeLocalPeerChannel.Parse("{\"kind\":\"syncNow\"}")
            .Should().Be(new LocalPeerMessage(LocalPeerMessageKinds.SyncNow));
        PipeLocalPeerChannel.Parse("null").Should().BeNull();
        PipeLocalPeerChannel.Parse("{}").Should().BeNull();
    }

    /// <summary>Duas instalações isoladas (testes, <c>MYTASKAPP_DATA_DIR</c>) nunca dividem o pipe.</summary>
    [Fact]
    public void EachDatabase_HasItsOwnPipe_WithoutShowingThePath()
    {
        var one = PipeLocalPeerChannel.NameFor(Path.Combine(Path.GetTempPath(), "a", "mytaskapp.db"));
        var other = PipeLocalPeerChannel.NameFor(Path.Combine(Path.GetTempPath(), "b", "mytaskapp.db"));

        one.Should().NotBe(other);
        one.Should().Be(PipeLocalPeerChannel.NameFor(Path.Combine(Path.GetTempPath(), "a", ".", "mytaskapp.db")));
        one.Should().StartWith("MyTaskApp.Peer.").And.NotContain("mytaskapp.db").And.NotContain(Environment.UserName);
    }

    private static PipeLocalPeerChannel Channel(string name, LocalPeerRole role) =>
        new(name, role, NullLogger<PipeLocalPeerChannel>.Instance, Quick);

    private static string UniqueName() => $"MyTaskApp.Peer.Test.{Guid.NewGuid():N}";

    private static ConcurrentQueue<LocalPeerMessage> Collect(ILocalPeerChannel channel)
    {
        var heard = new ConcurrentQueue<LocalPeerMessage>();
        channel.Received += heard.Enqueue;
        return heard;
    }

    private static async Task WriteAsync(Stream stream, string text)
    {
        await stream.WriteAsync(Encoding.UTF8.GetBytes(text), Ct);
        await stream.FlushAsync(Ct);
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("A condição não aconteceu a tempo.");
            }

            await Task.Delay(20, Ct);
        }
    }
}
