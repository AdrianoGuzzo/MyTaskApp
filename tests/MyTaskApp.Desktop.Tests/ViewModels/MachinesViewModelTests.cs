using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A janela "Máquinas" (fase 2, T-06): o que ela mostra, que ligar o remoto e
/// aceitar controle passam pela pessoa, e o pareamento dos dois lados — sem
/// servidor e sem rede.
/// </summary>
public class MachinesViewModelTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 24, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid ThisMachine = Guid.Parse("0199a000-0000-7000-8000-000000000001");

    private static readonly Guid Notebook = Guid.Parse("0199a000-0000-7000-8000-000000000002");

    private readonly FakeUseCaseRunner _runner = new();

    private readonly FakeAccount _account = new();

    private readonly FakeShellLauncher _shell = new();

    private readonly FakeConfirmationDialog _confirmation = new() { Answer = true };

    private readonly SettingsStore _settings = new();

    public MachinesViewModelTests()
    {
        _runner.Handlers[typeof(UpdateRemoteSettingsHandler)] =
            new UpdateRemoteSettingsHandler(_settings, _settings, new NoAgentAutostart(), NullLogger<UpdateRemoteSettingsHandler>.Instance);
        _runner.ResultsByHandler[typeof(RegisterThisMachineHandler)] = RemoteAvailability.Ready;
        Show(View(new RemoteSettings(true, false)));
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private MachinesViewModel ViewModel() =>
        new(
            _runner,
            _account,
            new RemotePresenceScheduler(_runner, TimeProvider.System, NullLogger<RemotePresenceScheduler>.Instance),
            _shell,
            _confirmation,
            new FakeTimeProvider(Now),
            () => new RemoteControlViewModel(_runner, NullLogger<RemoteControlViewModel>.Instance),
            NullLogger<MachinesViewModel>.Instance);

    private void Show(MachinesView view) => _runner.ResultsByHandler[typeof(GetMachinesHandler)] = view;

    private static MachinesView View(
        RemoteSettings settings,
        RemoteAvailability availability = RemoteAvailability.Ready,
        string? problem = null,
        IReadOnlyList<OpenPairingView>? pairings = null,
        IReadOnlyList<TrustedControllerView>? trusted = null) =>
        new(
            settings,
            availability,
            problem,
            availability == RemoteAvailability.Ready
                ?
                [
                    new MachineView(ThisMachine, "Desktop", "windows", "1.25.0", true, true, Now, false, "aaaabbbbccccdddd" + new string('0', 48), ["Git 2.51.0"]),
                    new MachineView(Notebook, "Notebook", "linux", "1.25.0", false, false, Now.AddMinutes(-7), true, "1111222233334444" + new string('0', 48), []),
                    new MachineView(Guid.CreateVersion7(), "Celular", "android", "1.25.0", false, false, null, false, null, []),
                ]
                : [],
            pairings ?? [],
            trusted ?? []);

    [AvaloniaFact]
    public async Task TheList_ShowsWhoIsOn_AndOffersPairingOnlyWithMachinesThatAcceptIt()
    {
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);

        viewModel.Machines.Select(machine => (machine.View.Nickname, machine.Presence, machine.CanPair)).Should().Equal(
            ("Desktop", "ligado", false),
            ("Notebook", "visto há 7 min", true),
            ("Celular", "nunca ligou o controle remoto", false));
        viewModel.Machines[0].Details.Should().Be("windows · versão 1.25.0 · este computador");
        viewModel.Machines[0].Fingerprint.Should().Be("chave aaaa bbbb cccc dddd");
        viewModel.Machines[1].Details.Should().Contain("aceita ser controlado");
        viewModel.IsReady.Should().BeTrue();
    }

    /// <summary>Aceitar controle é a decisão que mais pesa: pergunta antes, e "não" deixa tudo como estava.</summary>
    [AvaloniaFact]
    public async Task AcceptingControl_AsksFirst_AndNoLeavesItOff()
    {
        _confirmation.Answer = false;
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.AcceptControl = true;
        await Settled();

        _confirmation.LastAsked!.Headline.Should().Contain("Aceitar controle remoto");
        viewModel.AcceptControl.Should().BeFalse();
        _settings.Saved.Should().BeEmpty();
    }

    [AvaloniaFact]
    public async Task AcceptingControl_SavesIt_AndRegistersRightAway()
    {
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.AcceptControl = true;
        await Settled();

        _settings.Saved.Should().Equal(new RemoteSettings(true, true));
        _runner.Invoked.Should().Contain(typeof(RegisterThisMachineHandler), "os papéis mudaram, e o registro não espera o minuto");
    }

    [AvaloniaFact]
    public async Task TurningTheRemoteOff_AlsoStopsAcceptingControl()
    {
        Show(View(new RemoteSettings(true, true)));
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        Show(View(RemoteSettings.Off, RemoteAvailability.Off));

        viewModel.RemoteEnabled = false;
        await Settled();

        viewModel.AcceptControl.Should().BeFalse();
        _settings.Saved.Should().Equal(RemoteSettings.Off);
    }

    /// <summary>O login de antes de ligar o remoto não tem o escopo: a janela oferece conectar de novo, pelo navegador.</summary>
    [AvaloniaFact]
    public async Task AnOldLogin_OffersToConnectAgain_ThroughTheBrowser()
    {
        Show(View(new RemoteSettings(true, false), RemoteAvailability.NeedsRemoteLogin, "Conecte de novo para o login incluir o controle remoto."));
        var authorize = new Uri("https://sync.example.com/connect/authorize?scope=remote");
        _account.NextAuthorization = new SyncAuthorization(authorize, Task.CompletedTask);
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.NeedsRemoteLogin.Should().BeTrue();
        viewModel.StateText.Should().Contain("Conecte de novo");

        await viewModel.ReconnectAsync();

        _shell.OpenedUris.Should().Equal(authorize);
        _runner.Invoked.Should().Contain(typeof(RegisterThisMachineHandler));
        viewModel.ErrorMessage.Should().BeNull();
    }

    /// <summary>Este computador pede para controlar o notebook: mostra o código em dois grupos, e some quando lá confirmam.</summary>
    [AvaloniaFact]
    public async Task Pairing_ShowsTheCode_UntilTheOtherMachineConfirmsIt()
    {
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        _runner.ResultsByHandler[typeof(RequestPairingHandler)] = new PairingCodeView(Guid.CreateVersion7(), Notebook, "482913", Now.AddMinutes(10));
        _runner.ResultsByHandler[typeof(GetPairingStatusHandler)] = PairingStatus.Pending;

        await viewModel.PairAsync(viewModel.Machines[1]);

        viewModel.ActivePairingCode.Should().Be("482 913");
        viewModel.ActivePairingText.Should().StartWith("Digite este código em Notebook");

        await viewModel.RefreshAsync(Ct);
        viewModel.HasActivePairing.Should().BeTrue("ainda ninguém digitou");

        _runner.ResultsByHandler[typeof(GetPairingStatusHandler)] = PairingStatus.Approved;
        await viewModel.RefreshAsync(Ct);

        viewModel.HasActivePairing.Should().BeFalse();
        viewModel.StatusMessage.Should().Contain("Notebook confirmou");
    }

    [AvaloniaFact]
    public async Task AWrongCode_ShowsWhy_AndTrustsNobody()
    {
        Show(View(new RemoteSettings(true, true), pairings: [new OpenPairingView(Guid.CreateVersion7(), Guid.CreateVersion7(), "Celular", Now.AddMinutes(9))]));
        _runner.FailuresByHandler[typeof(ConfirmPairingHandler)] = new DomainException("O código não confere com o do controlador. Nada foi aprovado.");
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        viewModel.OpenPairings.Single().TypedCode = "000 000";

        await viewModel.ConfirmPairingAsync(viewModel.OpenPairings.Single());

        viewModel.ErrorMessage.Should().Contain("não confere");
        viewModel.StatusMessage.Should().BeNull();
    }

    /// <summary>O relógio recarrega a lista a cada 10 s: o código digitado pela metade não pode sumir.</summary>
    [AvaloniaFact]
    public async Task TheCodeBeingTyped_SurvivesTheRefresh()
    {
        var pairing = new OpenPairingView(Guid.CreateVersion7(), Guid.CreateVersion7(), "Celular", Now.AddMinutes(9));
        Show(View(new RemoteSettings(true, true), pairings: [pairing]));
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        viewModel.OpenPairings.Single().TypedCode = "482";

        await viewModel.RefreshAsync(Ct);

        viewModel.OpenPairings.Single().TypedCode.Should().Be("482");
    }

    [AvaloniaFact]
    public async Task RevokingATrustedController_AsksFirst()
    {
        var trusted = new TrustedControllerView(Guid.CreateVersion7(), Guid.CreateVersion7(), "Celular", new string('a', 64), Now);
        Show(View(new RemoteSettings(true, true), trusted: [trusted]));
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        await viewModel.RevokeTrustedAsync(viewModel.TrustedControllers.Single());

        _confirmation.LastAsked!.Headline.Should().Be("Deixar de confiar em Celular?");
        _runner.Invoked.Should().Contain(typeof(RevokeTrustedControllerHandler));
        viewModel.TrustedControllers.Single().Details.Should().Contain("chave aaaa aaaa aaaa aaaa");
    }

    [AvaloniaFact]
    public async Task TheWindow_DrawsTheLists_AndThePairingCode_WithoutBrokenBindings()
    {
        Show(View(
            new RemoteSettings(true, true),
            pairings: [new OpenPairingView(Guid.CreateVersion7(), Guid.CreateVersion7(), "Celular", Now.AddMinutes(9))],
            trusted: [new TrustedControllerView(Guid.CreateVersion7(), Guid.CreateVersion7(), "Tablet", new string('b', 64), Now)]));
        _runner.ResultsByHandler[typeof(RequestPairingHandler)] = new PairingCodeView(Guid.CreateVersion7(), Notebook, "482913", Now.AddMinutes(10));
        var viewModel = ViewModel();
        var window = new MachinesWindow(viewModel);
        window.Show();
        window.Reveal();
        await viewModel.LoadAsync(Ct);
        await viewModel.PairAsync(viewModel.Machines[1]);

        window.FindControl<ItemsControl>("MachineList")!.IsEffectivelyVisible.Should().BeTrue();
        window.FindControl<ItemsControl>("OpenPairingList")!.IsEffectivelyVisible.Should().BeTrue();
        window.FindControl<ItemsControl>("TrustedList")!.IsEffectivelyVisible.Should().BeTrue();
        window.FindControl<TextBlock>("PairingCodeText")!.Text.Should().Be("482 913");
        window.FindControl<Button>("ReconnectButton")!.IsEffectivelyVisible.Should().BeFalse();

        window.Close();
        window.IsVisible.Should().BeFalse("o X esconde: a janela é única, como as outras do menu");
        viewModel.IsShown.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task WithoutAServer_TheWindowOnlyExplains()
    {
        _account.IsAvailable = false;
        var viewModel = ViewModel();
        var window = new MachinesWindow(viewModel);
        window.Show();
        await viewModel.LoadAsync(Ct);

        viewModel.HasNoServer.Should().BeTrue();
        window.FindControl<ToggleSwitch>("RemoteSwitch")!.IsEffectivelyVisible.Should().BeFalse();
    }

    /// <summary>As mudanças de chave disparam o salvamento sem esperar; o teste deixa a fila do despachante andar.</summary>
    [AvaloniaFact]
    public async Task Controlling_OpensTheOperationScreenForThatMachine()
    {
        _runner.ResultsByHandler[typeof(GetRemoteControlHandler)] = new RemoteControlView(new AgentCatalog("h", [], [], [], []), []);
        _runner.ResultsByHandler[typeof(GetPendingApprovalsHandler)] = (IReadOnlyList<ApprovalView>)[];
        _runner.ResultsByHandler[typeof(ListRemoteSessionsHandler)] = new RemoteSessionList([]);
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        RemoteControlViewModel? opened = null;
        viewModel.ControlOpened += (_, control) => opened = control;

        await viewModel.ControlCommand.ExecuteAsync(viewModel.Machines[1]);

        opened!.Title.Should().Be("Controlar Notebook");
        _runner.Invoked.Should().Contain(typeof(GetRemoteControlHandler));
    }

    /// <summary>"Conversas do Claude": as de todas as máquinas, com o nome de cada uma na linha.</summary>
    [AvaloniaFact]
    public async Task TheConversations_OfEveryMachine_OpenWithTheMachineName()
    {
        var session = new RemoteSessionItem(
            new RemoteAgentSessionView(
                Guid.CreateVersion7(), Notebook, 1, Guid.CreateVersion7(), Guid.CreateVersion7(), "read", null,
                RemoteAgentSessionStatus.Idle, RemoteAgentActivity.Working, null, null, Now, null, Now, Now),
            new RemoteAgentPresence(Notebook, Now),
            "Validar antimicrobianos",
            "feature/ECO-12",
            "Esperando o próximo prompt");
        _runner.ResultsByHandler[typeof(ListRemoteSessionsHandler)] = new RemoteSessionList([session]);
        var viewModel = new MachinesViewModel(
            _runner,
            _account,
            new RemotePresenceScheduler(_runner, TimeProvider.System, NullLogger<RemotePresenceScheduler>.Instance),
            _shell,
            _confirmation,
            new FakeTimeProvider(Now),
            () => new RemoteControlViewModel(_runner, NullLogger<RemoteControlViewModel>.Instance),
            NullLogger<MachinesViewModel>.Instance,
            () => new RemoteConversationsViewModel(_runner, NullLogger<RemoteConversationsViewModel>.Instance));
        await viewModel.LoadAsync(Ct);
        RemoteConversationsViewModel? opened = null;
        viewModel.ConversationsOpened += (_, conversations) => opened = conversations;

        await viewModel.OpenConversationsCommand.ExecuteAsync(null);

        opened!.Sessions.Items.Should().ContainSingle()
            .Which.Details.Should().Be("Notebook · feature/ECO-12 · Esperando o próximo prompt");

        var window = new RemoteConversationsWindow(opened);
        window.Show();
        window.Title.Should().Be("Conversas do Claude");
        window.Close();
    }

    [AvaloniaFact]
    public async Task WithoutTheConversationsWindow_TheButtonDoesNothing()
    {
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        var opened = false;
        viewModel.ConversationsOpened += (_, _) => opened = true;

        await viewModel.OpenConversationsCommand.ExecuteAsync(null);

        opened.Should().BeFalse();
    }

    private static async Task Settled()
    {
        for (var turn = 0; turn < 5; turn++)
        {
            await Task.Yield();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>A configuração do remoto na memória, gravando o que o caso de uso de verdade salvou.</summary>
    private sealed class SettingsStore : IRemoteSettingsStore, IUnitOfWork
    {
        private RemoteSettings _pending = RemoteSettings.Off;

        public List<RemoteSettings> Saved { get; } = [];

        public Task<RemoteSettings> GetAsync(CancellationToken cancellationToken = default) => Task.FromResult(_pending);

        public Task SaveAsync(RemoteSettings settings, CancellationToken cancellationToken = default)
        {
            _pending = settings;
            return Task.CompletedTask;
        }

        public Task SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Saved.Add(_pending);
            return Task.CompletedTask;
        }
    }

    /// <summary>A conta sem navegador nem servidor.</summary>
    private sealed class FakeAccount : ISyncAccount
    {
        public bool IsAvailable { get; set; } = true;

        public Uri? Server => new("https://sync.example.com");

        public Guid DeviceId => ThisMachine;

        public SyncAuthorization? NextAuthorization { get; set; }

        public Task<SyncAuthorization> BeginConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(NextAuthorization ?? throw new InvalidOperationException("Sem autorização preparada."));

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SwitchServerAsync(Uri? server, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;

        public void SyncNow()
        {
        }

        public Task<IReadOnlyList<MyTaskApp.Sync.Contracts.DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<MyTaskApp.Sync.Contracts.DeviceInfo>>([]);

        public Task RenameDeviceAsync(Guid deviceId, string nickname, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
