using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Server.Application.Tests;

/// <summary>
/// O relay do terminal remoto (ADR-099): quem pediu a conexão segue e digita,
/// só a máquina alvo publica, a fila de quem segue tem teto, e nada da saída
/// ou da entrada é gravado.
/// </summary>
public sealed class RemoteTerminalUseCasesTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);

    private readonly Guid _user = Guid.CreateVersion7();

    private readonly Guid _controller = Guid.CreateVersion7();

    private readonly Guid _executor = Guid.CreateVersion7();

    private readonly FakeTimeProvider _clock = new(Now);

    private readonly FakeDevices _devices = new();

    private readonly FakeOperations _operations = new();

    private readonly FakeNotifier _notifier = new();

    private readonly FakeTransport _transport = new();

    private readonly RecordingLogger _logs = new();

    private readonly RemoteTerminalRelay _relay;

    private readonly RemoteTerminalDirectory _directory = new();

    public RemoteTerminalUseCasesTests()
    {
        _relay = new RemoteTerminalRelay(_transport, _notifier, NullLogger<RemoteTerminalRelay>.Instance);
        _devices.Add(_controller, DeviceRoles.Controller);
        _devices.Add(_executor, DeviceRoles.Controller | DeviceRoles.Executor);
    }

    private SyncCaller Controller => new(_user, _controller);

    private SyncCaller Executor => new(_user, _executor);

    [Fact]
    public async Task WhoAskedForTheConnection_GetsWhatTheTargetPublishes_InOrder()
    {
        var attach = Attach();
        var terminals = UseCases();

        await terminals.SubscribeAsync(Controller, "c1", attach, TestContext.Current.CancellationToken);
        await terminals.PublishAsync(Executor, "a1", Output(attach, 0, "olá", reset: true), TestContext.Current.CancellationToken);
        await terminals.PublishAsync(Executor, "a1", Output(attach, 3, " mundo"), TestContext.Current.CancellationToken);

        var sent = await _transport.WaitForAsync(2);
        sent.Should().AllSatisfy(item => item.ConnectionId.Should().Be("c1"));
        sent.Select(item => ((RemoteTerminalFrame)item.Args[0]!).Offset).Should().Equal(0, 3);
    }

    [Fact]
    public async Task OnlyTheRequester_Follows_AndOnlyTheTarget_Publishes()
    {
        var attach = Attach();
        var other = Guid.CreateVersion7();
        _devices.Add(other, DeviceRoles.Controller | DeviceRoles.Executor);
        var terminals = UseCases();
        var ct = TestContext.Current.CancellationToken;

        await FluentActions.Awaiting(() => terminals.SubscribeAsync(new SyncCaller(_user, other), "x", attach, ct)).Should().ThrowAsync<SyncServerException>();
        await FluentActions.Awaiting(() => terminals.SubscribeAsync(Executor, "x", attach, ct)).Should().ThrowAsync<SyncServerException>();
        await FluentActions.Awaiting(() => terminals.SubscribeAsync(new SyncCaller(Guid.CreateVersion7(), _controller), "x", attach, ct))
            .Should().ThrowAsync<SyncServerException>("a operação de outra conta nem aparece");
        await FluentActions.Awaiting(() => terminals.SendAsync(new SyncCaller(_user, other), attach, new SignedEnvelope("a.b.c"), ct)).Should().ThrowAsync<SyncServerException>();
        await FluentActions.Awaiting(() => terminals.PublishAsync(Controller, "c1", Output(attach, 0, "x"), ct)).Should().ThrowAsync<SyncServerException>();
        await FluentActions.Awaiting(() => terminals.CloseAsync(Controller, attach, null, ct)).Should().ThrowAsync<SyncServerException>();

        _logs.Lines.Should().Contain(line => line.Contains("RemoteTerminalRefused", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(RemoteOperationKinds.AgentStart, RemoteOperationStatus.Succeeded)]
    [InlineData(RemoteOperationKinds.TerminalStart, RemoteOperationStatus.Succeeded)]
    [InlineData(RemoteOperationKinds.TerminalAttach, RemoteOperationStatus.Rejected)]
    [InlineData(RemoteOperationKinds.TerminalAttach, RemoteOperationStatus.Expired)]
    [InlineData(RemoteOperationKinds.TerminalAttach, RemoteOperationStatus.Failed)]
    public async Task AnythingButALiveAttach_IsNotAConnection(string kind, RemoteOperationStatus status)
    {
        var attach = Attach(kind, status);

        await FluentActions.Awaiting(() => UseCases().SubscribeAsync(Controller, "c1", attach, TestContext.Current.CancellationToken))
            .Should().ThrowAsync<SyncServerException>();
    }

    [Fact]
    public async Task TheInput_GoesToTheTarget_AsItCame_AndIsNeverLogged()
    {
        var attach = Attach();
        var input = new SignedEnvelope("eyJhbGciOiJFUzI1NiJ9.c2VncmVkbw.YXNzaW5hdHVyYQ");

        await UseCases().SendAsync(Controller, attach, input, TestContext.Current.CancellationToken);

        _notifier.Sent.Should().ContainSingle().Which.Should().Be((_user, _executor, RemoteHubMessages.TerminalInput, attach, input));
        _logs.Lines.Should().NotContain(line => line.Contains("c2VncmVkbw", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnEmptyOrHugeInput_IsRefused()
    {
        var attach = Attach();
        var terminals = UseCases();
        var ct = TestContext.Current.CancellationToken;

        await FluentActions.Awaiting(() => terminals.SendAsync(Controller, attach, new SignedEnvelope(string.Empty), ct)).Should().ThrowAsync<SyncServerException>();
        await FluentActions.Awaiting(() => terminals.SendAsync(Controller, attach, new SignedEnvelope(new string('a', RemoteJws.MaxLength + 1)), ct)).Should().ThrowAsync<SyncServerException>();
        _notifier.Sent.Should().BeEmpty();
    }

    [Fact]
    public async Task AFrameTooBig_OrOfAnUnknownKind_IsRefused()
    {
        var attach = Attach();
        var terminals = UseCases();
        var ct = TestContext.Current.CancellationToken;

        await FluentActions.Awaiting(() => terminals.PublishAsync(Executor, "a1", new RemoteTerminalFrame(attach, RemoteTerminalFrameKinds.Output, Data: new byte[RemoteLimits.MaxTerminalFrameData + 1]), ct))
            .Should().ThrowAsync<SyncServerException>();
        await FluentActions.Awaiting(() => terminals.PublishAsync(Executor, "a1", new RemoteTerminalFrame(attach, "exec", Text: "rm"), ct))
            .Should().ThrowAsync<SyncServerException>();
    }

    [Fact]
    public async Task ASlowFollower_NeverHoldsTheAgent_TheQueueHasACeiling_AndAGapWaitsForASnapshot()
    {
        var attach = Attach();
        var terminals = UseCases();
        var ct = TestContext.Current.CancellationToken;
        _transport.Block();

        await terminals.SubscribeAsync(Controller, "c1", attach, ct);
        await terminals.PublishAsync(Executor, "a1", Output(attach, 0, "primeiro"), ct);
        await _transport.WaitForAsync(1);

        var chunk = new byte[RemoteLimits.MaxTerminalFrameData];
        var publishing = Task.Run(
            async () =>
            {
                for (var index = 0; index < 100; index++)
                {
                    await terminals.PublishAsync(Executor, "a1", new RemoteTerminalFrame(attach, RemoteTerminalFrameKinds.Output, index * chunk.Length, chunk), ct);
                }
            },
            ct);

        await publishing.WaitAsync(TimeSpan.FromSeconds(10), ct);
        _relay.Find(attach)!.PendingBytes.Should().BeLessThanOrEqualTo(RemoteLimits.MaxTerminalPendingBytes);

        await terminals.PublishAsync(Executor, "a1", Output(attach, 999, "perdido"), ct);
        await terminals.PublishAsync(Executor, "a1", Output(attach, 0, "retrato", reset: true), ct);
        _transport.Release();

        var sent = await _transport.WaitUntilAsync(items => items.Any(item => item.Message == RemoteHubMessages.TerminalFrame && ((RemoteTerminalFrame)item.Args[0]!).Reset));
        var afterGap = sent.SkipWhile(item => item.Message != RemoteHubMessages.TerminalGap).ToList();

        afterGap.Should().NotBeEmpty("estourar o teto avisa a lacuna");
        afterGap.Skip(1).Should().ContainSingle(item => item.Message == RemoteHubMessages.TerminalFrame)
            .Which.Args[0].Should().BeOfType<RemoteTerminalFrame>().Which.Reset.Should().BeTrue("depois da lacuna, só o retrato desenha");
    }

    [Fact]
    public async Task WhenTheFollowerLeaves_TheAgentHears_AndTheConnectionIsOver()
    {
        var attach = Attach();
        var terminals = UseCases();
        var ct = TestContext.Current.CancellationToken;
        await terminals.SubscribeAsync(Controller, "c1", attach, ct);
        await terminals.PublishAsync(Executor, "a1", Output(attach, 0, "x"), ct);

        await _relay.DisconnectedAsync("c1");

        _notifier.Sent.Should().Contain((_user, _executor, RemoteHubMessages.TerminalDetached, attach, null));
        await FluentActions.Awaiting(() => terminals.SubscribeAsync(Controller, "c2", attach, ct))
            .Should().ThrowAsync<SyncServerException>("voltar é outra conexão, com outra chave");
    }

    [Fact]
    public async Task AClosedConnection_IsForgottenOnlyAfterItsDeadline_AndNeverComesBack()
    {
        var attach = Attach();
        var terminals = UseCases();
        var ct = TestContext.Current.CancellationToken;
        await terminals.SubscribeAsync(Controller, "c1", attach, ct);
        await terminals.CloseAsync(Executor, attach, "fim", ct);

        _clock.Advance(TimeSpan.FromMinutes(11));
        await FluentActions.Awaiting(() => terminals.SubscribeAsync(Controller, "c2", Attach(), ct)).Should().ThrowAsync<SyncServerException>("o pedido novo também venceu");
        await FluentActions.Awaiting(() => terminals.SubscribeAsync(Controller, "c2", attach, ct)).Should().ThrowAsync<SyncServerException>();
    }

    [Fact]
    public async Task WhenTheAgentLeaves_TheFollowerHearsTheConnectionClosed()
    {
        var attach = Attach();
        var terminals = UseCases();
        var ct = TestContext.Current.CancellationToken;
        await terminals.SubscribeAsync(Controller, "c1", attach, ct);
        await terminals.PublishAsync(Executor, "a1", Output(attach, 0, "x"), ct);

        await _relay.DisconnectedAsync("a1");

        var sent = await _transport.WaitUntilAsync(items => items.Any(item => item.Message == RemoteHubMessages.TerminalClosed));
        sent.Last().Args.Should().Equal(attach, "O agente da máquina saiu do ar. O Claude pode seguir rodando lá.");
        await FluentActions.Awaiting(() => terminals.PublishAsync(Executor, "a1", Output(attach, 1, "y"), ct)).Should().ThrowAsync<SyncServerException>();
    }

    [Fact]
    public async Task TheAgentClosing_WithAReasonFullOfEscapes_ArrivesClean()
    {
        var attach = Attach();
        var terminals = UseCases();
        var ct = TestContext.Current.CancellationToken;
        await terminals.SubscribeAsync(Controller, "c1", attach, ct);

        await terminals.CloseAsync(Executor, attach, "\u001b[2JO Claude terminou.", ct);

        var sent = await _transport.WaitForAsync(1);
        sent[0].Args.Should().Equal(attach, "[2JO Claude terminou.");
    }

    [Fact]
    public async Task TheSessions_AreReported_Listed_AndGoAwayWithTheMachine()
    {
        var terminals = UseCases();
        var ct = TestContext.Current.CancellationToken;
        var batch = new RemoteTerminalSessionBatch([Session()]);

        await terminals.ReportAsync(Executor, batch, ct);
        await terminals.ReportAsync(Executor, batch, ct);

        _notifier.Sent.Should().ContainSingle(sent => sent.Message == RemoteHubMessages.TerminalsChanged, "o mesmo relato não avisa de novo");
        terminals.List(Controller).Sessions.Should().ContainSingle().Which.DeviceId.Should().Be(_executor);
        terminals.List(new SyncCaller(Guid.CreateVersion7(), _controller)).Sessions.Should().BeEmpty("cada conta vê as suas");

        _clock.Advance(RemoteLimits.OfflineAfter + TimeSpan.FromSeconds(1));
        terminals.List(Controller).Sessions.Should().BeEmpty("a máquina parou de dar notícias");
    }

    [Fact]
    public async Task AReport_FromAMachineThatIsNotAnExecutor_OrMalformed_IsRefused()
    {
        var terminals = UseCases();
        var ct = TestContext.Current.CancellationToken;

        await FluentActions.Awaiting(() => terminals.ReportAsync(Controller, new RemoteTerminalSessionBatch([Session()]), ct)).Should().ThrowAsync<SyncServerException>();
        await FluentActions.Awaiting(() => terminals.ReportAsync(Executor, new RemoteTerminalSessionBatch([Session() with { ActivityMessage = "\u001b[31m" }]), ct)).Should().ThrowAsync<SyncServerException>();
        await FluentActions.Awaiting(() => terminals.ReportAsync(Executor, new RemoteTerminalSessionBatch([.. Enumerable.Range(0, RemoteLimits.MaxTerminalSessionsPerBatch + 1).Select(_ => Session())]), ct))
            .Should().ThrowAsync<SyncServerException>();

        var repeated = Session();
        await FluentActions.Awaiting(() => terminals.ReportAsync(Executor, new RemoteTerminalSessionBatch([repeated, repeated]), ct)).Should().ThrowAsync<SyncServerException>();
    }

    private RemoteTerminalUseCases UseCases() =>
        new(_devices, _operations, _relay, _directory, _notifier, _clock, _logs);

    private Guid Attach(string kind = RemoteOperationKinds.TerminalAttach, RemoteOperationStatus status = RemoteOperationStatus.Succeeded)
    {
        var id = Guid.CreateVersion7();
        _operations.Add(new RemoteOperationRecord
        {
            OperationId = id,
            UserId = _user,
            WorkspaceId = Guid.CreateVersion7(),
            TargetDeviceId = _executor,
            RequestedByDeviceId = _controller,
            Kind = kind,
            Status = status,
            CreatedAt = Now,
            NotAfter = Now.AddMinutes(10),
            ReceivedAt = Now,
            UpdatedAt = Now,
            Request = "a.b.c",
            RequestHash = new string('0', 64),
        });

        return id;
    }

    private static RemoteTerminalFrame Output(Guid attach, long offset, string text, bool reset = false) =>
        new(attach, RemoteTerminalFrameKinds.Output, offset, System.Text.Encoding.UTF8.GetBytes(text), reset);

    private static RemoteTerminalSessionReport Session() => new(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        RemoteTerminalProcess.Running,
        null,
        RemoteAgentActivity.Working,
        "Rodando testes",
        HasDialog: false,
        Monitored: true,
        PermissionsBypassed: false,
        RemotelyControlled: false,
        Now,
        Now);

    private sealed class FakeDevices : IRemoteDeviceStore
    {
        private readonly List<RemoteDeviceState> _devices = [];

        public void Add(Guid deviceId, DeviceRoles roles) => _devices.Add(new RemoteDeviceState(
            deviceId,
            roles,
            "fp",
            new DeviceCapabilities(1, [], "windows", new ToolStatus(true, null), new ToolStatus(true, null), new ToolStatus(false, null), new ToolStatus(false, null), 3, "hash"),
            Now));

        public Task<IReadOnlyList<RemoteDeviceState>> ListAsync(Guid userId, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RemoteDeviceState>>(_devices);

        public Task<RemoteRegistrationOutcome> RegisterAsync(SyncCaller caller, DeviceRegistration registration, DateTimeOffset at, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<DevicePublicKey?> FindActiveKeyAsync(Guid userId, Guid deviceId, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    /// <summary>Só lê: qualquer gravação quebra o teste, porque o terminal não grava nada.</summary>
    private sealed class FakeOperations : IRemoteOperationStore
    {
        private readonly List<RemoteOperationRecord> _operations = [];

        public void Add(RemoteOperationRecord operation) => _operations.Add(operation);

        public Task<RemoteOperationRecord?> FindAsync(Guid userId, Guid operationId, CancellationToken cancellationToken) =>
            Task.FromResult(_operations.SingleOrDefault(operation => operation.UserId == userId && operation.OperationId == operationId));

        public Task<RemoteAddOutcome> TryAddAsync(RemoteOperationRecord operation, int maxOpen, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RemoteOperationRecord>> ListRecentAsync(Guid userId, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<Guid>> DispatchQueuedAsync(Guid userId, Guid targetDeviceId, DateTimeOffset at, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RemoteOperationRecord>> ListActiveForTargetAsync(Guid userId, Guid targetDeviceId, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<bool> TryMoveAsync(Guid operationId, RemoteOperationStatus from, RemoteOperationStatus to, DateTimeOffset at, string? code, string? summary, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task RequestCancelAsync(Guid operationId, DateTimeOffset at, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<long> AppendEventsAsync(Guid operationId, IReadOnlyList<RemoteOperationEvent> events, int maxStored, DateTimeOffset at, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<RemoteOperationEvent>> ListEventsAsync(Guid operationId, long after, int limit, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RemoteOperationRecord>> ExpireAsync(DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RemoteOperationRecord>> RequeueAsync(DateTimeOffset dispatchedBefore, DateTimeOffset now, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> PurgeAsync(DateTimeOffset before, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<int> PruneEventsAsync(DateTimeOffset receivedBefore, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task LinkSessionAsync(Guid userId, Guid targetDeviceId, Guid operationId, Guid sessionId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<IReadOnlyList<RemoteOperationRecord>> ListForSessionAsync(Guid userId, Guid targetDeviceId, Guid sessionId, int limit, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeNotifier : IRemoteNotifier
    {
        public List<(Guid User, Guid? Device, string Message, object? First, object? Second)> Sent { get; } = [];

        public Task ToDeviceAsync(Guid userId, Guid deviceId, string message, params object?[] args)
        {
            lock (Sent)
            {
                Sent.Add((userId, deviceId, message, args.ElementAtOrDefault(0), args.ElementAtOrDefault(1)));
            }

            return Task.CompletedTask;
        }

        public Task ToAccountAsync(Guid userId, string message, params object?[] args)
        {
            lock (Sent)
            {
                Sent.Add((userId, null, message, args.ElementAtOrDefault(0), args.ElementAtOrDefault(1)));
            }

            return Task.CompletedTask;
        }
    }

    /// <summary>A conexão de quem segue. Bloqueada, faz o papel do controlador lento.</summary>
    private sealed class FakeTransport : IRemoteTerminalTransport
    {
        private readonly List<(string ConnectionId, string Message, object?[] Args)> _sent = [];

        private TaskCompletionSource _open = CompletedSource();

        public async Task ToConnectionAsync(string connectionId, string message, object?[] args, CancellationToken cancellationToken)
        {
            lock (_sent)
            {
                _sent.Add((connectionId, message, args));
            }

            await _open.Task;
        }

        public void Block() => _open = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Release() => _open.TrySetResult();

        public Task<List<(string ConnectionId, string Message, object?[] Args)>> WaitForAsync(int count) =>
            WaitUntilAsync(items => items.Count >= count);

        public async Task<List<(string ConnectionId, string Message, object?[] Args)>> WaitUntilAsync(Func<List<(string ConnectionId, string Message, object?[] Args)>, bool> done)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);

            while (true)
            {
                lock (_sent)
                {
                    if (done(_sent))
                    {
                        return [.. _sent];
                    }
                }

                if (DateTime.UtcNow > deadline)
                {
                    throw new TimeoutException("O relay não entregou a tempo.");
                }

                await Task.Delay(10);
            }
        }

        private static TaskCompletionSource CompletedSource()
        {
            var source = new TaskCompletionSource();
            source.SetResult();
            return source;
        }
    }

    private sealed class RecordingLogger : ILogger<RemoteTerminalUseCases>
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Lines.Add(formatter(state, exception));
    }
}
