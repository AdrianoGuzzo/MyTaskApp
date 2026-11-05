using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Desktop.Terminals;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// A janela "Conversas do Claude": as conversas de todas as máquinas da conta
/// (ADR-091), como a aba Remoto do celular. Daqui se acompanha e continua
/// qualquer uma — a aberta neste computador, no celular ou em outro — sem
/// antes escolher a máquina em <i>Máquinas → Controlar</i>.
/// <para>
/// Em cima, os terminais do Claude das outras máquinas (ADR-101): a sessão
/// aberta pelo card lá aparece aqui, para seguir e assumir.
/// </para>
/// </summary>
public sealed partial class RemoteConversationsViewModel : ObservableObject
{
    private readonly IUseCaseRunner _runner;
    private readonly ILogger<RemoteConversationsViewModel> _logger;
    private readonly RemotePace _pace;
    private int _ticks;
    private bool _ticking;

    public RemoteConversationsViewModel(
        IUseCaseRunner runner,
        ILogger<RemoteConversationsViewModel> logger,
        IRemoteWatch? watch = null,
        RemoteTerminalWindows? terminals = null)
    {
        _runner = runner;
        _logger = logger;
        _pace = new RemotePace(watch ?? new NoRemoteWatch(), TimeProvider.System);
        Sessions = new RemoteSessionsViewModel(runner, logger);
        Terminals = new RemoteTerminalsViewModel(runner, logger, terminals);
    }

    public RemoteSessionsViewModel Sessions { get; }

    /// <summary>Os terminais do agente das outras máquinas (ADR-101).</summary>
    public RemoteTerminalsViewModel Terminals { get; }

    /// <summary>O servidor não respondeu a lista de máquinas: as linhas ficam sem o nome delas.</summary>
    [ObservableProperty]
    private string? _message;

    public async Task OpenAsync(CancellationToken cancellationToken)
    {
        await _pace.StartAsync(cancellationToken);

        var (machines, thisDeviceId) = await MachinesAsync(cancellationToken);

        await Terminals.LoadAllAsync(machines, thisDeviceId, cancellationToken);
        await Sessions.LoadAllAsync(machines, cancellationToken);
    }

    /// <summary>O relógio da janela, a cada meio segundo: o mesmo ritmo da tela <i>Controlar</i> (ADR-094).</summary>
    public async Task TickAsync(CancellationToken cancellationToken)
    {
        if (_ticking)
        {
            return;
        }

        _ticking = true;

        try
        {
            var noticed = _pace.TakeNotices();

            await Terminals.TickAsync(noticed, _pace.IsListening, cancellationToken);
            await Sessions.TickAsync(++_ticks, noticed, _pace.IsListening, cancellationToken);
        }
        finally
        {
            _ticking = false;
        }
    }

    public ValueTask CloseAsync() => _pace.DisposeAsync();

    private async Task<(IReadOnlyDictionary<Guid, string> Machines, Guid? ThisDeviceId)> MachinesAsync(CancellationToken cancellationToken)
    {
        try
        {
            var view = await _runner.RunAsync<GetMachinesHandler, MachinesView>(
                (handler, token) => handler.HandleAsync(new GetMachines(), token),
                cancellationToken);

            Message = view.Problem;
            return (
                view.Machines.ToDictionary(machine => machine.DeviceId, machine => machine.IsThis ? $"{machine.Nickname} (este)" : machine.Nickname),
                view.Machines.FirstOrDefault(machine => machine.IsThis)?.DeviceId);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogInformation(exception, "RemoteConversationsMachinesUnavailable");
            Message = "Não foi possível ler as máquinas da conta agora.";
            return (new Dictionary<Guid, string>(), null);
        }
    }
}
