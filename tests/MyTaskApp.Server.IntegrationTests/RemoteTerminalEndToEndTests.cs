using System.Text;
using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Infrastructure.Remote;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// O terminal remoto de ponta a ponta (ADR-099), de computador para computador:
/// o notebook hospeda uma sessão do terminal do agente (aqui, com um
/// pseudoterminal falso no lugar do Claude) e aceita o terminal remoto; o
/// escritório, pareado, segue a sessão pelo relay do servidor de verdade, vê a
/// tela, assume a vez e digita. Sair não encerra nada lá.
/// </summary>
public sealed partial class RemoteOperationsEndToEndTests
{
    [Fact]
    public async Task AnotherDesktop_FollowsTheTerminalOfTheNotebook_AndTypesIntoTheSameSession()
    {
        var hosted = new HostedTerminal();
        var (notebook, office) = await TerminalPairAsync(hosted);
        await using var clocks = Clocks.Run(notebook, office);
        var (taskId, developmentId) = await ReadyEnvironmentAsync(notebook);
        var host = hosted.Start(taskId, developmentId);
        hosted.Pty.Print("Claude Code pronto\r\n> ");

        // O agente do notebook: a conexão do hub dele leva o terminal, e a lista sobe para a conta.
        var hub = notebook.Services.GetRequiredService<SignalRRemoteHub>();
        notebook.Services.GetRequiredService<RemoteTerminalBridge>();
        await hub.ConnectAsync(Ct);
        await notebook.Services.GetRequiredService<RemoteTerminalReporter>().PublishAsync(Ct);

        var list = await office.UseCaseAsync<ListRemoteTerminalsHandler, RemoteTerminalList>(
            (handler, cancel) => handler.HandleAsync(new ListRemoteTerminals(notebook.Device.DeviceId), cancel));
        var item = list.Items.Should().ContainSingle().Subject;
        item.Session.SessionId.Should().Be(host.SessionId);
        var target = item.Target(notebook.Device.DeviceId)!;

        // O escritório pede para seguir; o agente do notebook admite pela chave do pareamento.
        await using var channel = await office.Services.GetRequiredService<RemoteTerminalConnector>().ConnectAsync(target, new TerminalSize(100, 30), Ct);
        await notebook.Services.GetRequiredService<RemoteOperationDispatcher>().PollAsync(Ct);

        var mirror = new Mirror();
        await ReadUntilAsync(channel, mirror.Apply, () => mirror.Shows("Claude Code pronto") && mirror.Status is not null);

        var status = mirror.Status!;
        status.SessionId.Should().Be(host.SessionId);
        status.WorkingDirectory.Should().BeEmpty("a pasta não sai da máquina");
        status.YourClientId.Should().NotBeNull();
        host.Status.Clients.Should().ContainSingle(client => client.Kind == TerminalClientKind.Remote && client.Label == "Escritório");

        // A vez de digitar é pedida; depois, o teclado chega ao mesmo processo.
        await channel.SendAsync(new TerminalTakeControl(), Ct);
        await WaitForAsync(() => host.Status.Lease?.Kind == TerminalClientKind.Remote);
        await channel.SendAsync(new TerminalKeys("rode os testes\r"u8.ToArray()), Ct);
        await WaitForAsync(() => hosted.Pty.Input.Contains("rode os testes\r", StringComparison.Ordinal));

        hosted.Pty.Print("\r\n12 testes passaram\r\n");
        await ReadUntilAsync(channel, mirror.Apply, () => mirror.Shows("12 testes passaram"));

        // Sair é fechar a conexão: o notebook solta a vista, e o Claude segue rodando lá.
        await channel.DisposeAsync();
        await WaitForAsync(() => host.Status.Clients.Count == 0);
        host.Status.IsRunning.Should().BeTrue();
        hosted.Pty.Killed.Should().BeFalse();
        hosted.Pty.Closed.Should().BeFalse();

        await hub.DisconnectAsync(Ct);
        await host.DisposeAsync();
    }

    [Fact]
    public async Task WithTheTerminalSwitchOff_TheNotebookDoesNotOfferIt_AndRefusesTheAttach()
    {
        var hosted = new HostedTerminal();
        var (notebook, office) = await TerminalPairAsync(hosted, acceptTerminal: false);
        await using var clocks = Clocks.Run(notebook, office);
        var (taskId, developmentId) = await ReadyEnvironmentAsync(notebook);
        var host = hosted.Start(taskId, developmentId);

        var machines = await office.UseCaseAsync<GetMachinesHandler, MachinesView>((handler, cancel) => handler.HandleAsync(new GetMachines(), cancel));
        machines.Machines.Single(machine => machine.DeviceId == notebook.Device.DeviceId).OffersTerminal.Should().BeFalse();

        var hub = notebook.Services.GetRequiredService<SignalRRemoteHub>();
        notebook.Services.GetRequiredService<RemoteTerminalBridge>();
        await hub.ConnectAsync(Ct);

        // Mesmo pedido à mão, por quem não vê a lista: o agente recusa, e a vista fica sabendo.
        var target = new RemoteTerminalTarget(notebook.Device.DeviceId, host.SessionId, taskId, developmentId);
        await using var channel = await office.Services.GetRequiredService<RemoteTerminalConnector>().ConnectAsync(target, new TerminalSize(100, 30), Ct);
        await notebook.Services.GetRequiredService<RemoteOperationDispatcher>().PollAsync(Ct);

        var refusal = await ReadUntilAsync(channel, _ => { }, () => false, stopAt: message => message is TerminalRefusal);
        refusal.Should().BeOfType<TerminalRefusal>().Which.Text.Should().NotBeNullOrWhiteSpace();
        host.Status.Clients.Should().BeEmpty();

        await hub.DisconnectAsync(Ct);
        await host.DisposeAsync();
    }

    /// <summary>O notebook aceita controle (e o terminal, se pedido); o escritório pareia com ele pelo código, como na tela.</summary>
    private async Task<(SyncedDesktop Notebook, SyncedDesktop Office)> TerminalPairAsync(HostedTerminal hosted, bool acceptTerminal = true)
    {
        // O servidor em memória não fala WebSocket: os dois hubs vão por long polling.
        var transport = new RemoteHubTransport(() => _api.Server.CreateHandler(), HttpTransportType.LongPolling);

        var notebook = await SyncedDesktop.CreateAsync(_api, "Notebook", Ct, configure: services =>
        {
            services.AddSingleton(transport);
            services.RemoveAll<ITerminalSessions>();
            services.AddSingleton<ITerminalSessions>(hosted);
        });
        _desktops.Add(notebook);
        await notebook.UseCaseAsync<UpdateRemoteSettingsHandler, RemoteSettings>(
            (handler, cancel) => handler.HandleAsync(new UpdateRemoteSettings(Enabled: true, AcceptControl: true, AcceptTerminal: acceptTerminal), cancel));
        await notebook.ConnectAsync("ana", Ct);
        (await notebook.UseCaseAsync<RegisterThisMachineHandler, RemoteAvailability>((handler, cancel) => handler.HandleAsync(new RegisterThisMachine(), cancel)))
            .Should().Be(RemoteAvailability.Ready);

        var office = await SyncedDesktop.CreateAsync(_api, "Escritório", Ct, configure: services => services.AddSingleton(transport));
        _desktops.Add(office);
        await office.UseCaseAsync<UpdateRemoteSettingsHandler, RemoteSettings>(
            (handler, cancel) => handler.HandleAsync(new UpdateRemoteSettings(Enabled: true, AcceptControl: false), cancel));
        await office.ConnectAsync("ana", Ct);
        await office.UseCaseAsync<RegisterThisMachineHandler, RemoteAvailability>((handler, cancel) => handler.HandleAsync(new RegisterThisMachine(), cancel));

        var code = await office.UseCaseAsync<RequestPairingHandler, PairingCodeView>(
            (handler, cancel) => handler.HandleAsync(new RequestPairing(notebook.Device.DeviceId), cancel));
        await notebook.UseCaseAsync<ConfirmPairingHandler>((handler, cancel) => handler.HandleAsync(new ConfirmPairing(code.PairingId, code.Code), cancel));

        // A tarefa e o ambiente do notebook chegam ao escritório pelo sync, como na vida real.
        await notebook.SyncedAsync(Ct);
        return (notebook, office);
    }

    private static async Task<TerminalMessage?> ReadUntilAsync(ITerminalChannel channel, Action<TerminalMessage> read, Func<bool> done, Func<TerminalMessage, bool>? stopAt = null)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));

        while (!done())
        {
            var message = await channel.ReadAsync(timeout.Token);
            message.Should().NotBeNull("a conexão não devia ter acabado");
            read(message!);

            if (stopAt?.Invoke(message!) == true)
            {
                return message;
            }
        }

        return null;
    }

    /// <summary>
    /// Os relógios dos desktops de teste ficam parados (o token não vence deste
    /// lado); aqui eles andam, porque a ponte junta a saída numa janela de
    /// milissegundos e o canal relê o attach a cada segundo, ambos medidos neles.
    /// </summary>
    private sealed class Clocks : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();

        private readonly Task _ticking;

        private Clocks(FakeTimeProvider[] clocks) => _ticking = TickAsync(clocks, _stop.Token);

        public static Clocks Run(params SyncedDesktop[] desktops) =>
            new([.. desktops.Select(desktop => (FakeTimeProvider)desktop.Services.GetRequiredService<TimeProvider>())]);

        public async ValueTask DisposeAsync()
        {
            await _stop.CancelAsync();
            await _ticking;
            _stop.Dispose();
        }

        private static async Task TickAsync(FakeTimeProvider[] clocks, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                foreach (var clock in clocks)
                {
                    clock.Advance(TimeSpan.FromMilliseconds(20));
                }

                try
                {
                    await Task.Delay(10, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    /// <summary>A tela do escritório, montada como a vista monta: o retrato recomeça, a saída segue.</summary>
    private sealed class Mirror
    {
        private VtScreen? _screen;

        public TerminalSessionStatus? Status { get; private set; }

        public bool Shows(string text) => _screen?.ScreenText().Contains(text, StringComparison.Ordinal) == true;

        public void Apply(TerminalMessage message)
        {
            switch (message)
            {
                case TerminalOutput output when output.Reset:
                    _screen = new VtScreen(Status?.Size ?? new TerminalSize(100, 30));
                    _screen.Feed(output.Data);
                    break;
                case TerminalOutput output:
                    _screen?.Feed(output.Data);
                    break;
                case TerminalStateMessage state:
                    Status = state.Status;
                    break;
            }
        }
    }

    /// <summary>O terminal do agente do notebook, com uma sessão só e um pseudoterminal de mentira.</summary>
    private sealed class HostedTerminal : ITerminalSessions
    {
        private TerminalSessionHost? _host;

        public FakePty Pty { get; } = new();

        public bool IsHost => true;

        public PseudoTerminalSupport Support { get; } = new(true, "teste", null);

        public event EventHandler? Changed;

        public event EventHandler<Guid>? ActivityChanged
        {
            add { }
            remove { }
        }

        public TerminalSessionHost Start(Guid taskId, Guid developmentId)
        {
            _host = new TerminalSessionHost(
                new TerminalSessionInfo(Guid.CreateVersion7(), taskId, developmentId, @"C:\segredo\wt", Monitored: true, PermissionsBypassed: false),
                Pty,
                TimeProvider.System,
                NullLogger<TerminalSessionHost>.Instance);
            Changed?.Invoke(this, EventArgs.Empty);
            return _host;
        }

        public TerminalSessionHost? Find(Guid sessionId) => _host?.SessionId == sessionId ? _host : null;

        public IReadOnlyList<TerminalSessionHost> List() => _host is null ? [] : [_host];

        public Task<TerminalLaunchResult> LaunchAsync(TerminalLaunchOptions options, TerminalSessionInfo info, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public void Observe(AgentEvent agentEvent)
        {
        }

        public void NotifyActivity(Guid sessionId)
        {
        }
    }

    /// <summary>Um pseudoterminal de mentira: o teste escreve a saída e lê o que chegou como entrada.</summary>
    private sealed class FakePty : IPseudoTerminal
    {
        private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();

        private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly List<byte> _input = [];

        public int ProcessId => 4242;

        public DateTimeOffset ProcessStartedAt { get; } = DateTimeOffset.UtcNow;

        public TerminalSize Size { get; private set; } = new(80, 24);

        public Task<int?> Exited => _exited.Task;

        public bool Closed { get; private set; }

        public bool Killed { get; private set; }

        public string Input
        {
            get
            {
                lock (_input)
                {
                    return Encoding.UTF8.GetString([.. _input]);
                }
            }
        }

        public void Print(string text) => _output.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

        public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (!await _output.Reader.WaitToReadAsync(cancellationToken))
            {
                return 0;
            }

            var chunk = await _output.Reader.ReadAsync(cancellationToken);
            chunk.CopyTo(buffer);
            return chunk.Length;
        }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
        {
            if (_exited.Task.IsCompleted)
            {
                throw new DomainException("O terminal já fechou.");
            }

            lock (_input)
            {
                _input.AddRange(data.ToArray());
            }

            return ValueTask.CompletedTask;
        }

        public void Resize(TerminalSize size) => Size = size;

        public void Close()
        {
            Closed = true;
            End();
        }

        public void Kill()
        {
            Killed = true;
            End();
        }

        public ValueTask DisposeAsync()
        {
            End();
            return ValueTask.CompletedTask;
        }

        private void End()
        {
            _output.Writer.TryComplete();
            _exited.TrySetResult(0);
        }
    }
}
