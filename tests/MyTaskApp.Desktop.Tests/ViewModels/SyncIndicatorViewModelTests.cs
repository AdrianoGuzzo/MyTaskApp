using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Headless.XUnit;
using Avalonia.LogicalTree;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Sync;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A bolinha da sincronização no cabeçalho (ADR-063): qual estado vence qual,
/// o que o tooltip diz, que o "sincronizando" não pisca a cada minuto, e que
/// o XAML liga tudo isso de verdade.
/// </summary>
public class SyncIndicatorViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _clock = new(Now);

    private readonly FakeUseCaseRunner _runner = new();

    private readonly FakeSyncAccount _account = new();

    private readonly SyncScheduler _scheduler = new(
        new IdleCycle(), new SyncTrigger(), TimeProvider.System, NullLogger<SyncScheduler>.Instance);

    public SyncIndicatorViewModelTests() => Show(Linked());

    private SyncIndicatorViewModel ViewModel() =>
        new(_runner, _scheduler, _account, _clock, NullLogger<SyncIndicatorViewModel>.Instance);

    private void Show(SyncLocalState local, SyncCounts? counts = null, SyncCycleResult? cycle = null, bool available = true) =>
        _runner.ResultsByHandler[typeof(GetSyncStatusHandler)] = Status(local, counts, cycle, available);

    private static SyncStatusView Status(SyncLocalState local, SyncCounts? counts = null, SyncCycleResult? cycle = null, bool available = true) =>
        new(available, available ? new Uri("https://sync.example.com") : null, local, counts ?? new SyncCounts(0, 0, 0, 0, 0), false, cycle);

    private static SyncLocalState Linked(SyncLinkState state = SyncLinkState.Linked, bool enabled = true, string? error = null) =>
        SyncLocalState.Unlinked with
        {
            State = state,
            IsEnabled = enabled,
            UserId = Guid.NewGuid(),
            WorkspaceId = Guid.NewGuid(),
            LinkedDeviceId = Guid.NewGuid(),
            LastSuccessAt = Now.AddMinutes(-2),
            LastError = error,
            LastErrorCode = error is null ? null : "Transient",
        };

    private static readonly SyncCycleResult Offline = new(SyncCycleStatus.Transient, Message: "Sem conexão com o servidor de sincronização.");

    private static readonly SyncCycleResult Blocked = new(SyncCycleStatus.Blocked, Message: "Atualize o MyTaskApp para continuar sincronizando.");

    public static TheoryData<string, SyncStatusView, bool, SyncIndicatorKind> States => new()
    {
        { "sem servidor na build", Status(SyncLocalState.Unlinked, available: false), false, SyncIndicatorKind.Hidden },
        { "sem conta", Status(SyncLocalState.Unlinked), false, SyncIndicatorKind.LocalOnly },
        { "login vencido vence pendente", Status(Linked(SyncLinkState.NeedsReconnect), new SyncCounts(3, 0, 0, 0, 0)), false, SyncIndicatorKind.Attention },
        { "revogado vence sincronizando", Status(Linked(SyncLinkState.DeviceRevoked)), true, SyncIndicatorKind.Attention },
        { "bloqueado", Status(Linked(), cycle: Blocked), false, SyncIndicatorKind.Attention },
        { "alteração recusada", Status(Linked(), new SyncCounts(0, 1, 0, 0, 0)), false, SyncIndicatorKind.Attention },
        { "pausada vence sem conexão", Status(Linked(enabled: false, error: "x"), cycle: Offline), false, SyncIndicatorKind.Paused },
        { "sincronizando vence sem conexão", Status(Linked(error: "x"), cycle: Offline), true, SyncIndicatorKind.Syncing },
        { "sem conexão", Status(Linked(error: "x"), new SyncCounts(2, 0, 0, 0, 0), Offline), false, SyncIndicatorKind.Offline },
        { "erro gravado antes do primeiro ciclo", Status(Linked(error: "x")), false, SyncIndicatorKind.Offline },
        { "pendente", Status(Linked(), new SyncCounts(2, 0, 0, 0, 0), SyncCycleResult.Completed), false, SyncIndicatorKind.Pending },
        { "para revisar", Status(Linked(), new SyncCounts(0, 0, 1, 0, 1), SyncCycleResult.Completed), false, SyncIndicatorKind.Review },
        { "em dia", Status(Linked(), cycle: SyncCycleResult.Completed), false, SyncIndicatorKind.Synced },
    };

    [Theory]
    [MemberData(nameof(States))]
    public void EachState_WinsInTheRightOrder(string because, SyncStatusView status, bool syncing, SyncIndicatorKind expected) =>
        SyncIndicatorViewModel.Classify(status, syncing).Should().Be(expected, because);

    [AvaloniaFact]
    public async Task InSync_TheTipSaysSo_WithTheTimeAgo()
    {
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);

        viewModel.IsSynced.Should().BeTrue();
        viewModel.Title.Should().Be("Sincronizado");
        viewModel.Tip.Should().Contain("Última sincronização há 2 min.");
    }

    [AvaloniaFact]
    public async Task TheTimeAgo_FollowsTheClock_WithoutReadingAgain()
    {
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        var changed = new List<string?>();
        viewModel.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        _clock.Advance(TimeSpan.FromMinutes(1));

        changed.Should().Contain(nameof(SyncIndicatorViewModel.Detail));
        viewModel.Detail.Should().Be("Última sincronização há 3 min.");
        _runner.Invoked.Should().HaveCount(1, "o relógio só refaz o texto");
    }

    [AvaloniaFact]
    public async Task Offline_SaysTheChangesAreKeptHere()
    {
        Show(Linked(error: "Sem conexão com o servidor de sincronização."), new SyncCounts(3, 0, 0, 0, 0), Offline);
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);

        viewModel.IsOffline.Should().BeTrue();
        viewModel.Title.Should().Be("Offline");
        viewModel.Detail.Should().Be(
            "Sem conexão com o servidor de sincronização. 3 alterações guardadas aqui; sobem quando a conexão voltar. Tentando de novo sozinho.");
        viewModel.CanSyncNow.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task WithoutAnAccount_ItOffersToConnect()
    {
        Show(SyncLocalState.Unlinked);
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);

        viewModel.IsVisible.Should().BeTrue();
        viewModel.IsLocalOnly.Should().BeTrue();
        viewModel.Title.Should().Be("Só neste computador");
        viewModel.DetailsLabel.Should().Be("Conectar…");
        viewModel.CanSyncNow.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task WithoutAServer_ItIsHidden()
    {
        Show(SyncLocalState.Unlinked, available: false);
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);

        viewModel.IsVisible.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task AnExpiredLogin_AsksToSignInAgain()
    {
        Show(Linked(SyncLinkState.NeedsReconnect), new SyncCounts(2, 0, 0, 0, 0));
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);

        viewModel.NeedsAttention.Should().BeTrue();
        viewModel.Title.Should().Be("O login da sincronização expirou");
        viewModel.DetailsLabel.Should().Be("Entrar de novo…");
        viewModel.CanSyncNow.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task ARoutineCycle_DoesNotBlink()
    {
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.OnCycleStarted();
        _clock.Advance(SyncIndicatorViewModel.SyncingGrace / 2);
        viewModel.IsSyncing.Should().BeFalse("o ciclo de rotina dura milissegundos");

        viewModel.OnCycleCompleted();
        _clock.Advance(SyncIndicatorViewModel.SyncingGrace);

        viewModel.IsSyncing.Should().BeFalse();
        viewModel.IsSynced.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task ALongCycle_ShowsSyncing_AfterTheGrace()
    {
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.OnCycleStarted();
        _clock.Advance(SyncIndicatorViewModel.SyncingGrace);

        viewModel.IsSyncing.Should().BeTrue();
        viewModel.Detail.Should().Be("Buscando novidades dos outros computadores.");

        viewModel.OnCycleCompleted();
        await WaitUntilAsync(() => !viewModel.IsSyncing);
        viewModel.IsSynced.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task SendingChanges_ShowsSyncing_RightAway()
    {
        Show(Linked(), new SyncCounts(2, 0, 0, 0, 0));
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        viewModel.IsPending.Should().BeTrue();

        viewModel.OnCycleStarted();

        viewModel.IsSyncing.Should().BeTrue();
        viewModel.Detail.Should().Be("Enviando 2 alterações.");
    }

    [AvaloniaFact]
    public void SyncNow_AsksTheAccount()
    {
        var viewModel = ViewModel();

        viewModel.SyncNowCommand.Execute(null);

        _account.SyncRequests.Should().Be(1);
    }

    [AvaloniaFact]
    public async Task TheHeader_DrawsTheDot_AndItsPanel()
    {
        var today = Today();
        var indicator = ViewModel();
        await indicator.LoadAsync(Ct);
        var window = new MainWindow { DataContext = today };
        window.Show();

        SyncButton(window).IsVisible.Should().BeFalse("sem indicador atribuído, nada aparece");

        today.SyncIndicator = indicator;
        window.UpdateLayout();

        var button = SyncButton(window);
        button.IsVisible.Should().BeTrue();
        ToolTip.GetTip(button).Should().Be(indicator.Tip);
        button.GetVisualDescendants().OfType<Ellipse>().Single().Classes.Should().Contain(["syncDot", "synced"]);

        var flyout = (Flyout)button.Flyout!;
        flyout.ShowAt(button);
        var panel = (Control)flyout.Content!;
        var texts = panel.GetSelfAndLogicalDescendants().OfType<TextBlock>().Select(text => text.Text).ToList();
        var actions = panel.GetSelfAndLogicalDescendants().OfType<Button>().Select(action => action.Content).ToList();

        texts.Should().Contain(["Sincronizado", "Última sincronização há 2 min."]);
        actions.Should().Equal("Sincronizar agora", "Abrir sincronização…");

        flyout.Hide();
    }

    /// <summary>
    /// O botão de detalhes fecha o painel e abre a janela. Fechar o painel
    /// desliga o binding do conteúdo; com <c>Command</c> no XAML, o Button não
    /// tinha mais o que executar depois do <c>Click</c>.
    /// </summary>
    [AvaloniaFact]
    public async Task ThePanelDetailsButton_OpensTheSyncWindow_AndClosesThePanel()
    {
        var today = Today();
        var indicator = ViewModel();
        await indicator.LoadAsync(Ct);
        today.SyncIndicator = indicator;
        var window = new MainWindow { DataContext = today };
        window.Show();
        var requests = 0;
        today.SyncRequested += () => requests++;

        var flyout = (Flyout)SyncButton(window).Flyout!;
        flyout.ShowAt(SyncButton(window));
        var details = ((Control)flyout.Content!).GetSelfAndLogicalDescendants().OfType<Button>()
            .Single(action => Equals(action.Content, "Abrir sincronização…"));

        ((IInvokeProvider)ControlAutomationPeer.CreatePeerForElement(details)).Invoke();

        requests.Should().Be(1);
        flyout.IsOpen.Should().BeFalse();
    }

    private static Button SyncButton(MainWindow window) =>
        window.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "SyncStatusButton");

    private static TodayViewModel Today() =>
        new(
            new FakeUseCaseRunner { Result = new TodayBoard(new DateOnly(2026, 10, 13), [], [], [], [], []) },
            new FakeConfirmationDialog(),
            new FakeClipboardWriter(),
            TimeProvider.System,
            NullLogger<TodayViewModel>.Instance);

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);

        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            await Task.Delay(10, Ct);
        }
    }

    private sealed class IdleCycle : ISyncCycleRunner
    {
        public Task<SyncCycleResult> RunCycleAsync(CancellationToken cancellationToken) => Task.FromResult(SyncCycleResult.Idle);
    }

    /// <summary>A conta sem servidor: só conta os pedidos de sincronizar.</summary>
    private sealed class FakeSyncAccount : ISyncAccount
    {
        public bool IsAvailable => true;

        public Uri? Server => new("https://sync.example.com");

        public Guid DeviceId { get; } = Guid.NewGuid();

        public int SyncRequests { get; private set; }

        public void SyncNow() => SyncRequests++;

        public Task<SyncAuthorization> BeginConnectAsync(CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task DisconnectAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<DeviceInfo>>([]);

        public Task RenameDeviceAsync(Guid deviceId, string nickname, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
