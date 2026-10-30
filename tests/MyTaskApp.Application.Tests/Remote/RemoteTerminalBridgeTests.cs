using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Application.Tests.Terminals;
using MyTaskApp.Domain.Remote;
using MyTaskApp.Remote.Contracts;
using NSubstitute;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>
/// O agente no terminal remoto (ADR-099): a conexão de longe é mais um cliente
/// da mesma sessão; a entrada só passa assinada pela chave da conexão, em
/// ordem, com a chave da máquina ligada e o controlador ainda pareado.
/// </summary>
public sealed class RemoteTerminalBridgeTests : IAsyncDisposable
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero));

    private readonly FakePseudoTerminal _pty = new();

    private readonly FakeRemoteSettingsStore _settings = new(new RemoteSettings(true, true, AcceptTerminal: true));

    private readonly FakeTrustedControllerRepository _trusted = new();

    private readonly FakeDeviceIdentity _identity = new();

    private readonly FakeDeviceSigner _signer = new();

    private readonly FakeLink _link = new();

    private readonly ECDsa _viewer = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private readonly Guid _controller = Guid.CreateVersion7();

    private readonly TerminalSessionHost _host;

    private readonly HostedSessions _terminals;

    private readonly ServiceProvider _services;

    private readonly RemoteTerminalBridge _bridge;

    private long _sequence;

    public RemoteTerminalBridgeTests()
    {
        _host = new TerminalSessionHost(
            new TerminalSessionInfo(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), @"C:\segredo\wt", Monitored: true, PermissionsBypassed: true),
            _pty,
            _time,
            NullLogger<TerminalSessionHost>.Instance);
        _terminals = new HostedSessions(_host);
        var controllerKey = DevicePublicKey.Es256Of(_viewer.ExportSubjectPublicKeyInfo());
        _trusted.All.Add(TrustedController.Approve(_controller, "Notebook", controllerKey.Fingerprint, controllerKey.SubjectPublicKeyInfo, Guid.CreateVersion7(), _time.GetUtcNow()));

        _services = new ServiceCollection()
            .AddSingleton<IRemoteSettingsStore>(_settings)
            .AddSingleton<ITrustedControllerRepository>(_trusted)
            .AddSingleton<IDeviceIdentity>(_identity)
            .AddSingleton<IDeviceSigner>(_signer)
            .AddScoped<RemoteTrust>()
            .AddScoped<CheckRemoteTerminalAccessHandler>()
            .AddSingleton<IUseCaseRunner, ScopedUseCaseRunner>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        _bridge = new RemoteTerminalBridge(_terminals, _link, Runner, _time, NullLogger<RemoteTerminalBridge>.Instance);
    }

    private IUseCaseRunner Runner => _services.GetRequiredService<IUseCaseRunner>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask DisposeAsync()
    {
        _bridge.Dispose();
        await _host.DisposeAsync();
        await _services.DisposeAsync();
        _viewer.Dispose();
        _signer.Dispose();
    }

    [Fact]
    public async Task FromAfar_TheConnection_SeesTheSameScreen_AndTheStateWithoutTheFolder()
    {
        _pty.Print("olá de longe\r\n");
        var attach = await OpenAsync();

        var frames = await _link.WaitAsync(this, attach, all => all.Any(frame => frame.Kind == RemoteTerminalFrameKinds.State) && Text(all).Contains("olá de longe", StringComparison.Ordinal));

        frames.First(frame => frame.Kind == RemoteTerminalFrameKinds.Output).Reset.Should().BeTrue("quem chega começa pelo retrato");
        var state = frames.Last(frame => frame.Kind == RemoteTerminalFrameKinds.State).State!;
        state.SessionId.Should().Be(_host.SessionId);
        state.PermissionsBypassed.Should().BeTrue("o controlador mostra o aviso");
        state.Viewers.Should().ContainSingle(viewer => viewer.Kind == "remote" && viewer.Label == "Notebook");
        state.YourViewerId.Should().Be(state.Viewers.Single().Id);
        JsonSerializer.Serialize(state, RemoteJson.Options).Should().NotContain("segredo", "a pasta não sai da máquina");
    }

    [Fact]
    public async Task SignedKeys_WithControl_ReachTheTerminal()
    {
        var attach = await OpenAsync();

        await SendAsync(attach, new RemoteTerminalAction(RemoteTerminalActions.TakeControl));
        await SendAsync(attach, Keys("ls\r"));

        _pty.Input.Should().Be("ls\r");
        _host.Status.Lease!.Kind.Should().Be(TerminalClientKind.Remote);
    }

    [Fact]
    public async Task ARepeatedBatch_IsRefused_AndTypesNothingTwice()
    {
        var attach = await OpenAsync();
        await SendAsync(attach, new RemoteTerminalAction(RemoteTerminalActions.TakeControl));
        var keys = Sign(attach, ++_sequence, Keys("x"));

        await _bridge.HandleInputAsync(attach, keys);
        await _bridge.HandleInputAsync(attach, keys);

        _pty.Input.Should().Be("x");
        _link.Frames(attach).Should().Contain(frame => frame.Kind == RemoteTerminalFrameKinds.Refusal && frame.Text!.Contains("repetida"));
    }

    [Fact]
    public async Task KeysSignedByAnotherKey_AreRefused()
    {
        var attach = await OpenAsync();
        using var intruder = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        await SendAsync(attach, new RemoteTerminalAction(RemoteTerminalActions.TakeControl));

        await _bridge.HandleInputAsync(attach, Sign(attach, ++_sequence, Keys("rm -rf /\r"), intruder));

        _pty.Input.Should().BeEmpty();
        _link.Frames(attach).Should().Contain(frame => frame.Kind == RemoteTerminalFrameKinds.Refusal);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task SwitchingOffOrUnpairing_InTheMiddle_RefusesTheNextBatch_AndEndsTheConnection(bool acceptTerminal, bool paired)
    {
        var attach = await OpenAsync();
        await SendAsync(attach, new RemoteTerminalAction(RemoteTerminalActions.TakeControl));

        _settings.Settings = new RemoteSettings(true, true, acceptTerminal);

        if (!paired)
        {
            _trusted.All.Clear();
        }

        _time.Advance(RemoteTerminalBridge.AccessLifetime + TimeSpan.FromMilliseconds(1));
        await SendAsync(attach, Keys("ls\r"));

        _pty.Input.Should().BeEmpty();
        _link.Closed.Should().ContainKey(attach);
        _bridge.Count.Should().Be(0);
        _host.Status.Clients.Should().BeEmpty("o cliente de longe saiu da sessão");
        _host.IsRunning.Should().BeTrue("a sessão continua: só a conexão acabou");
    }

    [Fact]
    public async Task WhenTheFollowerLeaves_OnlyTheConnectionEnds()
    {
        var attach = await OpenAsync();

        _link.Detach(attach);
        await WaitUntil(() => _bridge.Count == 0);

        _host.Status.Clients.Should().BeEmpty();
        _host.IsRunning.Should().BeTrue();
        _pty.Closed.Should().BeFalse();
        _link.Closed.Should().NotContainKey(attach, "quem saiu já sabe");
    }

    [Fact]
    public async Task WhenTheLinkToTheServerDrops_EveryConnectionEnds_AndTheSessionStays()
    {
        await OpenAsync();
        await OpenAsync();

        _link.Lose();
        await WaitUntil(() => _bridge.Count == 0);

        _host.Status.Clients.Should().BeEmpty();
        _host.IsRunning.Should().BeTrue();
    }

    [Fact]
    public async Task WhenTheSessionEnds_TheFollowerHearsItClosed()
    {
        var attach = await OpenAsync();

        _pty.Exit(0);

        // Saiu o processo: quem segue vê o fim, e a tela fica até o agente largar a sessão.
        await _link.WaitAsync(this, attach, all => all.Any(frame => frame.State?.Process == RemoteTerminalProcess.Exited));
        _link.Closed.Should().NotContainKey(attach);

        await _host.DisposeAsync();
        await WaitUntil(() => _link.Closed.ContainsKey(attach));
        _link.Closed[attach].Should().Contain("terminou");
    }

    [Fact]
    public async Task ABigOutput_GoesInPiecesUnderTheCeiling_InOrder()
    {
        var attach = await OpenAsync();
        await _link.WaitAsync(this, attach, all => all.Any(frame => frame.Kind == RemoteTerminalFrameKinds.Output));

        for (var piece = 0; piece < 16; piece++)
        {
            _pty.Print(new string('a', 4096));
        }

        _pty.Print(new string('a', 100));

        var pieces = (await _link.WaitAsync(this, attach, all => all.Where(frame => frame is { Kind: RemoteTerminalFrameKinds.Output, Reset: false }).Sum(frame => frame.Data!.Length) >= (16 * 4096) + 100))
            .Where(frame => frame is { Kind: RemoteTerminalFrameKinds.Output, Reset: false })
            .ToList();

        pieces.Should().AllSatisfy(frame => frame.Data!.Length.Should().BeLessThanOrEqualTo(RemoteLimits.MaxTerminalFrameData));
        pieces.Count.Should().BeLessThan(17, "o que chega junto vai junto");
        pieces.Zip(pieces.Skip(1)).Should().AllSatisfy(pair => pair.Second.Offset.Should().Be(pair.First.Offset + pair.First.Data!.Length));
    }

    /// <summary>O retrato passa de um pedaço: só o primeiro recomeça a tela, todos levam o deslocamento dele, e só o último fecha.</summary>
    [Fact]
    public async Task ABigSnapshot_GoesInPieces_AllAtTheSnapshotsOffset()
    {
        // Cor a cada letra: o retrato da tela e do histórico passa de 32 KiB.
        var line = string.Concat(Enumerable.Range(0, 40).Select(index => $"\u001b[3{(index % 6) + 1}m{(char)('a' + (index % 26))}")) + "\r\n";

        for (var row = 0; row < 400; row++)
        {
            _pty.Print(line);
        }

        _pty.Print("fim\r\n");
        var first = await OpenAsync();
        await _link.WaitAsync(this, first, all => Text(all).Contains("fim", StringComparison.Ordinal));

        var second = await OpenAsync();
        var output = (await _link.WaitAsync(this, second, all => all.Any(frame => frame is { Kind: RemoteTerminalFrameKinds.Output, More: false })))
            .Where(frame => frame.Kind == RemoteTerminalFrameKinds.Output)
            .ToList();
        var snapshot = output.Take(output.FindIndex(frame => !frame.More) + 1).ToList();

        snapshot.Count.Should().BeGreaterThan(1);
        snapshot.Select(frame => frame.Reset).Should().Equal([true, .. Enumerable.Repeat(false, snapshot.Count - 1)]);
        snapshot.Should().AllSatisfy(frame => frame.Data!.Length.Should().BeLessThanOrEqualTo(RemoteLimits.MaxTerminalFrameData));
        snapshot.Select(frame => frame.Offset).Distinct().Should().ContainSingle();
        snapshot.Select(frame => frame.More).Should().Equal([.. Enumerable.Repeat(true, snapshot.Count - 1), false]);
    }

    [Fact]
    public async Task AnotherSessionOrEnvironment_IsNotAConnection()
    {
        var unknown = await _bridge.OpenAsync(Execution(Guid.CreateVersion7(), _host.Info.DevelopmentId!.Value), Ct);
        var otherEnvironment = await _bridge.OpenAsync(Execution(_host.SessionId, Guid.CreateVersion7()), Ct);

        unknown.Succeeded.Should().BeFalse();
        otherEnvironment.Succeeded.Should().BeFalse();
        _host.Status.Clients.Should().BeEmpty();
    }

    [Fact]
    public async Task TheExecutors_RefuseWithTheSwitchOff_OrWithoutTheAgentsTerminal()
    {
        var attach = new TerminalAttachExecutor(Runner, _terminals, _bridge);
        var request = Execution(_host.SessionId, _host.Info.DevelopmentId!.Value).Request;

        (await attach.CheckAsync(request, Ct)).Should().BeNull();

        _settings.Settings = new RemoteSettings(true, true, AcceptTerminal: false);
        (await attach.CheckAsync(request, Ct))!.Code.Should().Be(RemoteErrorCodes.TerminalNotAccepted);

        _settings.Settings = new RemoteSettings(true, true, AcceptTerminal: true);
        var desktop = new TerminalAttachExecutor(Runner, NoTerminalSessions.Instance, _bridge);
        (await desktop.CheckAsync(request, Ct))!.Code.Should().Be(RemoteErrorCodes.TerminalNotAccepted);
    }

    [Theory]
    [InlineData(RemoteTerminalStopMode.Graceful)]
    [InlineData(RemoteTerminalStopMode.Force)]
    public async Task Stopping_ClosesOrKills_OnlyThatSession(RemoteTerminalStopMode mode)
    {
        var stop = new TerminalStopExecutor(Runner, _terminals);
        var request = RequestOf(RemoteOperationKinds.TerminalStop, new RemoteTerminalStopRequest(_host.SessionId, mode));

        var result = await stop.ExecuteAsync(new RemoteExecution(request, "Notebook", (_, _) => Task.CompletedTask, _time), Ct);

        result.Succeeded.Should().BeTrue();
        (mode == RemoteTerminalStopMode.Force ? _pty.Killed : _pty.Closed).Should().BeTrue();
        (mode == RemoteTerminalStopMode.Force ? _pty.Closed : _pty.Killed).Should().BeFalse("fechar e forçar são coisas diferentes");
    }

    [Fact]
    public async Task TheReport_HasTheSessions_OnlyWithTheSwitchOn()
    {
        var api = Substitute.For<IRemoteTerminalsApi>();
        var batches = new List<RemoteTerminalSessionBatch>();
        api.ReportTerminalsAsync(Arg.Do<RemoteTerminalSessionBatch>(batches.Add), Arg.Any<CancellationToken>()).Returns(new RemoteTerminalSessionAck(0));
        using var reporter = new RemoteTerminalReporter(api, _terminals, Runner, _time, NullLogger<RemoteTerminalReporter>.Instance);

        await reporter.PublishAsync(Ct);
        _settings.Settings = new RemoteSettings(true, true, AcceptTerminal: false);
        await reporter.PublishAsync(Ct);

        batches.Should().HaveCount(2);
        batches[0].Sessions.Should().ContainSingle().Which.Should().Match<RemoteTerminalSessionReport>(report =>
            report.SessionId == _host.SessionId && report.PermissionsBypassed && report.Process == RemoteTerminalProcess.Running);
        batches[1].Sessions.Should().BeEmpty("sem a chave, a lista dos controladores esvazia");
    }

    [Fact]
    public void TheRegistration_OffersTheTerminalKinds_OnlyWhenTheyAreOffered()
    {
        IReadOnlyList<string> kinds = [RemoteOperationKinds.EnvironmentInspect, .. RemoteOperationKinds.Terminal];

        TerminalKinds.Filter(kinds, offered: true).Should().Equal(kinds);
        TerminalKinds.Filter(kinds, offered: false).Should().Equal(RemoteOperationKinds.EnvironmentInspect);
    }

    private async Task<Guid> OpenAsync()
    {
        var execution = Execution(_host.SessionId, _host.Info.DevelopmentId!.Value);
        var result = await _bridge.OpenAsync(execution, Ct);
        result.Succeeded.Should().BeTrue(result.Summary);
        return execution.Request.OperationId;
    }

    private RemoteExecution Execution(Guid sessionId, Guid developmentId) =>
        new(
            RequestOf(
                RemoteOperationKinds.TerminalAttach,
                new RemoteTerminalAttachRequest(sessionId, developmentId, DevicePublicKey.Es256Of(_viewer.ExportSubjectPublicKeyInfo()), 100, 30)),
            "Notebook",
            (_, _) => Task.CompletedTask,
            _time);

    private RemoteOperationRequest RequestOf<T>(string kind, T payload)
    {
        var now = _time.GetUtcNow();
        return new RemoteOperationRequest(
            Guid.CreateVersion7(),
            Guid.CreateVersion7(),
            _identity.DeviceId,
            _controller,
            kind,
            JsonSerializer.SerializeToElement(payload, RemoteJson.Options),
            _host.Info.TaskId,
            [],
            now,
            now.AddMinutes(10));
    }

    private Task SendAsync(Guid attach, RemoteTerminalAction action) =>
        _bridge.HandleInputAsync(attach, Sign(attach, ++_sequence, action));

    private SignedEnvelope Sign(Guid attach, long sequence, RemoteTerminalAction action, ECDsa? key = null)
    {
        key ??= _viewer;
        var input = new RemoteTerminalInput(attach, _identity.DeviceId, _host.SessionId, sequence, _time.GetUtcNow(), action);
        var signingInput = RemoteJws.SigningInput(input, DevicePublicKey.Es256Of(key.ExportSubjectPublicKeyInfo()).Fingerprint, RemoteJws.TerminalInputType);
        var signature = key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return RemoteJws.Compose(signingInput, Base64Url.EncodeToString(signature));
    }

    private static RemoteTerminalAction Keys(string text) => new(RemoteTerminalActions.Keys, Keys: Encoding.UTF8.GetBytes(text));

    private static string Text(IEnumerable<RemoteTerminalFrame> frames) =>
        Encoding.UTF8.GetString([.. frames.Where(frame => frame.Kind == RemoteTerminalFrameKinds.Output).SelectMany(frame => frame.Data!)]);

    /// <summary>Espera, andando o relógio falso: a saída espera a janela de juntar.</summary>
    internal async Task WaitUntil(Func<bool> done)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (!done())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Não aconteceu a tempo.");
            }

            _time.Advance(TimeSpan.FromMilliseconds(20));
            await Task.Delay(10, Ct);
        }
    }

    private sealed class FakeLink : IRemoteTerminalLink
    {
        private readonly ConcurrentQueue<RemoteTerminalFrame> _frames = new();

        public event Action<Guid, SignedEnvelope>? InputReceived;

        public event Action<Guid>? Detached;

        public event Action? Lost;

        public ConcurrentDictionary<Guid, string> Closed { get; } = new();

        public List<RemoteTerminalFrame> Frames(Guid attach) => [.. _frames.Where(frame => frame.AttachId == attach)];

        public async Task<List<RemoteTerminalFrame>> WaitAsync(RemoteTerminalBridgeTests test, Guid attach, Func<List<RemoteTerminalFrame>, bool> done)
        {
            await test.WaitUntil(() => done(Frames(attach)));
            return Frames(attach);
        }

        public Task PublishAsync(RemoteTerminalFrame frame, CancellationToken cancellationToken)
        {
            _frames.Enqueue(frame);
            return Task.CompletedTask;
        }

        public Task CloseAsync(Guid attachId, string reason, CancellationToken cancellationToken)
        {
            Closed[attachId] = reason;
            return Task.CompletedTask;
        }

        public void Detach(Guid attach) => Detached?.Invoke(attach);

        public void Lose() => Lost?.Invoke();

        public void Type(Guid attach, SignedEnvelope input) => InputReceived?.Invoke(attach, input);
    }

    private sealed class HostedSessions(TerminalSessionHost host) : ITerminalSessions
    {
        public bool IsHost => true;

        public PseudoTerminalSupport Support { get; } = new(true, "teste", null);

        public event EventHandler? Changed
        {
            add { }
            remove { }
        }

        public event EventHandler<Guid>? ActivityChanged
        {
            add { }
            remove { }
        }

        public TerminalSessionHost? Find(Guid sessionId) => sessionId == host.SessionId ? host : null;

        public IReadOnlyList<TerminalSessionHost> List() => [host];

        public Task<TerminalLaunchResult> LaunchAsync(TerminalLaunchOptions options, TerminalSessionInfo info, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Observe(AgentEvent agentEvent)
        {
        }

        public void NotifyActivity(Guid sessionId)
        {
        }
    }
}
