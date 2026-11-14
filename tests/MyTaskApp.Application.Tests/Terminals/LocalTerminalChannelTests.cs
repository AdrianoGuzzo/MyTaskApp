using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace MyTaskApp.Application.Tests.Terminals;

/// <summary>
/// O canal local do terminal (ADR-098) por um pipe de verdade: a janela e o
/// app seguem a sessão, o retrato atravessa em pedaços, e o que não é do
/// formato fecha a conexão sem derrubar o agente.
/// </summary>
public sealed class LocalTerminalChannelTests : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 29, 9, 0, 0, TimeSpan.Zero));

    private readonly FakePseudoTerminal _pty = new();

    private readonly ITerminalSessions _terminals = Substitute.For<ITerminalSessions>();

    private readonly IUseCaseRunner _runner = Substitute.For<IUseCaseRunner>();

    private readonly TerminalSessionHost _host;

    private readonly List<Task> _served = [];

    public LocalTerminalChannelTests()
    {
        _host = new TerminalSessionHost(
            new TerminalSessionInfo(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), @"C:\wt", Monitored: true, PermissionsBypassed: false),
            _pty,
            _time,
            NullLogger<TerminalSessionHost>.Instance);
        _terminals.Find(_host.SessionId).Returns(_host);
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync();
        await Task.WhenAll(_served).WaitAsync(Patience);
    }

    [Fact]
    public async Task TheNativeWindow_SeesTheScreen_AndTypesIntoTheSameSession()
    {
        _pty.Print("olá do Claude\r\n");
        await ShownAsync("olá do Claude");
        await using var channel = await LocalTerminalChannel.AttachAsync(await ConnectAsync(), _host.SessionId, TerminalClientKind.NativeWindow, "Janela", new TerminalSize(100, 30), Ct);

        var first = await ReadOutputAsync(channel, "olá do Claude");
        first.Reset.Should().BeTrue("quem chega começa pelo retrato");

        await channel.SendKeysAsync("ls\r"u8.ToArray(), Ct);
        await WaitUntil(() => _pty.Input.Contains("ls\r", StringComparison.Ordinal));

        _pty.Print("depois\r\n");
        var next = await ReadOutputAsync(channel, "depois");
        next.Reset.Should().BeFalse();
        _pty.Size.Should().Be(new TerminalSize(100, 30), "a janela nativa dita o tamanho");
    }

    [Fact]
    public async Task ABigScreen_CrossesInPieces_AndArrivesWhole()
    {
        var line = new string('x', 199) + "\r\n";

        for (var i = 0; i < 2_000; i++)
        {
            _pty.Print(line);
        }

        _pty.Print("FIM\r\n");

        // Uma primeira vista espera a saída toda passar pela tela.
        await using (var warmup = await LocalTerminalChannel.AttachAsync(await ConnectAsync(), _host.SessionId, TerminalClientKind.Embedded, "App", null, Ct))
        {
            await ReadOutputAsync(warmup, "FIM");
        }

        await using var channel = await LocalTerminalChannel.AttachAsync(await ConnectAsync(), _host.SessionId, TerminalClientKind.Embedded, "App", null, Ct);
        var snapshot = await ReadOutputAsync(channel, "FIM");

        snapshot.Reset.Should().BeTrue();
        snapshot.Data.Length.Should().BeGreaterThan(TerminalFrameStream.SnapshotChunk, "o retrato maior que um quadro chega inteiro");
    }

    [Fact]
    public async Task ClosingTheWindow_OnlyDetaches()
    {
        var channel = await LocalTerminalChannel.AttachAsync(await ConnectAsync(), _host.SessionId, TerminalClientKind.NativeWindow, "Janela", null, Ct);
        await WaitUntil(() => _host.Status.Clients.Count == 1);

        await channel.DisposeAsync();

        await WaitUntil(() => _host.Status.Clients.Count == 0);
        _host.IsRunning.Should().BeTrue();
        _pty.Closed.Should().BeFalse();
        _pty.Killed.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stop_ClosesOrKills_AsAsked(bool force)
    {
        await using var channel = await LocalTerminalChannel.AttachAsync(await ConnectAsync(), _host.SessionId, TerminalClientKind.Embedded, "App", null, Ct);
        await WaitUntil(() => _host.Status.Clients.Count == 1);

        await channel.StopAsync(force, Ct);

        await WaitUntil(() => force ? _pty.Killed : _pty.Closed);
        (force ? _pty.Closed : _pty.Killed).Should().BeFalse("encerrar e matar são coisas diferentes");
    }

    [Fact]
    public async Task ARefusedAction_ComesBackAsText_AndTheConnectionStays()
    {
        await using var channel = await LocalTerminalChannel.AttachAsync(await ConnectAsync(), _host.SessionId, TerminalClientKind.Embedded, "App", null, Ct);

        await channel.SendAsync(new TerminalChoice(Guid.NewGuid(), Allow: true), Ct);
        var refusal = await ReadUntilAsync<TerminalRefusal>(channel);

        refusal.Text.Should().NotBeNullOrWhiteSpace();

        await channel.SendKeysAsync("ok"u8.ToArray(), Ct);
        await WaitUntil(() => _pty.Input.Contains("ok", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARemoteClient_CannotAttachThroughTheLocalChannel()
    {
        await using var channel = await LocalTerminalChannel.AttachAsync(await ConnectAsync(), _host.SessionId, TerminalClientKind.Remote, "Outro", null, Ct);

        var refusal = await ReadUntilAsync<TerminalRefusal>(channel);

        refusal.Text.Should().Contain("servidor");
        (await channel.ReadAsync(Ct)).Should().BeNull();
        _host.Status.Clients.Should().BeEmpty();
    }

    [Fact]
    public async Task AnUnknownSession_IsRefused()
    {
        await using var channel = await LocalTerminalChannel.AttachAsync(await ConnectAsync(), Guid.NewGuid(), TerminalClientKind.NativeWindow, "Janela", null, Ct);

        (await ReadUntilAsync<TerminalRefusal>(channel)).Text.Should().Contain("terminal do agente");
    }

    [Fact]
    public async Task AnotherVersion_IsRefused_WithAPlainSentence()
    {
        await using var stream = await ConnectAsync();
        await using var frames = new TerminalFrameStream(stream);

        await frames.WriteJsonAsync(TerminalFrameType.Hello, new TerminalHello(99, TerminalHello.Attach, _host.SessionId, TerminalClientKind.NativeWindow), Ct);

        var reply = await frames.ReadAsync(Ct);
        reply!.Type.Should().Be(TerminalFrameType.Error);
        Encoding.UTF8.GetString(reply.Payload).Should().Contain("versões diferentes");
        _host.Status.Clients.Should().BeEmpty();
    }

    [Fact]
    public async Task AnOversizedFrame_ClosesTheConnection_WithoutReadingIt()
    {
        await using var stream = await ConnectAsync();
        var header = new byte[5];
        header[0] = (byte)TerminalFrameType.Hello;
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(1), TerminalFrameStream.MaxPayload + 1);

        await stream.WriteAsync(header, Ct);
        await stream.FlushAsync(Ct);

        var buffer = new byte[16];
        (await stream.ReadAsync(buffer, Ct).AsTask().WaitAsync(Patience, Ct)).Should().Be(0, "o agente fecha sem alocar o quadro");
    }

    [Fact]
    public async Task AFrameOutOfPlace_ClosesTheConnection_AndTheSessionGoesOn()
    {
        await using var stream = await ConnectAsync();
        await using var channel = await LocalTerminalChannel.AttachAsync(stream, _host.SessionId, TerminalClientKind.Embedded, "App", null, Ct);
        await WaitUntil(() => _host.Status.Clients.Count == 1);

        // Um segundo Hello no meio da conversa não é do formato.
        await new TerminalFrameStream(stream).WriteJsonAsync(TerminalFrameType.Hello, new TerminalHello(1, TerminalHello.Attach, _host.SessionId, TerminalClientKind.Embedded), Ct);

        await WaitUntil(() => _host.Status.Clients.Count == 0);
        _host.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task Start_RunsTheSameUseCaseAsTheCard_AndAnswersWithTheSession()
    {
        var sessionId = Guid.NewGuid();
        _runner.RunAsync(Arg.Any<Func<StartAgentSessionHandler, CancellationToken, Task<AgentSessionView>>>(), Arg.Any<CancellationToken>())
            .Returns(new AgentSessionView(sessionId, Guid.NewGuid(), "claude-code", "Claude Code", "claude", @"C:\wt", 4242, _time.GetUtcNow(), null, AgentSessionStatus.Running, null));

        var result = await LocalTerminalChannel.StartAsync(await ConnectAsync(), Request(AgentHosting.AgentWindow), Ct);

        result.Should().Be(new TerminalStartResult(true, sessionId, null, null));
    }

    [Fact]
    public async Task ANewProviderCannotBeSilentlyOpenedAsClaudeByALegacyAgent()
    {
        var name = $"MyTaskApp.Terminal.LegacyTest.{Guid.NewGuid():N}";
        await using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await Task.WhenAll(server.WaitForConnectionAsync(Ct), client.ConnectAsync(Ct));
        var legacy = Task.Run(async () =>
        {
            await using var frames = new TerminalFrameStream(server);
            var frame = await frames.ReadAsync(Ct);
            var hello = TerminalFrameStream.ReadJson<TerminalHello>(frame!.Payload);
            hello.Version.Should().Be(TerminalHello.ProviderStartVersion);
            hello.Version.Should().NotBe(TerminalHello.CurrentVersion);
            await frames.WriteTextAsync(TerminalFrameType.Error, "O app e o agente estão em versões diferentes. Atualize os dois.", Ct);
        }, Ct);

        var result = await LocalTerminalChannel.StartAsync(client, Request(AgentHosting.AgentWindow) with { ProviderId = "codex" }, Ct);
        result.Ok.Should().BeFalse();
        result.Error.Should().Contain("versões diferentes");
        await legacy;
    }

    [Fact]
    public async Task Start_TurnsTheExpectedFailure_IntoTheSentence()
    {
        _runner.RunAsync(Arg.Any<Func<StartAgentSessionHandler, CancellationToken, Task<AgentSessionView>>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new DomainException("Já há um Claude rodando neste ambiente."));

        var result = await LocalTerminalChannel.StartAsync(await ConnectAsync(), Request(AgentHosting.AgentEmbedded), Ct);

        result.Ok.Should().BeFalse();
        result.Error.Should().Be("Já há um Claude rodando neste ambiente.");
    }

    [Fact]
    public async Task Start_RefusesTheDirectTerminal_WithoutRunningAnything()
    {
        var result = await LocalTerminalChannel.StartAsync(await ConnectAsync(), Request(AgentHosting.Direct), Ct);

        result.Ok.Should().BeFalse();
        await _runner.DidNotReceive().RunAsync(Arg.Any<Func<StartAgentSessionHandler, CancellationToken, Task<AgentSessionView>>>(), Arg.Any<CancellationToken>());
    }

    private static TerminalStartRequest Request(AgentHosting hosting) =>
        new(Guid.NewGuid(), Guid.NewGuid(), hosting, Arguments: null, Prompt: null, RunDirectly: false, Monitor: true, Model: null, Effort: null, ResumeConversation: null);

    /// <summary>Um par de pontas de um pipe novo: o servidor atende a sua com o <see cref="LocalTerminalServer"/>.</summary>
    private async Task<Stream> ConnectAsync()
    {
        var name = $"MyTaskApp.Terminal.Test.{Guid.NewGuid():N}";
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        await Task.WhenAll(server.WaitForConnectionAsync(Ct), client.ConnectAsync(Ct));

        var terminalServer = new LocalTerminalServer(_terminals, _runner, NullLogger<LocalTerminalServer>.Instance);
        _served.Add(Task.Run(async () =>
        {
            await using (server)
            {
                await terminalServer.ServeAsync(server, CancellationToken.None);
            }
        }, CancellationToken.None));

        return client;
    }

    /// <summary>
    /// Espera o host passar o texto para a tela. O <c>Print</c> só entrega ao
    /// pseudoterminal falso; sem esperar, a janela pode chegar antes, com o
    /// retrato vazio, e ver o texto como saída comum.
    /// </summary>
    private async Task ShownAsync(string text)
    {
        using var probe = _host.Attach(TerminalClientKind.Embedded, "Prova");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Patience);

        while (await probe.ReadAsync(timeout.Token) is { } message)
        {
            if (message is TerminalOutput output && Encoding.UTF8.GetString(output.Data).Contains(text, StringComparison.Ordinal))
            {
                return;
            }
        }

        throw new InvalidOperationException($"O host não mostrou \"{text}\".");
    }

    private static async Task<TerminalOutput> ReadOutputAsync(LocalTerminalChannel channel, string text)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Patience);

        while (await channel.ReadAsync(timeout.Token) is { } message)
        {
            if (message is TerminalOutput output && Encoding.UTF8.GetString(output.Data).Contains(text, StringComparison.Ordinal))
            {
                return output;
            }
        }

        throw new InvalidOperationException($"O canal fechou sem mostrar \"{text}\".");
    }

    private static async Task<T> ReadUntilAsync<T>(LocalTerminalChannel channel)
        where T : TerminalMessage
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(Patience);

        while (await channel.ReadAsync(timeout.Token) is { } message)
        {
            if (message is T wanted)
            {
                return wanted;
            }
        }

        throw new InvalidOperationException($"O canal fechou sem {typeof(T).Name}.");
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;

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
