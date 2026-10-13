using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Desktop.ViewModels;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Desktop.Tests.ViewModels;

/// <summary>
/// A janela "Sincronização…" (ADR-060): o que ela mostra em cada estado da
/// ligação, e que cada botão pede o caso de uso certo — sem conta e sem rede.
/// </summary>
public class SyncViewModelTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Guid ThisDevice = Guid.Parse("0199a000-0000-7000-8000-000000000001");

    private static readonly Guid OtherDevice = Guid.Parse("0199a000-0000-7000-8000-000000000002");

    private readonly FakeUseCaseRunner _runner = new();

    private readonly FakeSyncAccount _account = new();

    private readonly FakeShellLauncher _shell = new();

    private readonly FakeConfirmationDialog _confirmation = new() { Answer = true };

    private readonly SyncScheduler _scheduler = new(
        new IdleCycle(), new SyncTrigger(), TimeProvider.System, NullLogger<SyncScheduler>.Instance);

    public SyncViewModelTests()
    {
        Show(SyncLocalState.Unlinked);
        _runner.ResultsByHandler[typeof(ListSyncConflictsHandler)] = (IReadOnlyList<SyncConflictView>)[];
        _runner.ResultsByHandler[typeof(ListSyncFailuresHandler)] = new SyncFailuresView([], []);
        _runner.ResultsByHandler[typeof(ListCommandReviewsHandler)] = (IReadOnlyList<SyncCommandReviewItem>)[];
    }

    private SyncViewModel ViewModel() =>
        new(_account, _runner, _scheduler, _shell, _confirmation, NullLogger<SyncViewModel>.Instance);

    private void Show(SyncLocalState local, SyncCounts? counts = null, bool available = true) =>
        _runner.ResultsByHandler[typeof(GetSyncStatusHandler)] = new SyncStatusView(
            available, available ? new Uri("https://sync.example.com") : null, local, counts ?? new SyncCounts(0, 0, 0, 0, 0), false, null);

    private static SyncLocalState Linked(SyncLinkState state = SyncLinkState.Linked, bool enabled = true, string? error = null) =>
        SyncLocalState.Unlinked with
        {
            State = state,
            IsEnabled = enabled,
            UserId = Guid.NewGuid(),
            UserDisplayName = "Ana",
            UserEmail = "ana@example.com",
            WorkspaceId = Guid.NewGuid(),
            LinkedDeviceId = ThisDevice,
            LastSuccessAt = DateTimeOffset.UtcNow,
            LastError = error,
            LastErrorCode = error is null ? null : "network",
        };

    [AvaloniaFact]
    public async Task WithoutAnAccount_OnlyConnectIsOffered_AndNothingElseIsAsked()
    {
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);

        viewModel.IsLinked.Should().BeFalse();
        viewModel.CanConnect.Should().BeTrue();
        viewModel.StateLabel.Should().Contain("só neste computador");
        _runner.Invoked.Should().Equal(typeof(GetSyncStatusHandler));
        _account.DevicesListed.Should().Be(0);
    }

    [AvaloniaFact]
    public async Task WithoutAServerInTheBuild_TheWindowOnlyExplains()
    {
        _account.IsAvailable = false;
        Show(SyncLocalState.Unlinked, available: false);
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);

        viewModel.IsUnavailable.Should().BeTrue();
        viewModel.CanConnect.Should().BeFalse();
        viewModel.StateLabel.Should().Be("Indisponível nesta versão");
    }

    [AvaloniaFact]
    public async Task Linked_ShowsTheUser_TheDevices_AndTheLists()
    {
        Show(Linked());
        _account.Devices =
        [
            new DeviceInfo(OtherDevice, "Notebook", "windows", "1.2.0", DateTimeOffset.UtcNow, null, null, false),
            new DeviceInfo(ThisDevice, "Desktop", "windows", "1.2.0", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, true),
        ];
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);

        viewModel.IsLinked.Should().BeTrue();
        viewModel.CanConnect.Should().BeFalse();
        viewModel.UserText.Should().Be("Ana · ana@example.com");
        viewModel.StateLabel.Should().Be("Em dia");
        viewModel.Devices.Select(device => device.Nickname).Should().Equal("Desktop", "Notebook");
        viewModel.Devices[0].Details.Should().EndWith("este computador");
    }

    [AvaloniaFact]
    public async Task AnExpiredLogin_AsksToSignInAgain()
    {
        Show(Linked(SyncLinkState.NeedsReconnect));
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);

        viewModel.NeedsAttention.Should().BeTrue();
        viewModel.CanConnect.Should().BeTrue();
        viewModel.ConnectLabel.Should().Be("Entrar de novo com o Google");
        _account.DevicesListed.Should().Be(0, "sem login válido, a lista de dispositivos não é pedida");
    }

    [AvaloniaFact]
    public async Task Connecting_OpensTheBrowser_AndWaitsForTheLogin()
    {
        var login = new TaskCompletionSource();
        _account.NextAuthorization = new SyncAuthorization(new Uri("https://sync.example.com/connect/authorize?x=1"), login.Task);
        var viewModel = ViewModel();

        var connecting = viewModel.ConnectAsync();

        viewModel.IsConnecting.Should().BeTrue();
        _shell.OpenedUris.Should().ContainSingle().Which.Host.Should().Be("sync.example.com");

        Show(Linked());
        login.SetResult();
        await connecting;

        viewModel.IsConnecting.Should().BeFalse();
        viewModel.IsLinked.Should().BeTrue();
        viewModel.ErrorMessage.Should().BeNull();
    }

    [AvaloniaFact]
    public async Task ARefusedLogin_ShowsWhy()
    {
        _account.NextAuthorization = new SyncAuthorization(
            new Uri("https://sync.example.com/connect/authorize"),
            Task.FromException(new DomainException("Este computador foi revogado na conta.")));
        var viewModel = ViewModel();

        await viewModel.ConnectAsync();

        viewModel.ErrorMessage.Should().Be("Este computador foi revogado na conta.");
        viewModel.IsConnecting.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task WithoutABrowser_TheLoginIsAbandoned()
    {
        _shell.Succeeds = false;
        _account.NextAuthorization = new SyncAuthorization(new Uri("https://sync.example.com/connect/authorize"), new TaskCompletionSource().Task);
        var viewModel = ViewModel();

        await viewModel.ConnectAsync();

        viewModel.ErrorMessage.Should().Contain("navegador");
        viewModel.IsConnecting.Should().BeFalse();
    }

    [AvaloniaFact]
    public async Task Disconnecting_SaysTheLocalDataStays_AndCanBeRefused()
    {
        Show(Linked());
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        _confirmation.Answer = false;

        await viewModel.DisconnectAsync();

        _confirmation.LastAsked!.Message.Should().Contain("Tudo continua neste computador");
        _confirmation.LastAsked.IsIrreversible.Should().BeFalse();
        _account.Disconnected.Should().BeFalse();

        _confirmation.Answer = true;
        await viewModel.DisconnectAsync();

        _account.Disconnected.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task TheSwitch_PausesAndResumes_ButLoadingDoesNotTouchIt()
    {
        Show(Linked(enabled: true));
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        _account.EnabledChanges.Should().BeEmpty("ler o estado não é mudar o estado");

        viewModel.IsEnabled = false;
        await _account.LastChange;

        _account.EnabledChanges.Should().Equal(false);
    }

    [AvaloniaFact]
    public async Task Conflicts_ShowReadableValues_AndBothChoicesReachTheUseCase()
    {
        Show(Linked(), new SyncCounts(0, 0, 1, 0, 0));
        _runner.ResultsByHandler[typeof(ListSyncConflictsHandler)] = (IReadOnlyList<SyncConflictView>)
        [
            new SyncConflictView(Guid.NewGuid(), SyncEntityTypes.Task, Guid.NewGuid(), "Relatório", SyncFields.Task.Title,
                "\"Relatório mensal\"", "\"Relatório do mês\"", SyncErrorCodes.ConcurrentChange, DateTimeOffset.UtcNow),
        ];
        var viewModel = ViewModel();

        await viewModel.LoadAsync(Ct);

        var conflict = viewModel.Conflicts.Should().ContainSingle().Subject;
        conflict.Title.Should().Be("Tarefa \"Relatório\" — título");
        conflict.Local.Should().Be("Relatório mensal");
        conflict.Server.Should().Be("Relatório do mês");
        conflict.When.Should().Contain("alterado antes em outro computador");

        await viewModel.ReapplyAsync(conflict);
        await viewModel.DismissAsync(conflict);

        _runner.Invoked.Count(type => type == typeof(ResolveSyncConflictHandler)).Should().Be(2);
    }

    [AvaloniaFact]
    public async Task DiscardingARefusedChange_IsAskedAsIrreversible()
    {
        Show(Linked(), new SyncCounts(0, 1, 0, 1, 0));
        _runner.ResultsByHandler[typeof(ListSyncFailuresHandler)] = new SyncFailuresView(
            [new SyncFailedOperationView(Guid.NewGuid(), SyncEntityTypes.Tag, Guid.NewGuid(), "cliente", SyncErrorCodes.ValidationFailed, "Nome longo demais.", DateTimeOffset.UtcNow)],
            [new SyncApplyFailureView(SyncEntityTypes.Task, Guid.NewGuid(), "apply_failed", "Falhou.")]);
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);

        viewModel.HasFailures.Should().BeTrue();
        viewModel.HasReceivedFailures.Should().BeTrue();
        var failure = viewModel.Failures.Should().ContainSingle().Subject;
        failure.Reason.Should().Be("Nome longo demais.");

        await viewModel.DiscardFailureAsync(failure);

        _confirmation.LastAsked!.IsIrreversible.Should().BeTrue();
        _runner.Invoked.Should().Contain(typeof(DiscardSyncOperationHandler));
    }

    [AvaloniaFact]
    public async Task AReceivedCommand_ShowsItsFullText_BeforeBeingApproved()
    {
        Show(Linked(), new SyncCounts(0, 0, 0, 0, 1));
        _account.Devices = [new DeviceInfo(OtherDevice, "Notebook", "windows", "1.2.0", DateTimeOffset.UtcNow, null, null, false)];
        _runner.ResultsByHandler[typeof(ListCommandReviewsHandler)] = (IReadOnlyList<SyncCommandReviewItem>)
            [new SyncCommandReviewItem(SyncEntityTypes.Command, Guid.NewGuid(), "build", "dotnet build -c Release", OtherDevice, 3)];
        var viewModel = ViewModel();

        // Os apelidos vêm com os dispositivos: a segunda carga já os conhece.
        await viewModel.LoadAsync(Ct);
        await viewModel.LoadAsync(Ct);

        var review = viewModel.Reviews.Should().ContainSingle().Subject;
        review.From.Should().Be("Notebook");

        await viewModel.ApproveReviewAsync(review);

        _confirmation.LastAsked!.Message.Should().Contain("dotnet build -c Release").And.Contain("Notebook");
        _runner.Invoked.Should().Contain(typeof(ApproveCommandReviewHandler));
    }

    [AvaloniaFact]
    public async Task Devices_AreRenamed_AndRevokedAfterAsking()
    {
        Show(Linked());
        _account.Devices = [new DeviceInfo(OtherDevice, "Notebook", "windows", "1.2.0", DateTimeOffset.UtcNow, null, null, false)];
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        var device = viewModel.Devices.Single();

        device.Nickname = "Notebook do trabalho";
        await viewModel.RenameDeviceAsync(device);
        await viewModel.RevokeDeviceAsync(device);

        _account.Renamed.Should().Equal((OtherDevice, "Notebook do trabalho"));
        _account.Revoked.Should().Equal(OtherDevice);
        _confirmation.LastAsked!.IsIrreversible.Should().BeTrue();
    }

    [AvaloniaFact]
    public async Task AFailedAction_ShowsTheServerMessage_AndKeepsTheWindowUsable()
    {
        Show(Linked());
        _account.Devices = [new DeviceInfo(OtherDevice, "Notebook", "windows", "1.2.0", DateTimeOffset.UtcNow, null, null, false)];
        var viewModel = ViewModel();
        await viewModel.LoadAsync(Ct);
        _account.Failure = new SyncApiException(SyncFailureKind.Transient, "Sem conexão com o servidor.");

        await viewModel.RenameDeviceAsync(viewModel.Devices.Single());

        viewModel.ErrorMessage.Should().Be("Sem conexão com o servidor.");
        viewModel.IsBusy.Should().BeFalse();
    }

    [Theory]
    [InlineData(null, "(vazio)")]
    [InlineData("null", "(vazio)")]
    [InlineData("\"texto\"", "texto")]
    [InlineData("true", "sim")]
    [InlineData("3", "3")]
    [InlineData("não é json", "não é json")]
    public void WireValues_AreShownAsPeopleReadThem(string? json, string expected) =>
        SyncViewModel.Show(json).Should().Be(expected);

    [AvaloniaFact]
    public async Task TheWindow_DrawsBothStates_WithoutBrokenBindings()
    {
        var viewModel = ViewModel();
        var window = new SyncWindow(viewModel);
        window.Show();
        window.Reveal();
        await viewModel.LoadAsync(Ct);

        window.FindControl<Button>("ConnectButton")!.IsEffectivelyVisible.Should().BeTrue();
        window.FindControl<Button>("DisconnectButton")!.IsEffectivelyVisible.Should().BeFalse();
        window.FindControl<ToggleSwitch>("EnabledSwitch")!.IsEffectivelyVisible.Should().BeFalse();

        Show(Linked(), new SyncCounts(0, 0, 1, 0, 1));
        _account.Devices = [new DeviceInfo(ThisDevice, "Desktop", "windows", "1.2.0", DateTimeOffset.UtcNow, null, null, true)];
        _runner.ResultsByHandler[typeof(ListSyncConflictsHandler)] = (IReadOnlyList<SyncConflictView>)
            [new SyncConflictView(Guid.NewGuid(), SyncEntityTypes.Tag, Guid.NewGuid(), "cliente", SyncFields.Tag.Name, "\"Cliente\"", "\"CLIENTE\"", SyncErrorCodes.ConcurrentChange, DateTimeOffset.UtcNow)];
        _runner.ResultsByHandler[typeof(ListCommandReviewsHandler)] = (IReadOnlyList<SyncCommandReviewItem>)
            [new SyncCommandReviewItem(SyncEntityTypes.Command, Guid.NewGuid(), "build", "dotnet build", OtherDevice, 1)];
        await viewModel.LoadAsync(Ct);

        window.FindControl<Button>("ConnectButton")!.IsEffectivelyVisible.Should().BeFalse();
        window.FindControl<Button>("DisconnectButton")!.IsEffectivelyVisible.Should().BeTrue();
        window.FindControl<TextBlock>("UserText")!.Text.Should().Be("Ana · ana@example.com");
        window.FindControl<ItemsControl>("ConflictList")!.IsEffectivelyVisible.Should().BeTrue();
        window.FindControl<ItemsControl>("ReviewList")!.IsEffectivelyVisible.Should().BeTrue();
        window.FindControl<ItemsControl>("DeviceList")!.IsEffectivelyVisible.Should().BeTrue();

        window.Close();
        window.IsVisible.Should().BeFalse("o X esconde: a janela é singleton");
        viewModel.IsShown.Should().BeFalse();
    }

    private sealed class IdleCycle : ISyncCycleRunner
    {
        public Task<SyncCycleResult> RunCycleAsync(CancellationToken cancellationToken) => Task.FromResult(SyncCycleResult.Idle);
    }

    /// <summary>A conta sem navegador nem servidor: obedece e registra o que pediram.</summary>
    private sealed class FakeSyncAccount : ISyncAccount
    {
        public bool IsAvailable { get; set; } = true;

        public Uri? Server => new("https://sync.example.com");

        public Guid DeviceId => ThisDevice;

        public SyncAuthorization? NextAuthorization { get; set; }

        public IReadOnlyList<DeviceInfo> Devices { get; set; } = [];

        public int DevicesListed { get; private set; }

        public bool Disconnected { get; private set; }

        public List<bool> EnabledChanges { get; } = [];

        public Task LastChange { get; private set; } = Task.CompletedTask;

        public List<(Guid, string)> Renamed { get; } = [];

        public List<Guid> Revoked { get; } = [];

        public int SyncRequests { get; private set; }

        public Exception? Failure { get; set; }

        public Task<SyncAuthorization> BeginConnectAsync(CancellationToken cancellationToken) =>
            Task.FromResult(NextAuthorization ?? throw new InvalidOperationException("Sem autorização preparada."));

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            Disconnected = true;
            return Task.CompletedTask;
        }

        public Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
        {
            EnabledChanges.Add(enabled);
            LastChange = Task.Delay(10, cancellationToken);
            return Task.CompletedTask;
        }

        public void SyncNow() => SyncRequests++;

        public Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken)
        {
            DevicesListed++;
            return Task.FromResult(Devices);
        }

        public Task RenameDeviceAsync(Guid deviceId, string nickname, CancellationToken cancellationToken)
        {
            if (Failure is { } failure)
            {
                return Task.FromException(failure);
            }

            Renamed.Add((deviceId, nickname));
            return Task.CompletedTask;
        }

        public Task RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken)
        {
            Revoked.Add(deviceId);
            return Task.CompletedTask;
        }
    }
}
