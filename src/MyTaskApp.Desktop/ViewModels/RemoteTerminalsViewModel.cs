using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Desktop.Terminals;
using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Os terminais do agente da máquina controlada (ADR-099): o mesmo Claude
/// interativo que roda lá, num terminal de verdade. A lista vem do relato do
/// agente; "Seguir" abre a vista, que liga pelo relay do servidor.
/// </summary>
/// <remarks>Só aparece quando a máquina anuncia <c>terminal.attach</c>: a chave "Aceitar o terminal remoto" ligada lá.</remarks>
public sealed partial class RemoteTerminalsViewModel : ObservableObject
{
    /// <summary>Sem hub, a lista é relida a cada tantas voltas do relógio da janela (meio segundo cada).</summary>
    internal const int ListTicks = 10;

    private readonly IUseCaseRunner _runner;
    private readonly ILogger _logger;
    private readonly RemoteTerminalWindows? _windows;

    private Guid? _deviceId;

    /// <summary>A lista de todas as máquinas (ADR-101): os apelidos, e esta máquina, que fica de fora.</summary>
    private IReadOnlyDictionary<Guid, string>? _machines;
    private Guid? _thisDeviceId;
    private IReadOnlySet<Guid>? _offering;
    private int _ticks;

    public RemoteTerminalsViewModel(IUseCaseRunner runner, ILogger logger, RemoteTerminalWindows? windows)
    {
        _runner = runner;
        _logger = logger;
        _windows = windows;
    }

    public ObservableCollection<RemoteTerminalItem> Items { get; } = [];

    /// <summary>A máquina aceita o terminal remoto, e este app sabe abrir a vista.</summary>
    [ObservableProperty]
    private bool _isOffered;

    [ObservableProperty]
    private string? _message;

    public bool HasItems => Items.Count > 0;

    public bool HasNoItems => !HasItems;

    /// <summary>A lista junta as máquinas da conta: cada linha diz de qual é.</summary>
    public bool IsAllMachines => _machines is not null;

    public string EmptyText => IsAllMachines
        ? "Nenhum agente rodando no terminal das outras máquinas."
        : "Nenhum agente rodando no terminal de lá.";

    public async Task LoadAsync(MachineView machine, CancellationToken cancellationToken)
    {
        _machines = null;
        _deviceId = machine.DeviceId;
        IsOffered = machine.OffersTerminal && _windows is not null;

        if (!IsOffered)
        {
            Items.Clear();
            Message = null;
            Notify();
            return;
        }

        await ReloadAsync(cancellationToken);
    }

    /// <summary>
    /// Os terminais de todas as máquinas da conta, menos esta: a daqui se vê
    /// pelo card da tarefa (ADR-101).
    /// </summary>
    /// <param name="offering">
    /// As máquinas que aceitam o terminal remoto: as outras mostram só o estado
    /// (ADR-103), sem "Seguir". <c>null</c> = não se sabe, e o agente de lá decide.
    /// </param>
    public async Task LoadAllAsync(IReadOnlyDictionary<Guid, string> machines, Guid? thisDeviceId, CancellationToken cancellationToken, IReadOnlySet<Guid>? offering = null)
    {
        _machines = machines;
        _thisDeviceId = thisDeviceId;
        _offering = offering;
        _deviceId = null;
        IsOffered = _windows is not null;
        OnPropertyChanged(nameof(IsAllMachines));
        OnPropertyChanged(nameof(EmptyText));

        if (IsOffered)
        {
            await ReloadAsync(cancellationToken);
        }
    }

    /// <summary>Com o hub, relê no aviso da máquina; sem ele, de tempos em tempos.</summary>
    public async Task TickAsync(IReadOnlySet<RemoteNoticeKind> noticed, bool listening, CancellationToken cancellationToken)
    {
        if (!IsOffered)
        {
            return;
        }

        var fallback = ++_ticks % (listening ? RemoteControlViewModel.LiveTicks * 2 : ListTicks) == 0;

        if (fallback || noticed.Contains(RemoteNoticeKind.Terminals) || noticed.Contains(RemoteNoticeKind.Reconnected))
        {
            await ReloadAsync(cancellationToken);
        }
    }

    /// <summary>Abre a vista desta sessão; a mesma, se já está aberta.</summary>
    [RelayCommand]
    public void Follow(RemoteTerminalItem item)
    {
        if ((_deviceId ?? (IsAllMachines ? item.DeviceId : null)) is not { } deviceId || _windows is null)
        {
            return;
        }

        if (!item.CanFollow)
        {
            Message = $"{item.MachineName ?? "A máquina"} mostra o estado, mas não aceita o terminal remoto.";
            return;
        }

        if (item.Target(deviceId) is not { } target)
        {
            Message = "Esta sessão não é de um ambiente de desenvolvimento: não dá para seguir de longe.";
            return;
        }

        if (!_windows.Show(target, $"{item.Title} — terminal de longe"))
        {
            Message = "Não foi possível abrir a vista do terminal.";
        }
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        if (_deviceId is null && !IsAllMachines)
        {
            return;
        }

        try
        {
            var deviceId = _deviceId;
            var list = await _runner.RunAsync<ListRemoteTerminalsHandler, RemoteTerminalList>(
                (handler, token) => handler.HandleAsync(new ListRemoteTerminals(deviceId), token),
                cancellationToken);

            Items.Clear();

            foreach (var item in list.Items)
            {
                if (_machines is not { } machines)
                {
                    Items.Add(item);
                }
                else if (item.DeviceId != _thisDeviceId)
                {
                    Items.Add(item with
                    {
                        MachineName = machines.GetValueOrDefault(item.DeviceId) ?? "Máquina",
                        CanFollow = _offering?.Contains(item.DeviceId) ?? true,
                    });
                }
            }

            Message = list.Problem;
        }
        catch (Exception exception) when (exception is DomainException or SyncApiException)
        {
            Message = exception.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A lista é um extra da janela: sem ela, o resto continua.
            _logger.LogError(exception, "RemoteTerminalsLoadFailed");
            Message = "Não foi possível ler os terminais da outra máquina.";
        }

        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(HasNoItems));
    }
}
