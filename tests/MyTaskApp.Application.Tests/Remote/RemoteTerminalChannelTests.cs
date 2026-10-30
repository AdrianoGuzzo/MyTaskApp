using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;
using MyTaskApp.Remote.Contracts;
using NSubstitute;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>
/// O lado de quem controla no terminal remoto (ADR-099): o pedido de conexão
/// leva uma chave só dela, cada lote sai assinado por ela e passa na conferência
/// do agente, e o que chega pelo relay vira a mesma mensagem do canal local.
/// </summary>
public sealed class RemoteTerminalChannelTests : IAsyncLifetime
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly RemoteDispatchKit _kit = new();

    private readonly FakeWire _wire = new();

    private readonly IRemoteOperationsApi _operations = Substitute.For<IRemoteOperationsApi>();

    private readonly RemoteTerminalTarget _target = new(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());

    private RemoteOperationStatus _attachStatus = RemoteOperationStatus.Succeeded;

    private string? _attachSummary;

    public ValueTask InitializeAsync()
    {
        _operations.GetAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => View(call.Arg<Guid>(), _attachStatus, _attachSummary));
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync() => await _kit.DisposeAsync();

    [Fact]
    public async Task Connecting_AsksForTheAttach_WithAKeyOfItsOwn_AndFollowsIt()
    {
        await using var channel = await ConnectAsync();

        var (attach, request) = SentAttach();
        attach.SessionId.Should().Be(_target.SessionId);
        attach.DevelopmentId.Should().Be(_target.DevelopmentId);
        attach.ViewerKey.Fingerprint.Should().NotBe((await _kit.Signer.GetPublicKeyAsync(Ct)).Fingerprint, "a chave da conexão não é a do aparelho");
        (attach.Columns, attach.Rows).Should().Be((100, 30));
        request.TargetDeviceId.Should().Be(_target.DeviceId);
        _wire.Subscribed.Should().Equal(request.OperationId);
    }

    [Fact]
    public async Task EachBatch_IsSignedByTheConnectionKey_AndPassesTheAgentsCheck_InOrder()
    {
        await using var channel = await ConnectAsync();
        var (attach, request) = SentAttach();
        var expected = new RemoteTerminalExpectation(request.OperationId, _target.DeviceId, _target.SessionId, attach.ViewerKey);

        await channel.SendAsync(new TerminalTakeControl(), Ct);
        await channel.SendAsync(new TerminalKeys("ls\r"u8.ToArray()), Ct);
        await channel.SendAsync(new TerminalChoice(Guid.CreateVersion7(), Allow: true), Ct);

        var last = 0L;
        var inputs = new List<TerminalInput?>();

        foreach (var (attachId, envelope) in _wire.Sent)
        {
            attachId.Should().Be(request.OperationId);
            var (input, sequence, problem) = RemoteTerminalInputVerifier.Verify(envelope, expected, last, _kit.Time.GetUtcNow());
            problem.Should().BeNull();
            last = sequence;
            inputs.Add(input);
        }

        inputs.Select(input => input?.GetType()).Should().Equal(typeof(TerminalTakeControl), typeof(TerminalKeys), typeof(TerminalChoice));
        inputs[1].Should().BeOfType<TerminalKeys>().Which.Data.Should().Equal("ls\r"u8.ToArray());
    }

    [Fact]
    public async Task ABigPaste_GoesInPiecesThatFitTheLimit()
    {
        await using var channel = await ConnectAsync();

        await channel.SendAsync(new TerminalKeys(new byte[(RemoteLimits.MaxTerminalKeys * 2) + 10]), Ct);

        _wire.Sent.Should().HaveCount(3);
    }

    [Fact]
    public async Task TheSnapshot_InPieces_ArrivesWhole_AtItsOffset()
    {
        await using var channel = await ConnectAsync();
        var attachId = SentAttach().Request.OperationId;

        _wire.Frame(new RemoteTerminalFrame(attachId, RemoteTerminalFrameKinds.Output, 500, "ab"u8.ToArray(), Reset: true, More: true));
        _wire.Frame(new RemoteTerminalFrame(attachId, RemoteTerminalFrameKinds.Output, 500, "cd"u8.ToArray()));
        _wire.Frame(new RemoteTerminalFrame(attachId, RemoteTerminalFrameKinds.Output, 500, "ef"u8.ToArray()));

        var snapshot = (await channel.ReadAsync(Ct)).Should().BeOfType<TerminalOutput>().Which;
        snapshot.Reset.Should().BeTrue();
        snapshot.Offset.Should().Be(500);
        snapshot.Data.Should().Equal("abcd"u8.ToArray());
        (await channel.ReadAsync(Ct)).Should().BeOfType<TerminalOutput>().Which.Offset.Should().Be(500);
    }

    [Fact]
    public async Task OutputInPieces_JoinsInOrder_AndAPieceThatDoesNotFollow_DropsTheHalf()
    {
        await using var channel = await ConnectAsync();
        var attachId = SentAttach().Request.OperationId;

        _wire.Frame(new RemoteTerminalFrame(attachId, RemoteTerminalFrameKinds.Output, 10, "ab"u8.ToArray(), More: true));
        _wire.Frame(new RemoteTerminalFrame(attachId, RemoteTerminalFrameKinds.Output, 12, "cd"u8.ToArray()));
        _wire.Frame(new RemoteTerminalFrame(attachId, RemoteTerminalFrameKinds.Output, 14, "x"u8.ToArray(), More: true));
        _wire.Frame(new RemoteTerminalFrame(attachId, RemoteTerminalFrameKinds.Output, 99, "y"u8.ToArray()));

        var joined = (await channel.ReadAsync(Ct)).Should().BeOfType<TerminalOutput>().Which;
        (joined.Offset, joined.Data).Should().Be((10L, joined.Data));
        joined.Data.Should().Equal("abcd"u8.ToArray());

        // O "x" ficou pela metade: a vista vê o buraco pelo deslocamento e pede o retrato.
        (await channel.ReadAsync(Ct)).Should().BeOfType<TerminalOutput>().Which.Offset.Should().Be(99);
    }

    [Fact]
    public async Task TheState_ComesWithoutTheFolder_AndKnowsWhichViewerThisIs()
    {
        await using var channel = await ConnectAsync();
        var attachId = SentAttach().Request.OperationId;
        var you = Guid.CreateVersion7();

        _wire.Frame(new RemoteTerminalFrame(attachId, RemoteTerminalFrameKinds.State, State: State(you)));

        var status = (await channel.ReadAsync(Ct)).Should().BeOfType<TerminalStateMessage>().Which.Status;
        status.WorkingDirectory.Should().BeEmpty();
        status.YourClientId.Should().Be(you);
        status.Lease!.ClientId.Should().Be(you);
    }

    [Fact]
    public async Task AFrameOfAnotherConnection_OrAnotherSession_IsIgnored()
    {
        await using var channel = await ConnectAsync();
        var attachId = SentAttach().Request.OperationId;

        _wire.Frame(new RemoteTerminalFrame(Guid.CreateVersion7(), RemoteTerminalFrameKinds.Notice, Text: "de outra"));
        _wire.Frame(new RemoteTerminalFrame(attachId, RemoteTerminalFrameKinds.State, State: State(null) with { SessionId = Guid.CreateVersion7() }));
        _wire.Frame(new RemoteTerminalFrame(attachId, RemoteTerminalFrameKinds.Notice, Text: "desta"));

        (await channel.ReadAsync(Ct)).Should().Be(new TerminalNotice("desta"));
    }

    [Fact]
    public async Task TextFromThere_LosesControlCharacters_BeforeReachingTheScreen()
    {
        await using var channel = await ConnectAsync();
        var attachId = SentAttach().Request.OperationId;

        _wire.Frame(new RemoteTerminalFrame(attachId, RemoteTerminalFrameKinds.Refusal, Text: "Recusado\u001b[2J‮ agora"));

        (await channel.ReadAsync(Ct)).Should().Be(new TerminalRefusal("Recusado[2J agora"));
    }

    [Fact]
    public async Task AGap_DropsTheHalfAndAsksForTheSnapshot()
    {
        await using var channel = await ConnectAsync();
        var (attach, request) = SentAttach();

        _wire.Gap(request.OperationId);
        await WaitUntilAsync(() => _wire.Sent.Count == 1);

        var expected = new RemoteTerminalExpectation(request.OperationId, _target.DeviceId, _target.SessionId, attach.ViewerKey);
        RemoteTerminalInputVerifier.Verify(_wire.Sent[0].Envelope, expected, 0, _kit.Time.GetUtcNow()).Input.Should().BeOfType<TerminalResync>();
    }

    [Fact]
    public async Task TheConnectionClosingThere_EndsTheReading_WithTheReason()
    {
        await using var channel = await ConnectAsync();

        _wire.Closed(SentAttach().Request.OperationId, "O terminal remoto foi desligado nesta máquina.");

        (await channel.ReadAsync(Ct)).Should().Be(new TerminalNotice("O terminal remoto foi desligado nesta máquina."));
        (await channel.ReadAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task TheServerDropping_EndsTheReading_SoTheViewOffersReconnect()
    {
        await using var channel = await ConnectAsync();

        _wire.Drop();

        (await channel.ReadAsync(Ct)).Should().BeOfType<TerminalNotice>().Which.Text.Should().Contain("conecte de novo");
        (await channel.ReadAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task TheAgentRefusingTheAttach_ReachesTheView_AndEndsIt()
    {
        _attachStatus = RemoteOperationStatus.Failed;
        _attachSummary = "O terminal remoto está desligado nesta máquina.";
        await using var channel = await ConnectAsync();

        (await channel.ReadAsync(Ct)).Should().Be(new TerminalRefusal("O terminal remoto está desligado nesta máquina."));
        (await channel.ReadAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task Stopping_IsAnOperation_GracefulOrForced_OnlyForThatSession()
    {
        await using var channel = await ConnectAsync();

        await channel.StopAsync(force: false, Ct);
        await channel.StopAsync(force: true, Ct);

        var stops = _kit.Server.Operations.Values
            .Select(entry => Payload(entry.Envelope))
            .Where(request => request.Kind == RemoteOperationKinds.TerminalStop)
            .Select(request => request.Payload.Deserialize<RemoteTerminalStopRequest>(RemoteJson.Options)!)
            .ToList();

        stops.Should().Equal(
            new RemoteTerminalStopRequest(_target.SessionId, RemoteTerminalStopMode.Graceful),
            new RemoteTerminalStopRequest(_target.SessionId, RemoteTerminalStopMode.Force));
    }

    [Fact]
    public async Task Disposing_ClosesTheHubConnection_ThatIsTheLeavingForTheAgent()
    {
        var channel = await ConnectAsync();

        await channel.DisposeAsync();

        _wire.Disposed.Should().BeTrue();
        (await channel.ReadAsync(Ct)).Should().BeNull();
    }

    [Fact]
    public async Task WithoutTheServer_ConnectingFails_WithTheReason_AndLeavesNothingOpen()
    {
        var wires = Substitute.For<IRemoteTerminalWires>();
        wires.OpenAsync(Arg.Any<CancellationToken>()).Returns<IRemoteTerminalWire>(_ => throw new DomainException("sem servidor"));

        var act = () => Connector(wires).ConnectAsync(_target, new TerminalSize(100, 30), Ct);

        await act.Should().ThrowAsync<DomainException>().WithMessage("sem servidor");
        _kit.Server.Operations.Should().BeEmpty("sem a conexão do hub, o pedido nem sai");
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 200 && !condition(); attempt++)
        {
            await Task.Delay(10, Ct);
        }

        condition().Should().BeTrue();
    }

    private static RemoteOperationRequest Payload(SignedEnvelope envelope)
    {
        RemoteJws.TryParse(envelope, out var parts).Should().BeTrue();
        return JsonSerializer.Deserialize<RemoteOperationRequest>(parts.Payload, RemoteJson.Options)!;
    }

    private static RemoteOperationView View(Guid id, RemoteOperationStatus status, string? summary)
    {
        var now = DateTimeOffset.UtcNow;
        return new RemoteOperationView(id, Guid.Empty, Guid.Empty, Guid.Empty, RemoteOperationKinds.TerminalAttach, null, status, now, now, now, null, null, summary, 0);
    }

    private RemoteTerminalState State(Guid? you) =>
        new(
            _target.SessionId,
            _target.TaskId,
            _target.DevelopmentId,
            4242,
            _kit.Time.GetUtcNow(),
            100,
            30,
            RemoteTerminalProcess.Running,
            null,
            RemoteAgentActivity.Working,
            null,
            null,
            you,
            you is { } id ? [new RemoteTerminalViewer(id, "remote", "Notebook", _kit.Time.GetUtcNow())] : [],
            Monitored: true,
            PermissionsBypassed: false,
            PermissionMode: null,
            you);

    private (RemoteTerminalAttachRequest Attach, RemoteOperationRequest Request) SentAttach()
    {
        var request = _kit.Server.Operations.Values.Select(entry => Payload(entry.Envelope)).Single(item => item.Kind == RemoteOperationKinds.TerminalAttach);
        return (request.Payload.Deserialize<RemoteTerminalAttachRequest>(RemoteJson.Options)!, request);
    }

    private Task<ITerminalChannel> ConnectAsync() => Connector(new Wires(_wire)).ConnectAsync(_target, new TerminalSize(100, 30), Ct);

    private RemoteTerminalConnector Connector(IRemoteTerminalWires wires)
    {
        var runner = new Runner();
        runner.Add(new SendRemoteOperationHandler(_kit.Server, _kit.Signer, _kit.Identity, _kit.Sync, _kit.Time));
        runner.Add(_operations);
        return new RemoteTerminalConnector(runner, wires, _kit.Time, NullLogger<RemoteTerminalChannel>.Instance);
    }

    private sealed class Runner : IUseCaseRunner
    {
        private readonly Dictionary<Type, object> _handlers = [];

        public void Add<T>(T handler)
            where T : notnull => _handlers[typeof(T)] = handler;

        public Task<TResult> RunAsync<THandler, TResult>(Func<THandler, CancellationToken, Task<TResult>> operation, CancellationToken cancellationToken = default)
            where THandler : notnull =>
            operation((THandler)_handlers[typeof(THandler)], cancellationToken);

        public Task RunAsync<THandler>(Func<THandler, CancellationToken, Task> operation, CancellationToken cancellationToken = default)
            where THandler : notnull =>
            operation((THandler)_handlers[typeof(THandler)], cancellationToken);
    }

    private sealed class Wires(FakeWire wire) : IRemoteTerminalWires
    {
        public Task<IRemoteTerminalWire> OpenAsync(CancellationToken cancellationToken) => Task.FromResult<IRemoteTerminalWire>(wire);
    }

    private sealed class FakeWire : IRemoteTerminalWire
    {
        private readonly List<(Guid AttachId, SignedEnvelope Envelope)> _sent = [];

        public event Action<RemoteTerminalFrame>? FrameReceived;

        public event Action<Guid>? GapReceived;

        public event Action<Guid, string>? ClosedReceived;

        public event Action? Dropped;

        public List<Guid> Subscribed { get; } = [];

        public IReadOnlyList<(Guid AttachId, SignedEnvelope Envelope)> Sent
        {
            get
            {
                lock (_sent)
                {
                    return [.. _sent];
                }
            }
        }

        public bool Disposed { get; private set; }

        public Task SubscribeAsync(Guid attachId, CancellationToken cancellationToken)
        {
            Subscribed.Add(attachId);
            return Task.CompletedTask;
        }

        public Task SendAsync(Guid attachId, SignedEnvelope input, CancellationToken cancellationToken)
        {
            lock (_sent)
            {
                _sent.Add((attachId, input));
            }

            return Task.CompletedTask;
        }

        public void Frame(RemoteTerminalFrame frame) => FrameReceived?.Invoke(frame);

        public void Gap(Guid attachId) => GapReceived?.Invoke(attachId);

        public void Closed(Guid attachId, string reason) => ClosedReceived?.Invoke(attachId, reason);

        public void Drop() => Dropped?.Invoke();

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
