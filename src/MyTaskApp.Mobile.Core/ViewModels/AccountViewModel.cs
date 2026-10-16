using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.Presentation;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Mobile.Core.ViewModels;

public sealed record DeviceLine(Guid DeviceId, string Nickname, string Detail, bool IsCurrent, bool IsRevoked);

public sealed record ConflictLine(Guid Id, string What, string Mine, string Theirs);

public sealed record FailureLine(Guid OpId, string What, string Error);

/// <summary>
/// A aba Conta (ADR-067): a ligação com a conta e o estado da sincronização
/// sem esconder nada — sincronizado, pendente e falha são estados diferentes,
/// e cada conflito ou recusa tem o que fazer com ele.
/// </summary>
public sealed partial class AccountViewModel : MobileViewModel, IDisposable
{
    private readonly IUseCaseRunner _runner;

    private readonly ISyncAccount _account;

    private readonly SyncScheduler _scheduler;

    private readonly IUiDispatcher _dispatcher;

    private readonly INavigator _navigator;

    private readonly TimeProvider _time;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinked), nameof(CanConnect), nameof(IsPaused), nameof(UserName), nameof(UserEmail), nameof(LastSync), nameof(Counts))]
    private SyncStatusView? _status;

    [ObservableProperty]
    private SyncBadge? _badge;

    [ObservableProperty]
    private bool _isRefreshing;

    public AccountViewModel(
        IUseCaseRunner runner,
        ISyncAccount account,
        SyncScheduler scheduler,
        IUiDispatcher dispatcher,
        INavigator navigator,
        IDialogService dialogs,
        TimeProvider time,
        ILogger<AccountViewModel> logger)
        : base(dialogs, logger)
    {
        _runner = runner;
        _account = account;
        _scheduler = scheduler;
        _dispatcher = dispatcher;
        _navigator = navigator;
        _time = time;
        _scheduler.CycleCompleted += OnCycleCompleted;
    }

    public ObservableCollection<DeviceLine> Devices { get; } = [];

    public ObservableCollection<ConflictLine> Conflicts { get; } = [];

    public ObservableCollection<FailureLine> Failures { get; } = [];

    public bool IsAvailable => _account.IsAvailable;

    public bool HasConflicts => Conflicts.Count > 0;

    public bool HasFailures => Failures.Count > 0;

    public string Server => _account.Server?.GetLeftPart(UriPartial.Authority) ?? "nenhum";

    public bool IsLinked => Status?.Local.State == SyncLinkState.Linked;

    /// <summary>Entrar aparece sem conta, e também quando o login venceu ou o celular foi removido.</summary>
    public bool CanConnect => IsAvailable && Status?.Local.State is not SyncLinkState.Linked;

    public bool IsPaused => IsLinked && Status?.Local.IsEnabled == false;

    public string? UserName => Status?.Local.UserDisplayName;

    public string? UserEmail => Status?.Local.UserEmail;

    public string LastSync => Status?.Local.LastSuccessAt is { } at
        ? "Última sincronização: " + Labels.Instant(at, _time.LocalTimeZone)
        : "Ainda não sincronizou.";

    public string Counts => Status?.Counts is { } counts
        ? $"{counts.Pending} pendente(s) · {counts.Failed} recusada(s) · {counts.OpenConflicts} conflito(s)"
        : string.Empty;

    /// <summary>Tudo, inclusive a lista de dispositivos, que vem do servidor.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        try
        {
            if (!await LoadLocalAsync())
            {
                return;
            }

            if (IsLinked)
            {
                await LoadDevicesAsync();
            }
            else
            {
                Devices.Clear();
            }
        }
        finally
        {
            IsRefreshing = false;
        }
    }

    [RelayCommand]
    public async Task SyncNowAsync()
    {
        _account.SyncNow();
        await Dialogs.ToastAsync("Sincronizando…");
    }

    [RelayCommand]
    public Task ConnectAsync() => _navigator.OpenConnectAsync();

    [RelayCommand]
    public async Task TogglePauseAsync()
    {
        var resume = IsPaused;

        if (await TryAsync(() => _account.SetEnabledAsync(resume, CancellationToken.None), "MobileSyncPauseFailed"))
        {
            await LoadAsync();
        }
    }

    /// <summary>Sair da conta neste celular: as tarefas ficam aqui, só deixam de sincronizar.</summary>
    [RelayCommand]
    public async Task SignOutAsync()
    {
        var confirmed = await Dialogs.ConfirmAsync(
            "Sair da conta",
            "As tarefas continuam neste celular, mas deixam de sincronizar. Alterações ainda não enviadas ficam guardadas aqui.",
            "Sair",
            "Voltar");

        if (confirmed && await TryAsync(() => _account.DisconnectAsync(CancellationToken.None), "MobileSignOutFailed"))
        {
            await Dialogs.ToastAsync("Você saiu da conta neste celular.");
            await LoadAsync();
        }
    }

    [RelayCommand]
    public async Task RenameThisDeviceAsync()
    {
        var current = Devices.FirstOrDefault(device => device.IsCurrent);

        if (current is null)
        {
            return;
        }

        var nickname = await Dialogs.PromptAsync("Nome deste celular", "Como ele aparece na lista de dispositivos.", current.Nickname, SyncSchema.MaxNicknameLength);

        if (!string.IsNullOrWhiteSpace(nickname)
            && await TryAsync(() => _account.RenameDeviceAsync(current.DeviceId, nickname.Trim(), CancellationToken.None), "MobileRenameFailed"))
        {
            await LoadDevicesAsync();
        }
    }

    /// <summary>"Reaplicar o meu" manda o valor deste celular de novo, por cima do que está no servidor.</summary>
    [RelayCommand]
    public Task ReapplyConflictAsync(ConflictLine? conflict) => ResolveAsync(conflict, reapply: true);

    /// <summary>"Manter o do servidor" só esquece o valor deste celular.</summary>
    [RelayCommand]
    public Task KeepServerAsync(ConflictLine? conflict) => ResolveAsync(conflict, reapply: false);

    [RelayCommand]
    public async Task RetryFailureAsync(FailureLine? failure)
    {
        if (failure is not null
            && await TryAsync(() => _runner.RunAsync<RetrySyncOperationHandler>((handler, token) => handler.HandleAsync(new RetrySyncOperation(failure.OpId), token)), "MobileRetryFailed"))
        {
            await LoadAsync();
        }
    }

    [RelayCommand]
    public async Task DiscardFailureAsync(FailureLine? failure)
    {
        if (failure is null)
        {
            return;
        }

        var confirmed = await Dialogs.ConfirmAsync(
            "Descartar alteração",
            "A alteração recusada sai deste celular, e ele volta a mostrar o que está no servidor.",
            "Descartar",
            "Voltar");

        if (confirmed
            && await TryAsync(() => _runner.RunAsync<DiscardSyncOperationHandler>((handler, token) => handler.HandleAsync(new DiscardSyncOperation(failure.OpId), token)), "MobileDiscardFailed"))
        {
            await LoadAsync();
        }
    }

    public void Dispose() => _scheduler.CycleCompleted -= OnCycleCompleted;

    /// <summary>O que este celular sabe sem perguntar ao servidor: estado, conflitos e recusas.</summary>
    private async Task<bool> LoadLocalAsync()
    {
        try
        {
            Status = await _runner.RunAsync<GetSyncStatusHandler, SyncStatusView>(
                (handler, token) => handler.HandleAsync(new GetSyncStatus(), token));
            Badge = SyncStatusText.Describe(Status, _scheduler.IsRunning);

            await LoadConflictsAsync();
            await LoadFailuresAsync();
            OnPropertyChanged(nameof(HasConflicts));
            OnPropertyChanged(nameof(HasFailures));
            return true;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogWarning(exception, "MobileAccountStatusFailed");
            return false;
        }
    }

    private async Task ResolveAsync(ConflictLine? conflict, bool reapply)
    {
        if (conflict is not null
            && await TryAsync(() => _runner.RunAsync<ResolveSyncConflictHandler>((handler, token) => handler.HandleAsync(new ResolveSyncConflict(conflict.Id, reapply), token)), "MobileResolveConflictFailed"))
        {
            await LoadAsync();
        }
    }

    private async Task LoadDevicesAsync()
    {
        try
        {
            var devices = await _account.ListDevicesAsync(CancellationToken.None);
            Devices.Clear();

            foreach (var device in devices
                .OrderByDescending(device => device.IsCurrent || device.DeviceId == _account.DeviceId)
                .ThenBy(device => device.RevokedAt is not null)
                .ThenBy(device => device.Nickname, StringComparer.Create(Labels.Culture, ignoreCase: true)))
            {
                Devices.Add(Describe(device));
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Sem rede, a lista de dispositivos simplesmente não aparece.
            Logger.LogWarning(exception, "MobileDevicesFailed");
        }
    }

    private async Task LoadConflictsAsync()
    {
        var conflicts = await _runner.RunAsync<ListSyncConflictsHandler, IReadOnlyList<SyncConflictView>>(
            (handler, token) => handler.HandleAsync(new ListSyncConflicts(), token));

        Conflicts.Clear();

        foreach (var conflict in conflicts)
        {
            Conflicts.Add(new ConflictLine(
                conflict.Id,
                $"{conflict.EntityLabel ?? "Item"} — {conflict.Field}",
                conflict.LocalValue ?? "(vazio)",
                conflict.ServerValue ?? "(vazio)"));
        }
    }

    private async Task LoadFailuresAsync()
    {
        var failures = await _runner.RunAsync<ListSyncFailuresHandler, SyncFailuresView>(
            (handler, token) => handler.HandleAsync(new ListSyncFailures(), token));

        Failures.Clear();

        foreach (var failure in failures.Operations)
        {
            Failures.Add(new FailureLine(failure.OpId, failure.EntityLabel ?? "Alteração", failure.Error ?? failure.ErrorCode ?? "Recusada pelo servidor."));
        }
    }

    private DeviceLine Describe(DeviceInfo device)
    {
        var isCurrent = device.IsCurrent || device.DeviceId == _account.DeviceId;
        var seen = device.LastSeenAt is { } at ? "visto em " + Labels.Instant(at, _time.LocalTimeZone) : "nunca sincronizou";
        var detail = device.RevokedAt is not null
            ? $"{device.Platform} · removido"
            : $"{device.Platform} · {seen}";

        return new DeviceLine(device.DeviceId, device.Nickname, isCurrent ? "Este celular · " + detail : detail, isCurrent, device.RevokedAt is not null);
    }

    private void OnCycleCompleted(SyncCycleResult result) => _dispatcher.Post(() => _ = LoadLocalAsync());
}
