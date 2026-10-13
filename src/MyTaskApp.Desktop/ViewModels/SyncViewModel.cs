using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Um valor perdido para o servidor, como a lista mostra.</summary>
public sealed record SyncConflictItem(SyncConflictView View, string Title, string Local, string Server, string When);

/// <summary>Uma alteração recusada de vez.</summary>
public sealed record SyncFailureItem(SyncFailedOperationView View, string Title, string Reason);

/// <summary>Um comando que chegou de outro computador e espera revisão.</summary>
public sealed record SyncReviewItem(SyncCommandReviewItem View, string From);

/// <summary>Um dispositivo da conta, com o apelido editável.</summary>
public sealed partial class SyncDeviceItem(DeviceInfo device) : ObservableObject
{
    public DeviceInfo Device { get; } = device;

    [ObservableProperty]
    private string _nickname = device.Nickname;

    public bool IsCurrent => Device.IsCurrent;

    public bool IsRevoked => Device.RevokedAt is not null;

    public bool CanRevoke => !IsRevoked;

    public string Details =>
        IsRevoked
            ? $"{Device.Platform} · revogado em {Device.RevokedAt!.Value.ToLocalTime():dd/MM/yyyy}"
            : $"{Device.Platform} · versão {Device.AppVersion}"
              + (Device.LastSeenAt is { } seen ? $" · visto em {seen.ToLocalTime():dd/MM HH:mm}" : string.Empty)
              + (IsCurrent ? " · este computador" : string.Empty);
}

/// <summary>
/// A janela "Sincronização…" (ADR-061): a conta, o estado, os conflitos, as
/// falhas, os comandos a revisar e os dispositivos.
/// </summary>
/// <remarks>
/// <para>
/// <b>Desconectar não apaga nada.</b> A tela diz isso antes de perguntar, e a
/// pergunta repete: tudo continua neste computador, só para de sincronizar.
/// </para>
/// <para>
/// <b>Sem servidor na build, a janela só explica.</b> O app continua inteiro,
/// só local; não há botão que leve a um erro.
/// </para>
/// </remarks>
public sealed partial class SyncViewModel : ObservableObject
{
    private readonly ISyncAccount _account;
    private readonly IUseCaseRunner _runner;
    private readonly IShellLauncher _shell;
    private readonly IConfirmationDialog _confirmation;
    private readonly ILogger<SyncViewModel> _logger;

    private CancellationTokenSource? _authorization;

    private bool _loading;

    public SyncViewModel(
        ISyncAccount account,
        IUseCaseRunner runner,
        SyncScheduler scheduler,
        IShellLauncher shell,
        IConfirmationDialog confirmation,
        ILogger<SyncViewModel> logger)
    {
        _account = account;
        _runner = runner;
        _shell = shell;
        _confirmation = confirmation;
        _logger = logger;

        // Singleton como a janela: a assinatura vive com o app.
        scheduler.CycleCompleted += cycle => OnUi(() =>
        {
            if (IsShown)
            {
                _ = LoadAsync(CancellationToken.None);
            }
        });
    }

    /// <summary>A janela está à vista: só então os ciclos em segundo plano a recarregam.</summary>
    public bool IsShown { get; set; }

    public bool IsAvailable => _account.IsAvailable;

    public bool IsUnavailable => !IsAvailable;

    public string ServerText => _account.Server?.Host ?? "—";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLinked), nameof(CanConnect), nameof(NeedsAttention), nameof(StateLabel), nameof(UserText), nameof(LastSyncText), nameof(ConnectLabel))]
    private SyncStatusView? _status;

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    private bool _isConnecting;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    public ObservableCollection<SyncConflictItem> Conflicts { get; } = [];

    public ObservableCollection<SyncFailureItem> Failures { get; } = [];

    public ObservableCollection<SyncReviewItem> Reviews { get; } = [];

    public ObservableCollection<SyncDeviceItem> Devices { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReceivedFailures))]
    private int _receivedFailures;

    public bool HasReceivedFailures => ReceivedFailures > 0;

    public bool HasConflicts => Conflicts.Count > 0;

    public bool HasFailures => Failures.Count > 0 || ReceivedFailures > 0;

    public bool HasReviews => Reviews.Count > 0;

    public bool HasDevices => Devices.Count > 0;

    private SyncLinkState State => Status?.Local.State ?? SyncLinkState.Unlinked;

    /// <summary>Ligado à conta, mesmo que o login peça renovação: a tela mostra conta, dispositivos e listas.</summary>
    public bool IsLinked => State != SyncLinkState.Unlinked;

    /// <summary>Conectar: sem conta, ou com o login vencido, ou como dispositivo novo.</summary>
    public bool CanConnect => IsAvailable && !IsConnecting && State != SyncLinkState.Linked;

    public bool NeedsAttention => State is SyncLinkState.NeedsReconnect or SyncLinkState.DeviceRevoked;

    public string ConnectLabel => State switch
    {
        SyncLinkState.NeedsReconnect => "Entrar de novo com o Google",
        SyncLinkState.DeviceRevoked => "Conectar como dispositivo novo",
        _ => "Conectar com o Google",
    };

    public string StateLabel => Status switch
    {
        null => "Carregando…",
        { IsAvailable: false } => "Indisponível nesta versão",
        { Local.State: SyncLinkState.Unlinked } => "Não conectado — tudo fica só neste computador",
        { Local.State: SyncLinkState.NeedsReconnect } => "O login expirou: entre de novo",
        { Local.State: SyncLinkState.DeviceRevoked } => "Este computador foi revogado na conta",
        { Local.IsEnabled: false } => "Pausada",
        { IsRunning: true } => "Sincronizando…",
        { Local.LastErrorCode: not null } => "Com problema — tentando de novo",
        { Counts.Pending: > 0 } => $"{Status.Counts.Pending} alteração(ões) esperando para subir",
        _ => "Em dia",
    };

    public string UserText => Status?.Local is { UserDisplayName: { } name } local
        ? local.UserEmail is { } email ? $"{name} · {email}" : name
        : string.Empty;

    public string LastSyncText => Status?.Local.LastSuccessAt is { } when
        ? $"Última sincronização: {when.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture)}"
        : "Ainda não sincronizou.";

    public string? LastErrorText => Status?.Local.LastError;

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var status = await _runner.RunAsync<GetSyncStatusHandler, SyncStatusView>(
                (handler, token) => handler.HandleAsync(new GetSyncStatus(), token),
                cancellationToken);

            _loading = true;
            Status = status;
            IsEnabled = status.Local.IsEnabled;
            _loading = false;
            OnPropertyChanged(nameof(LastErrorText));

            if (!IsLinked)
            {
                Replace(Conflicts, []);
                Replace(Failures, []);
                Replace(Reviews, []);
                Replace(Devices, []);
                ReceivedFailures = 0;
                NotifyLists();
                return;
            }

            var conflicts = await _runner.RunAsync<ListSyncConflictsHandler, IReadOnlyList<SyncConflictView>>(
                (handler, token) => handler.HandleAsync(new ListSyncConflicts(), token), cancellationToken);
            var failures = await _runner.RunAsync<ListSyncFailuresHandler, SyncFailuresView>(
                (handler, token) => handler.HandleAsync(new ListSyncFailures(), token), cancellationToken);
            var reviews = await _runner.RunAsync<ListCommandReviewsHandler, IReadOnlyList<SyncCommandReviewItem>>(
                (handler, token) => handler.HandleAsync(new ListCommandReviews(), token), cancellationToken);

            Replace(Conflicts, conflicts.Select(ToItem));
            Replace(Failures, failures.Operations.Select(failure => new SyncFailureItem(failure, Describe(failure.EntityType, failure.EntityLabel), failure.Error ?? failure.ErrorCode ?? "Recusada pelo servidor.")));
            ReceivedFailures = failures.Received.Count;
            Replace(Reviews, reviews.Select(review => new SyncReviewItem(review, NicknameOf(review.FromDeviceId))));
            NotifyLists();

            if (State == SyncLinkState.Linked)
            {
                await LoadDevicesAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _loading = false;
            _logger.LogError(exception, "SyncWindowLoadFailed");
            ErrorMessage = "Não foi possível ler o estado da sincronização.";
        }
    }

    private async Task LoadDevicesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var devices = await _account.ListDevicesAsync(cancellationToken);
            Replace(Devices, devices.OrderByDescending(device => device.IsCurrent).ThenBy(device => device.RevokedAt is not null).Select(device => new SyncDeviceItem(device)));
            OnPropertyChanged(nameof(HasDevices));
        }
        catch (SyncApiException exception)
        {
            // Sem rede, a lista de dispositivos fica para depois; o resto da tela vale.
            _logger.LogInformation("SyncDevicesUnavailable {Kind}", exception.Kind);
        }
    }

    [RelayCommand]
    public async Task ConnectAsync()
    {
        _authorization?.Cancel();
        _authorization = new CancellationTokenSource();
        var cancellation = _authorization.Token;

        ErrorMessage = null;
        IsConnecting = true;

        try
        {
            var authorization = await _account.BeginConnectAsync(cancellation);
            StatusMessage = "Conclua o login no navegador. O MyTaskApp espera aqui.";

            if (!await _shell.OpenUriAsync(authorization.AuthorizeUrl))
            {
                StatusMessage = null;
                ErrorMessage = "Não foi possível abrir o navegador. Tente de novo.";
                await _authorization.CancelAsync();
                return;
            }

            await authorization.Completion.WaitAsync(cancellation);
            StatusMessage = "Conectado. A primeira sincronização traz o que já existe na conta e manda o que só existe aqui.";
        }
        catch (DomainException exception)
        {
            StatusMessage = null;
            ErrorMessage = exception.Message;
        }
        catch (SyncApiException exception)
        {
            StatusMessage = null;
            ErrorMessage = exception.Message;
        }
        catch (OperationCanceledException)
        {
            StatusMessage = null;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "SyncConnectFailed");
            StatusMessage = null;
            ErrorMessage = "Não foi possível conectar a conta. Tente de novo.";
        }
        finally
        {
            IsConnecting = false;
            await LoadAsync(CancellationToken.None);
        }
    }

    [RelayCommand]
    public void CancelConnect() => _authorization?.Cancel();

    [RelayCommand]
    public async Task DisconnectAsync()
    {
        var confirmed = await _confirmation.AskAsync(new ConfirmationRequest(
            "Desconectar a conta?",
            "Tudo continua neste computador: tarefas, etiquetas, horas e post-its. Só param de sincronizar. "
            + "Conectar de novo à mesma conta manda o que mudou nesse meio-tempo.",
            "Desconectar"));

        if (!confirmed)
        {
            return;
        }

        await RunAsync(() => _account.DisconnectAsync(CancellationToken.None), "Conta desconectada. Os dados locais ficaram.", "Não foi possível desconectar.");
    }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_loading && IsLinked)
        {
            _ = RunAsync(() => _account.SetEnabledAsync(value, CancellationToken.None), value ? "Sincronização ligada." : "Sincronização pausada.", "Não foi possível mudar a sincronização.");
        }
    }

    [RelayCommand]
    public async Task SyncNowAsync()
    {
        _account.SyncNow();
        StatusMessage = "Sincronizando…";
        await Task.CompletedTask;
    }

    [RelayCommand]
    public Task ReapplyAsync(SyncConflictItem item) =>
        RunAsync(
            () => _runner.RunAsync<ResolveSyncConflictHandler>((handler, token) => handler.HandleAsync(new ResolveSyncConflict(item.View.Id, Reapply: true), token)),
            "Seu valor foi reaplicado e vai para o servidor.",
            "Não foi possível reaplicar.");

    [RelayCommand]
    public Task DismissAsync(SyncConflictItem item) =>
        RunAsync(
            () => _runner.RunAsync<ResolveSyncConflictHandler>((handler, token) => handler.HandleAsync(new ResolveSyncConflict(item.View.Id, Reapply: false), token)),
            null,
            "Não foi possível descartar.");

    [RelayCommand]
    public Task RetryFailureAsync(SyncFailureItem item) =>
        RunAsync(
            () => _runner.RunAsync<RetrySyncOperationHandler>((handler, token) => handler.HandleAsync(new RetrySyncOperation(item.View.OpId), token)),
            "A alteração vai ser enviada de novo.",
            "Não foi possível tentar de novo.");

    [RelayCommand]
    public async Task DiscardFailureAsync(SyncFailureItem item)
    {
        var confirmed = await _confirmation.AskAsync(new ConfirmationRequest(
            "Descartar a alteração recusada?",
            $"\"{item.Title}\" volta a ficar como está no servidor. A alteração feita aqui é perdida.",
            "Descartar",
            IsIrreversible: true));

        if (confirmed)
        {
            await RunAsync(
                () => _runner.RunAsync<DiscardSyncOperationHandler>((handler, token) => handler.HandleAsync(new DiscardSyncOperation(item.View.OpId), token)),
                "Descartada.",
                "Não foi possível descartar.");
        }
    }

    [RelayCommand]
    public Task RetryReceivedAsync() =>
        RunAsync(
            () => _runner.RunAsync<RetryReceivedChangesHandler>((handler, token) => handler.HandleAsync(new RetryReceivedChanges(), token)),
            "Buscado de novo no servidor.",
            "Não foi possível buscar de novo.");

    /// <summary>Mostra o texto inteiro antes de liberar: é o que vai rodar neste computador.</summary>
    [RelayCommand]
    public async Task ApproveReviewAsync(SyncReviewItem item)
    {
        var confirmed = await _confirmation.AskAsync(new ConfirmationRequest(
            "Liberar este comando neste computador?",
            $"Veio de {item.From}. Confira o que ele faz antes de liberar:{Environment.NewLine}{Environment.NewLine}{item.View.CommandText}",
            "Revisei e libero"));

        if (confirmed)
        {
            await RunAsync(
                () => _runner.RunAsync<ApproveCommandReviewHandler>((handler, token) => handler.HandleAsync(new ApproveCommandReview(item.View.EntityType, item.View.EntityId), token)),
                "Comando liberado neste computador.",
                "Não foi possível liberar.");
        }
    }

    [RelayCommand]
    public Task RenameDeviceAsync(SyncDeviceItem item) =>
        RunAsync(() => _account.RenameDeviceAsync(item.Device.DeviceId, item.Nickname, CancellationToken.None), "Apelido salvo.", "Não foi possível salvar o apelido.");

    [RelayCommand]
    public async Task RevokeDeviceAsync(SyncDeviceItem item)
    {
        var confirmed = await _confirmation.AskAsync(new ConfirmationRequest(
            $"Revogar {item.Nickname}?",
            item.IsCurrent
                ? "Este computador para de sincronizar na hora. Os dados dele ficam aqui; para voltar, ele entra como dispositivo novo."
                : "Ele para de sincronizar e perde o acesso à conta. Os dados que estão nele ficam lá; para voltar, ele entra como dispositivo novo.",
            "Revogar",
            IsIrreversible: true));

        if (confirmed)
        {
            await RunAsync(() => _account.RevokeDeviceAsync(item.Device.DeviceId, CancellationToken.None), "Dispositivo revogado.", "Não foi possível revogar.");
        }
    }

    private async Task RunAsync(Func<Task> action, string? success, string failure)
    {
        ErrorMessage = null;
        StatusMessage = null;
        IsBusy = true;

        try
        {
            await action();
            StatusMessage = success;
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (SyncApiException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "SyncWindowActionFailed");
            ErrorMessage = failure;
        }
        finally
        {
            IsBusy = false;
            await LoadAsync(CancellationToken.None);
        }
    }

    private SyncConflictItem ToItem(SyncConflictView view) => new(
        view,
        $"{Describe(view.EntityType, view.EntityLabel)} — {FieldLabel(view.Field)}",
        Show(view.LocalValue),
        view.ServerValue is null ? "(excluído)" : Show(view.ServerValue),
        view.DetectedAt.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.CurrentCulture) + " · " + ReasonLabel(view.Reason));

    private string NicknameOf(Guid? deviceId) =>
        Devices.FirstOrDefault(device => device.Device.DeviceId == deviceId)?.Device.Nickname ?? "outro computador";

    /// <summary>O valor do fio como a pessoa lê: texto sem aspas, vazio dito por extenso.</summary>
    public static string Show(string? json)
    {
        if (json is null)
        {
            return "(vazio)";
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            return document.RootElement.ValueKind switch
            {
                JsonValueKind.Null => "(vazio)",
                JsonValueKind.String => document.RootElement.GetString() is { Length: > 0 } text
                    && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant)
                    && text.EndsWith('Z')
                        ? instant.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.CurrentCulture)
                        : document.RootElement.GetString()!,
                JsonValueKind.True => "sim",
                JsonValueKind.False => "não",
                _ => json,
            };
        }
        catch (JsonException)
        {
            return json;
        }
    }

    private static string Describe(string type, string? label)
    {
        var kind = type switch
        {
            SyncEntityTypes.Task => "Tarefa",
            SyncEntityTypes.Occurrence => "Agendamento",
            SyncEntityTypes.Tag => "Etiqueta",
            SyncEntityTypes.TaskTag => "Etiqueta da tarefa",
            SyncEntityTypes.TagDirectory => "Diretório",
            SyncEntityTypes.Command => "Comando",
            SyncEntityTypes.DirectoryCommand => "Comando do diretório",
            SyncEntityTypes.Development => "Ambiente",
            SyncEntityTypes.TimeEntry => "Horas",
            SyncEntityTypes.StickyNote => "Post-it",
            _ => type,
        };

        return label is null ? kind : $"{kind} \"{label}\"";
    }

    private static string FieldLabel(string field) => field switch
    {
        SyncFields.Task.Title => "título",
        SyncFields.Task.Description => "anotação",
        SyncFields.Task.Priority => "prioridade",
        SyncFields.Task.NextAction => "próxima ação",
        SyncFields.Task.Estimate => "estimativa",
        SyncFields.Task.Reminder => "lembrete",
        SyncFields.Task.ArchivedAt => "arquivamento",
        SyncFields.Task.DeletedAt => "lixeira",
        SyncFields.Occurrence.Schedule => "data",
        SyncFields.Occurrence.State => "situação",
        SyncFields.Occurrence.Deadline => "prazo",
        SyncFields.Tag.Name => "nome",
        SyncFields.Tag.ColorHex => "cor",
        SyncFields.TagDirectory.Alias => "alias",
        SyncFields.StickyNote.Content => "texto",
        SyncFields.TimeEntry.Note => "nota",
        _ => field,
    };

    private static string ReasonLabel(string reason) => reason switch
    {
        SyncErrorCodes.ConcurrentChange => "alterado antes em outro computador",
        SyncErrorCodes.UniqueViolation => "o nome já existe na conta",
        SyncErrorCodes.EntityDeleted => "excluído em outro computador",
        SyncErrorCodes.ParentDeleted => "o item de que dependia foi excluído",
        "merged_on_join" => "juntado ao da conta na primeira conexão",
        _ => reason,
    };

    private void NotifyLists()
    {
        OnPropertyChanged(nameof(HasConflicts));
        OnPropertyChanged(nameof(HasFailures));
        OnPropertyChanged(nameof(HasReviews));
        OnPropertyChanged(nameof(HasDevices));
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();

        foreach (var item in items)
        {
            target.Add(item);
        }
    }

    private static void OnUi(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
        {
            action();
        }
        else
        {
            Dispatcher.UIThread.Post(action);
        }
    }
}
