using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.Ports;
using MyTaskApp.Mobile.Core.Presentation;

namespace MyTaskApp.Mobile.Core.ViewModels;

/// <summary>
/// O servidor de sincronização (ADR-078), aberto pela aba Conta: informar o
/// endereço, testar, salvar, trocar e tirar. Sem servidor o app é inteiro,
/// só neste celular; a tela diz isso em vez de pedir configuração.
/// </summary>
public sealed partial class SyncServerViewModel(
    SyncServerSettings settings,
    ISyncServerProbe probe,
    IUseCaseRunner runner,
    INavigator navigator,
    MobileHostOptions host,
    TimeProvider time,
    IDialogService dialogs,
    ILogger<SyncServerViewModel> logger) : MobileViewModel(dialogs, logger)
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(UrlError), nameof(IsChanged))]
    private string _url = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProbeText), nameof(ProbeFailed), nameof(ProbeSucceeded))]
    private SyncServerProbeResult? _probe;

    [ObservableProperty]
    private bool _isTesting;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasServer), nameof(CurrentText), nameof(StateText), nameof(LastSync), nameof(CanConnect), nameof(IsChanged))]
    private SyncStatusView? _status;

    public bool HasServer => settings.Current is not null;

    public string CurrentText => settings.Current?.AbsoluteUri ?? "Nenhum";

    /// <summary>A frase do topo: desativada, configurada sem conta, conectada, pausada.</summary>
    public string StateText => Status switch
    {
        null => string.Empty,
        _ when !HasServer => "Sincronização desativada. As tarefas ficam só neste celular, e tudo funciona sem internet.",
        { Local.State: SyncLinkState.Unlinked } => "Servidor configurado. Entre com sua conta para começar a sincronizar.",
        { Local: { State: SyncLinkState.NeedsReconnect, LastErrorCode: SyncEngine.ServerChangedCode } } =>
            "O servidor mudou. Entre com sua conta neste servidor para voltar a sincronizar.",
        { Local.State: SyncLinkState.NeedsReconnect } => "O login expirou. Entre de novo para sincronizar.",
        { Local.State: SyncLinkState.DeviceRevoked } => "Este celular foi removido da conta. Entre de novo para sincronizar.",
        { Local.IsEnabled: false } => "Sincronização pausada.",
        { Local.UserEmail: { } email } => $"Sincronizando como {email}.",
        _ => "Sincronizando.",
    };

    public string LastSync => Status?.Local.LastSuccessAt is { } at
        ? "Última sincronização: " + Labels.Instant(at, time.LocalTimeZone)
        : HasServer ? "Ainda não sincronizou com este servidor." : string.Empty;

    public bool CanConnect => HasServer && Status?.Local.State is not SyncLinkState.Linked;

    /// <summary>O erro do formato enquanto digita; campo vazio não é erro, é "sem servidor".</summary>
    public string? UrlError => string.IsNullOrWhiteSpace(Url) ? null : settings.Check(Url).Error;

    /// <summary>O endereço do campo é outro que não o salvo: "Salvar" faz algo.</summary>
    public bool IsChanged => settings.Check(Url).Uri is { } typed && !SyncServerUrl.SameServer(typed, settings.Current);

    public string? ProbeText => Probe?.Message;

    public bool ProbeSucceeded => Probe is { Reachable: true };

    public bool ProbeFailed => Probe is { Reachable: false };

    [RelayCommand]
    public async Task LoadAsync()
    {
        Url = settings.Current?.AbsoluteUri ?? settings.RejectedSaved ?? host.SuggestedServerUrl ?? string.Empty;
        Probe = null;
        await LoadStatusAsync();
    }

    /// <summary>Só lê <c>/health</c> e o discovery: nenhum login, nenhum dado sai do celular.</summary>
    [RelayCommand]
    public async Task TestAsync()
    {
        var check = settings.Check(Url);

        if (check.Uri is not { } server)
        {
            Probe = new SyncServerProbeResult(SyncServerProbeKind.InvalidUrl, check.Error ?? "Endereço inválido.");
            return;
        }

        try
        {
            IsTesting = true;
            Probe = null;
            Probe = await probe.TestAsync(server, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogWarning(exception, "MobileServerTestFailed");
            Probe = new SyncServerProbeResult(SyncServerProbeKind.Unexpected, "Não foi possível testar a conexão.");
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    public async Task SaveAsync()
    {
        var check = settings.Check(Url);

        if (check.Uri is not { } server)
        {
            await Dialogs.AlertAsync("Endereço inválido", check.Error ?? "Confira o endereço do servidor.");
            return;
        }

        if (!IsChanged)
        {
            return;
        }

        if (await ChangeAsync(server, "Trocar de servidor", "Trocar"))
        {
            await Dialogs.ToastAsync("Servidor salvo. Entre com sua conta para sincronizar.");
        }
    }

    [RelayCommand]
    public async Task RemoveAsync()
    {
        if (!HasServer)
        {
            return;
        }

        if (await ChangeAsync(null, "Tirar o servidor", "Tirar"))
        {
            Url = string.Empty;
            Probe = null;
            await Dialogs.ToastAsync("Sincronização desativada. As tarefas continuam neste celular.");
        }
    }

    [RelayCommand]
    public Task ConnectAsync() => navigator.OpenConnectAsync();

    /// <summary>Com uma conta de outro servidor, pergunta antes; sem ela, só grava.</summary>
    private async Task<bool> ChangeAsync(Uri? next, string title, string accept)
    {
        SyncServerChange change;

        try
        {
            change = await settings.PreviewAsync(next, CancellationToken.None);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogError(exception, "MobileServerPreviewFailed");
            await Dialogs.AlertAsync(FailureTitle, UnexpectedFailure);
            return false;
        }

        if (change.NeedsConfirmation
            && !await Dialogs.ConfirmAsync(title, SyncServerSettings.Consequences(change, next), accept, "Cancelar"))
        {
            return false;
        }

        if (!await TryAsync(() => settings.ApplyAsync(next, CancellationToken.None), "MobileServerChangeFailed"))
        {
            return false;
        }

        await LoadStatusAsync();
        return true;
    }

    private async Task LoadStatusAsync()
    {
        try
        {
            Status = await runner.RunAsync<GetSyncStatusHandler, SyncStatusView>(
                (handler, token) => handler.HandleAsync(new GetSyncStatus(), token));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogWarning(exception, "MobileServerStatusFailed");
        }
    }
}
