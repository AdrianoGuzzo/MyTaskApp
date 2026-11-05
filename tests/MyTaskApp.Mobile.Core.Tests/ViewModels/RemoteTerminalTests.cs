using System.Text;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.Tests.Fakes;
using MyTaskApp.Mobile.Core.ViewModels;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Mobile.Core.Tests.ViewModels;

/// <summary>
/// O terminal do Claude no celular (ADR-101): a lista das sessões das
/// máquinas na aba Remoto, a tela ao vivo, a vez de digitar pedida por botão,
/// as teclas que o teclado do celular não tem, e a ligação que cai em segundo
/// plano e volta sozinha.
/// </summary>
public sealed class RemoteTerminalTests : IAsyncLifetime
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(10);

    private static readonly Guid Notebook = Guid.CreateVersion7();

    private static readonly Guid Desktop = Guid.CreateVersion7();

    private MobileHarness _app = null!;

    private FakeRemoteServer Server => _app.Get<FakeRemoteServer>();

    private FakeConnector Connector => _app.Get<FakeConnector>();

    public async ValueTask InitializeAsync()
    {
        _app = await MobileHarness.CreateAsync(configure: services =>
        {
            services.AddSingleton(provider => new FakeRemoteServer(provider.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IRemoteApi>(provider => provider.GetRequiredService<FakeRemoteServer>());
            services.AddSingleton<IRemoteOperationsApi>(provider => provider.GetRequiredService<FakeRemoteServer>());
            services.AddSingleton<IRemoteSessionsApi>(provider => provider.GetRequiredService<FakeRemoteServer>());
            services.AddSingleton<IRemoteTerminalsApi>(provider => provider.GetRequiredService<FakeRemoteServer>());
            services.AddSingleton<FakeConnector>();
            services.AddSingleton<IRemoteTerminalConnector>(provider => provider.GetRequiredService<FakeConnector>());
        });

        await using (var scope = _app.Services.CreateAsyncScope())
        {
            var device = scope.ServiceProvider.GetRequiredService<IDeviceIdentity>().DeviceId;
            await scope.ServiceProvider.GetRequiredService<ISyncLocalStore>().LinkAsync(
                new SyncAccountLink(Guid.CreateVersion7(), "Ana", null, Guid.CreateVersion7(), device, new Uri("https://sync.exemplo.com/")),
                TestContext.Current.CancellationToken);
        }

        Server.Devices.Add(WithTerminal(FakeRemoteServer.Executor(Notebook, "Notebook", MobileHarness.Now)));
        Server.Devices.Add(FakeRemoteServer.Executor(Desktop, "Desktop", MobileHarness.Now));
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    /// <summary>A sessão aberta pelo card no notebook aparece na aba, com a máquina e o estado; a máquina sem o terminal remoto, não.</summary>
    [Fact]
    public async Task TheTab_ListsTheRunningClaudes_OfTheMachinesThatAcceptTheTerminal()
    {
        var waiting = Report(hasDialog: true);
        Server.Terminals.Add(new RemoteTerminalSessionView(Notebook, waiting, MobileHarness.Now));
        Server.Terminals.Add(new RemoteTerminalSessionView(Notebook, Report() with { Process = RemoteTerminalProcess.Exited }, MobileHarness.Now));
        Server.Terminals.Add(new RemoteTerminalSessionView(Desktop, Report(), MobileHarness.Now));
        var remote = _app.Get<RemoteViewModel>();

        await remote.EnableAsync();

        var line = remote.Terminals.Should().ContainSingle("o encerrado e o da máquina sem o terminal remoto ficam de fora").Subject;
        line.Detail.Should().Be("Notebook · ◐ Esperando resposta");
        line.NeedsAttention.Should().BeTrue();
        remote.TerminalsNeedingAttention.Should().Be(1);

        await remote.OpenTerminalAsync(line);
        _app.Navigator.Last.Should().Be($"terminal/{waiting.SessionId}");
    }

    [Fact]
    public async Task Following_ShowsTheScreen_AndFromAfarOnlyWatches_UntilTakingControl()
    {
        var session = Report();
        Server.Terminals.Add(new RemoteTerminalSessionView(Notebook, session, MobileHarness.Now));
        var terminal = await OpenAsync(session);

        Connector.Targets.Should().ContainSingle().Which.Should().Be(new RemoteTerminalTarget(Notebook, session.SessionId, session.TaskId, session.DevelopmentId!.Value));
        terminal.Machine.Should().Be("Notebook");

        var channel = Connector.Channels[^1];
        channel.Snapshot("olá do Claude\r\n");
        var you = Guid.NewGuid();
        channel.Send(new TerminalStateMessage(FakeTerminalChannel.Status(activity: AgentActivity.WaitingReview) with { YourClientId = you }));
        await WaitUntil(() => terminal.Screen?.ScreenText().Contains("olá do Claude", StringComparison.Ordinal) == true && terminal.CanTakeControl);

        terminal.CanType.Should().BeFalse("de longe, sem a vez, a tela só mostra");
        await terminal.PressAsync("esc");
        await terminal.TypeTextAsync("x");
        Sent<TerminalKeys>(channel).Should().BeEmpty();

        await terminal.TakeControlAsync();
        Sent<TerminalTakeControl>(channel).Should().ContainSingle();
        _app.Dialogs.Questions.Should().BeEmpty("com as aprovações do Claude, assumir não pergunta");

        channel.Send(new TerminalStateMessage(FakeTerminalChannel.Status(
            activity: AgentActivity.WaitingReview,
            lease: new TerminalLease(you, TerminalClientKind.Remote, "Pixel de teste", DateTimeOffset.UnixEpoch)) with { YourClientId = you }));
        await WaitUntil(() => terminal.HasControl);

        terminal.CanType.Should().BeTrue();
        terminal.LeaseText.Should().StartWith("Você está com o controle");

        await terminal.TypeTextAsync("oi\n");
        await terminal.PressAsync("ctrl-c");
        await terminal.PressAsync("shift-tab");
        await terminal.BackspaceAsync();
        Sent<TerminalKeys>(channel).Select(keys => Encoding.UTF8.GetString(keys.Data)).Should().Equal("oi\r", "\x03", "\x1b[Z", "\x7f");

        terminal.Prompt = "Rode os testes";
        terminal.CanPrompt.Should().BeTrue();
        await terminal.SendPromptAsync();
        Sent<TerminalPrompt>(channel).Should().ContainSingle().Which.Text.Should().Be("Rode os testes");
        terminal.Prompt.Should().BeEmpty();

        await terminal.ReleaseControlAsync();
        Sent<TerminalReleaseControl>(channel).Should().ContainSingle();
    }

    [Fact]
    public async Task ThePermissionDialog_IsAnswered_FromThePhone()
    {
        var session = Report();
        Server.Terminals.Add(new RemoteTerminalSessionView(Notebook, session, MobileHarness.Now));
        var terminal = await OpenAsync(session);
        var channel = Connector.Channels[^1];
        var dialog = new TerminalDialog(Guid.NewGuid(), TerminalDialogKind.Permission, "Bash: npm test", DateTimeOffset.UnixEpoch);

        channel.Send(new TerminalStateMessage(FakeTerminalChannel.Status(activity: AgentActivity.WaitingForUser, dialog: dialog)));
        await WaitUntil(() => terminal.CanAnswer);

        terminal.DialogText.Should().Be("Bash: npm test");
        await terminal.AllowAsync();
        Sent<TerminalChoice>(channel).Should().ContainSingle().Which.Should().Be(new TerminalChoice(dialog.Id, true));
    }

    [Fact]
    public async Task ASessionWithoutApprovals_AsksBeforeTakingControl()
    {
        var session = Report() with { PermissionsBypassed = true };
        Server.Terminals.Add(new RemoteTerminalSessionView(Notebook, session, MobileHarness.Now));
        var terminal = await OpenAsync(session);
        var channel = Connector.Channels[^1];
        channel.Send(new TerminalStateMessage(FakeTerminalChannel.Status(bypassed: true) with { YourClientId = Guid.NewGuid() }));
        await WaitUntil(() => terminal.HasBypassWarning);

        _app.Dialogs.Confirms = false;
        await terminal.TakeControlAsync();

        _app.Dialogs.Questions.Should().ContainSingle().Which.Should().Contain("sem as aprovações");
        Sent<TerminalTakeControl>(channel).Should().BeEmpty();
    }

    /// <summary>Em segundo plano a ligação é solta, sem encerrar nada lá; voltar liga de novo, com outra chave.</summary>
    [Fact]
    public async Task GoingToTheBackground_DropsTheConnection_AndComingBackReconnects()
    {
        var session = Report();
        Server.Terminals.Add(new RemoteTerminalSessionView(Notebook, session, MobileHarness.Now));
        var terminal = await OpenAsync(session);
        var first = Connector.Channels[^1];

        await _app.Get<MobileLifecycle>().SleepAsync();
        await WaitUntil(() => terminal.IsDisconnected);

        first.Disposed.Should().BeTrue();
        first.Stops.Should().BeEmpty("soltar a vista não encerra o Claude");
        terminal.Message.Should().Contain("segundo plano");

        _app.Get<MobileLifecycle>().Resume();
        await WaitUntil(() => terminal.IsConnected);
        Connector.Channels.Should().HaveCount(2);

        await terminal.CloseAsync();
        _app.Get<MobileLifecycle>().Resume();
        Connector.Channels.Should().HaveCount(2, "a tela fechada não liga de novo");
    }

    /// <summary>Cancelar o destravamento não liga, e a tela oferece tentar de novo.</summary>
    [Fact]
    public async Task NotUnlocking_LeavesTheScreenDisconnected_WithTheReason()
    {
        var session = Report();
        Server.Terminals.Add(new RemoteTerminalSessionView(Notebook, session, MobileHarness.Now));
        Connector.FailWith = new DomainException("Nada foi enviado: o celular não foi destravado.");
        var terminal = _app.Get<RemoteTerminalViewModel>();

        await terminal.OpenAsync(Notebook, session.SessionId);

        terminal.IsDisconnected.Should().BeTrue();
        terminal.Message.Should().Be("Nada foi enviado: o celular não foi destravado.");

        Connector.FailWith = null;
        await terminal.ReconnectAsync();
        terminal.IsConnected.Should().BeTrue();
    }

    [Fact]
    public async Task ASessionThatIsGone_SaysSo()
    {
        var terminal = _app.Get<RemoteTerminalViewModel>();

        await terminal.OpenAsync(Notebook, Guid.CreateVersion7());

        terminal.HasLoadError.Should().BeTrue();
        terminal.LoadError.Should().Contain("não está mais");
        Connector.Targets.Should().BeEmpty();
    }

    /// <summary>Da tela da máquina: os terminais dela e "No terminal de lá", que manda só os ids.</summary>
    [Fact]
    public async Task TheMachine_ListsItsTerminals_AndOpensTheClaudeInTheAgentsTerminalThere()
    {
        var task = Guid.CreateVersion7();
        var development = Guid.CreateVersion7();
        Server.Catalogs[Notebook] = new AgentCatalog("hash", [], [], [], []) { Environments = [new CatalogEnvironment(task, development, [])] };
        Server.Terminals.Add(new RemoteTerminalSessionView(Notebook, Report(), MobileHarness.Now));
        await _app.Get<RemoteViewModel>().EnableAsync();
        var machine = _app.Get<RemoteMachineViewModel>();

        await machine.LoadAsync(Notebook);

        machine.OffersTerminal.Should().BeTrue();
        machine.Terminals.Should().ContainSingle();
        await machine.StartTerminalAsync(machine.Environments.Should().ContainSingle().Subject);

        var request = Server.Requests[^1];
        request.Kind.Should().Be(RemoteOperationKinds.TerminalStart);
        _app.Dialogs.Toasts.Should().Contain(toast => toast.Contains("Terminais do Claude", StringComparison.Ordinal));
        _app.Navigator.Visits.Should().NotContain(visit => visit.StartsWith("operation/", StringComparison.Ordinal), "o Claude aparece na lista dos terminais, não numa operação");
    }

    private static RemoteDeviceInfo WithTerminal(RemoteDeviceInfo device) =>
        device with { Capabilities = device.Capabilities! with { Operations = [RemoteOperationKinds.TerminalStart, RemoteOperationKinds.TerminalAttach, RemoteOperationKinds.TerminalStop] } };

    private static RemoteTerminalSessionReport Report(bool hasDialog = false) =>
        new(
            Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), RemoteTerminalProcess.Running, null,
            RemoteAgentActivity.Unknown, null, hasDialog, true, false, false, MobileHarness.Now, MobileHarness.Now);

    private static List<T> Sent<T>(FakeTerminalChannel channel)
        where T : TerminalInput
    {
        lock (channel.Sent)
        {
            return [.. channel.Sent.OfType<T>()];
        }
    }

    private async Task<RemoteTerminalViewModel> OpenAsync(RemoteTerminalSessionReport session)
    {
        await _app.Get<RemoteViewModel>().EnableAsync();
        var terminal = _app.Get<RemoteTerminalViewModel>();
        await terminal.OpenAsync(Notebook, session.SessionId);
        terminal.IsConnected.Should().BeTrue();
        return terminal;
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow + Patience;

        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("A tela não chegou lá a tempo.");
            }

            await Task.Delay(10, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>A ligação de mentira: guarda o alvo e devolve um canal que o teste alimenta.</summary>
    private sealed class FakeConnector : IRemoteTerminalConnector
    {
        public List<RemoteTerminalTarget> Targets { get; } = [];

        public List<FakeTerminalChannel> Channels { get; } = [];

        public Exception? FailWith { get; set; }

        public Task<ITerminalChannel> ConnectAsync(RemoteTerminalTarget target, TerminalSize size, CancellationToken cancellationToken)
        {
            if (FailWith is { } failure)
            {
                return Task.FromException<ITerminalChannel>(failure);
            }

            Targets.Add(target);
            var channel = new FakeTerminalChannel();
            Channels.Add(channel);
            return Task.FromResult<ITerminalChannel>(channel);
        }
    }
}
