using System.Collections.ObjectModel;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Uma conversa na lista: a tarefa, a branch e o estado, nas palavras de <see cref="RemoteSessionTexts"/>.</summary>
public sealed class RemoteSessionLine(RemoteSessionItem item)
{
    public RemoteSessionItem Item { get; } = item;

    public Guid SessionId => Item.Session.SessionId;

    public string Title => Item.Title;

    public string Details => Item.Branch is { } branch ? $"{branch} · {Item.State}" : Item.State;
}

/// <summary>
/// As conversas do Claude da máquina controlada (ADR-091), lidas do servidor:
/// a lista, a linha do tempo da escolhida (os prompts dela em ordem, de
/// qualquer tela que os mandou), o próximo prompt e o parar. Os mesmos
/// handlers da aba Remoto do celular. Fechar a janela não perde a conversa: ela
/// mora no servidor.
/// </summary>
public sealed partial class RemoteSessionsViewModel : ObservableObject
{
    /// <summary>A lista é relida a cada tantas voltas do relógio da janela (meio segundo cada).</summary>
    internal const int ListTicks = 20;

    /// <summary>A linha do tempo de uma conversa viva, a cada tantas voltas.</summary>
    internal const int TimelineTicks = 2;

    private readonly IUseCaseRunner _runner;
    private readonly ILogger _logger;
    private readonly StringBuilder _lines = new();

    private RemoteSessionCursor _cursor = RemoteSessionCursor.Start;
    private bool _live;

    /// <summary>Trocando a linha da conversa escolhida pela relida: não é outra escolha.</summary>
    private bool _refreshing;
    private int _ticks;

    public RemoteSessionsViewModel(IUseCaseRunner runner, ILogger logger)
    {
        _runner = runner;
        _logger = logger;
    }

    public Guid? DeviceId { get; private set; }

    public ObservableCollection<RemoteSessionLine> Items { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection), nameof(CanPrompt), nameof(CanStop), nameof(SelectedState))]
    private RemoteSessionLine? _selected;

    [ObservableProperty]
    private string _timeline = string.Empty;

    [ObservableProperty]
    private string _prompt = string.Empty;

    /// <summary>O servidor não guarda conversas, ou a lista não veio: o que mostrar no lugar dela.</summary>
    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _isBusy;

    /// <summary>O agente de lá, separado do desktop dela: "agente parado desde…" quando os pedidos esperam.</summary>
    [ObservableProperty]
    private string? _agentState;

    public bool HasItems => Items.Count > 0;

    public bool HasSelection => Selected is not null;

    public bool CanPrompt => Selected?.Item.CanPrompt == true;

    public bool CanStop => Selected?.Item.CanStop == true;

    public string? SelectedState => Selected?.Details;

    /// <summary>
    /// A conversa que espera o próximo prompt neste ambiente, a mais nova, fora
    /// as que a tela deixou de lado (<paramref name="except"/>): a janela retoma por ela.
    /// </summary>
    public Guid? WaitingIn(Guid developmentId, IReadOnlySet<Guid> except) =>
        Items.FirstOrDefault(line => line.Item.Session.DevelopmentId == developmentId && line.Item.CanPrompt && !except.Contains(line.SessionId))?.SessionId;

    public async Task LoadAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        DeviceId = deviceId;

        try
        {
            var list = await _runner.RunAsync<ListRemoteSessionsHandler, RemoteSessionList>(
                (handler, token) => handler.HandleAsync(new ListRemoteSessions(deviceId), token),
                cancellationToken);

            var selected = Selected?.SessionId;
            Items.Clear();

            foreach (var item in list.Items)
            {
                Items.Add(new RemoteSessionLine(item));
            }

            Message = list.Unavailable;
            AgentState = list.AgentOf(deviceId);
            var again = Items.FirstOrDefault(line => line.SessionId == selected);

            if (again is not null)
            {
                // A mesma conversa, relida: troca a linha sem recomeçar a linha do tempo.
                Refresh(again);
            }
            else if (again is null)
            {
                Selected = null;
            }
        }
        catch (Exception exception) when (exception is DomainException or SyncApiException)
        {
            Message = exception.Message;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A lista é um extra da janela: sem ela, o resto continua.
            _logger.LogError(exception, "RemoteSessionsLoadFailed");
            Message = "Não foi possível ler as conversas do Claude.";
        }

        OnPropertyChanged(nameof(HasItems));
    }

    /// <summary>A volta do relógio da janela: a linha do tempo da conversa viva e, de tempos em tempos, a lista.</summary>
    public async Task TickAsync(int tick, CancellationToken cancellationToken)
    {
        if (Selected is not null && _live && tick % TimelineTicks == 0)
        {
            await ReadTimelineAsync(cancellationToken);
        }

        if (DeviceId is { } deviceId && ++_ticks % ListTicks == 0)
        {
            await LoadAsync(deviceId, cancellationToken);
        }
    }

    partial void OnSelectedChanged(RemoteSessionLine? value)
    {
        if (_refreshing)
        {
            return;
        }

        _cursor = RemoteSessionCursor.Start;
        _lines.Clear();
        Timeline = string.Empty;
        _live = value is not null;

        if (value is not null)
        {
            _ = ReadTimelineAsync(CancellationToken.None);
        }
    }

    [RelayCommand]
    public async Task SendPromptAsync()
    {
        if (DeviceId is not { } deviceId || Selected is not { } line || string.IsNullOrWhiteSpace(Prompt))
        {
            return;
        }

        if (await SendAsync(RemoteRequests.PromptAgent(deviceId, line.Item.Session.TaskId, line.SessionId, Prompt)))
        {
            Prompt = string.Empty;
        }
    }

    [RelayCommand]
    public async Task StopAsync()
    {
        if (DeviceId is { } deviceId && Selected is { } line)
        {
            await SendAsync(RemoteRequests.StopAgent(deviceId, line.Item.Session.TaskId, line.SessionId));
        }
    }

    private async Task<bool> SendAsync(SendRemoteOperation request)
    {
        Message = null;
        IsBusy = true;

        try
        {
            await _runner.RunAsync<SendRemoteOperationHandler, RemoteOperationView>(
                (handler, token) => handler.HandleAsync(request, token),
                CancellationToken.None);

            // A operação nova entra na conversa no servidor: a linha do tempo volta a ler.
            _live = true;
            return true;
        }
        catch (Exception exception) when (exception is DomainException or SyncApiException)
        {
            Message = exception.Message;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "RemoteSessionSendFailed");
            Message = "Não foi possível enviar o pedido.";
        }
        finally
        {
            IsBusy = false;
        }

        return false;
    }

    private async Task ReadTimelineAsync(CancellationToken cancellationToken)
    {
        if (Selected is not { } line)
        {
            return;
        }

        try
        {
            var timeline = await _runner.RunAsync<GetRemoteSessionTimelineHandler, RemoteSessionTimeline>(
                (handler, token) => handler.HandleAsync(new GetRemoteSessionTimeline(line.SessionId, _cursor), token),
                cancellationToken);

            if (Selected?.SessionId != line.SessionId)
            {
                return;
            }

            _cursor = timeline.Cursor;
            _live = timeline.IsLive || timeline.Session.Session.Status is RemoteAgentSessionStatus.Starting or RemoteAgentSessionStatus.Running;

            foreach (var text in timeline.Lines)
            {
                _lines.Append(text).Append('\n');
            }

            if (_lines.Length > RemoteControlViewModel.MaxOutputLength)
            {
                var cut = _lines.ToString().IndexOf('\n', _lines.Length - RemoteControlViewModel.MaxOutputLength);
                _lines.Remove(0, cut < 0 ? _lines.Length - RemoteControlViewModel.MaxOutputLength : cut + 1);
            }

            if (timeline.Lines.Count > 0)
            {
                Timeline = _lines.ToString();
            }

            var index = Items.IndexOf(line);

            if (index >= 0)
            {
                var fresh = new RemoteSessionLine(timeline.Session);
                Items[index] = fresh;
                Refresh(fresh);
            }
        }
        catch (SyncApiException exception)
        {
            _logger.LogInformation("RemoteSessionTimelineUnavailable {Kind}", exception.Kind);
        }
    }

    private void Refresh(RemoteSessionLine line)
    {
        _refreshing = true;

        try
        {
            Selected = line;
        }
        finally
        {
            _refreshing = false;
        }
    }
}
