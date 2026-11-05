using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;
using MyTaskApp.Mobile.Core.Composition;
using MyTaskApp.Mobile.Core.Ports;

namespace MyTaskApp.Mobile.Core.ViewModels;

/// <summary>Um Claude rodando no terminal do agente de uma máquina da conta (ADR-099, ADR-101).</summary>
public sealed record TerminalLine(RemoteTerminalItem Item, string Machine)
{
    public string Title => Item.Title;

    public string Detail => $"{Machine} · {Item.State}";

    /// <summary>Um diálogo aberto, ou o Claude esperando: a linha se destaca.</summary>
    public bool NeedsAttention => Item.NeedsAttention;

    public string? BypassWarning => Item.BypassWarning;

    public bool HasBypassWarning => BypassWarning is not null;
}

/// <summary>Uma tecla da barra de cima do teclado: as que o teclado do celular não tem.</summary>
public sealed record TerminalKeyButton(string Label, string Key);

/// <summary>
/// A tela de um terminal do Claude de outra máquina, no celular (ADR-101): o
/// mesmo Claude interativo que roda lá, ao vivo, com a vez de digitar pedida
/// por botão, as teclas que faltam ao teclado do celular, o prompt escrito
/// num campo normal e as respostas do diálogo.
/// </summary>
/// <remarks>
/// O miolo é o <see cref="TerminalSessionClient"/>, o mesmo do desktop. A
/// ligação é um <c>terminal.attach</c> assinado: pede o destravamento uma vez,
/// e as teclas vão assinadas pela chave da ligação, que só existe na memória.
/// Ir para segundo plano solta a ligação (a chave some junto); voltar liga de
/// novo, com outra chave.
/// </remarks>
public sealed partial class RemoteTerminalViewModel : MobileViewModel, IAsyncDisposable
{
    /// <summary>As teclas da barra, na ordem da tela.</summary>
    public static readonly IReadOnlyList<TerminalKeyButton> KeyBar =
    [
        new("Esc", "esc"),
        new("Tab", "tab"),
        new("⇧Tab", "shift-tab"),
        new("↑", "up"),
        new("↓", "down"),
        new("←", "left"),
        new("→", "right"),
        new("⏎", "enter"),
        new("Ctrl+C", "ctrl-c"),
        new("/", "slash"),
    ];

    /// <summary>O menor e o maior tamanho de letra da tela, em pontos.</summary>
    public const double MinFontSize = 6;

    public const double MaxFontSize = 20;

    private static readonly string[] StateProperties =
    [
        nameof(StatusText), nameof(LeaseText), nameof(DialogText), nameof(BypassWarning), nameof(MonitoringWarning), nameof(Message),
        nameof(IsConnected), nameof(IsDisconnected), nameof(IsConnecting), nameof(HasControl), nameof(CanTakeControl), nameof(CanReleaseControl),
        nameof(CanAnswer), nameof(CanInterrupt), nameof(CanPrompt), nameof(CanType), nameof(CanStop), nameof(HasDialog),
        nameof(HasBypassWarning), nameof(HasMonitoringWarning), nameof(HasMessage), nameof(HasLeaseText),
    ];

    private readonly IUseCaseRunner _runner;

    private readonly IRemoteTerminalConnector _connector;

    private readonly MobileLifecycle _lifecycle;

    private readonly IUiDispatcher _dispatcher;

    private TerminalSessionClient? _client;

    /// <summary>A tela está aberta: voltar do segundo plano liga de novo.</summary>
    private bool _open;

    /// <summary>Estava ligado quando o app foi para segundo plano.</summary>
    private bool _droppedInBackground;

    [ObservableProperty]
    private string _title = "Terminal do Claude";

    [ObservableProperty]
    private string _machine = string.Empty;

    /// <summary>O problema antes de ligar: a sessão sumiu, não é de um ambiente, a lista não veio.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLoadError))]
    private string? _loadError;

    /// <summary>O campo do prompt: vai como um prompt pronto, colado e com Enter (ADR-098).</summary>
    [ObservableProperty]
    private string _prompt = string.Empty;

    /// <summary>O tamanho da letra da tela: a pinça muda, e a tela ajusta as colunas que cabem.</summary>
    [ObservableProperty]
    private double _fontSize = 11;

    public RemoteTerminalViewModel(
        IUseCaseRunner runner,
        IRemoteTerminalConnector connector,
        MobileLifecycle lifecycle,
        IUiDispatcher dispatcher,
        IDialogService dialogs,
        ILogger<RemoteTerminalViewModel> logger)
        : base(dialogs, logger)
    {
        _runner = runner;
        _connector = connector;
        _lifecycle = lifecycle;
        _dispatcher = dispatcher;
    }

    /// <summary>A tela mudou: a área do terminal redesenha.</summary>
    public event Action? ScreenChanged;

    public IReadOnlyList<TerminalKeyButton> Keys => KeyBar;

    public Guid DeviceId { get; private set; }

    public Guid SessionId { get; private set; }

    public VtScreen? Screen => _client?.Screen;

    public long Version => _client?.Version ?? 0;

    public bool HasLoadError => LoadError is not null;

    public bool IsConnecting => _client?.Connection == TerminalConnection.Connecting && !HasLoadError;

    public bool IsConnected => _client?.IsConnected == true;

    public bool IsDisconnected => _client?.IsDisconnected == true;

    public string StatusText => LoadError is not null ? "Não foi possível abrir" : _client?.StatusText ?? "Carregando…";

    public string? LeaseText => _client?.LeaseText;

    public bool HasLeaseText => LeaseText is not null;

    public string? DialogText => _client?.DialogText;

    public bool HasDialog => DialogText is not null;

    public string? BypassWarning => _client?.BypassWarning;

    public bool HasBypassWarning => BypassWarning is not null;

    public string? MonitoringWarning => _client?.MonitoringWarning;

    public bool HasMonitoringWarning => MonitoringWarning is not null;

    public string? Message => _client?.Message;

    public bool HasMessage => Message is not null;

    public bool HasControl => _client?.HasControl == true;

    public bool CanTakeControl => _client?.CanTakeControlNow == true;

    public bool CanReleaseControl => _client?.CanReleaseControlNow == true;

    public bool CanAnswer => _client?.CanAnswer == true;

    public bool CanInterrupt => _client?.CanInterrupt == true;

    public bool CanPrompt => _client?.CanPrompt == true;

    /// <summary>Com a vez e a sessão rodando: o teclado e a barra de teclas valem.</summary>
    public bool CanType => _client is { IsReadOnly: false };

    public bool CanStop => _client?.CanStop == true;

    /// <summary>Abre a sessão: acha o ambiente dela na lista e liga. A mesma sessão já aberta só religa se caiu.</summary>
    public async Task OpenAsync(Guid deviceId, Guid sessionId)
    {
        if (!_open)
        {
            // Só a tela aberta ouve o segundo plano: a fechada não segura o ViewModel no ciclo de vida.
            _lifecycle.WentToBackground += OnWentToBackground;
            _lifecycle.CameBack += OnCameBack;
        }

        _open = true;

        if (_client is not null && DeviceId == deviceId && SessionId == sessionId)
        {
            if (_client.IsDisconnected)
            {
                await _client.ConnectAsync();
            }

            return;
        }

        await DropClientAsync();
        DeviceId = deviceId;
        SessionId = sessionId;
        LoadError = null;

        if (await FindAsync(deviceId, sessionId) is not { } target)
        {
            NotifyState();
            return;
        }

        _client = new TerminalSessionClient(
            sessionId,
            TerminalClientKind.Remote,
            (size, token) => _connector.ConnectAsync(target, size, token),
            _dispatcher.Post,
            Logger);
        _client.ScreenChanged += OnScreenChanged;
        _client.StateChanged += NotifyState;
        NotifyState();

        await _client.ConnectAsync();
    }

    /// <summary>A tela saiu: solta a ligação; o Claude segue lá.</summary>
    public async Task CloseAsync()
    {
        _lifecycle.WentToBackground -= OnWentToBackground;
        _lifecycle.CameBack -= OnCameBack;
        _open = false;
        _droppedInBackground = false;
        await DropClientAsync();
    }

    /// <summary>O texto que o teclado do celular digitou, como está.</summary>
    public Task TypeTextAsync(string text) =>
        CanType && !string.IsNullOrEmpty(text) ? _client!.TypeAsync(TerminalKeyBytes.Text(text)) : Task.CompletedTask;

    /// <summary>O Backspace do teclado do celular (o campo invisível não tem o que apagar).</summary>
    public Task BackspaceAsync() =>
        CanType ? _client!.TypeAsync(TerminalKeyBytes.Encode(TerminalKey.Backspace)) : Task.CompletedTask;

    /// <summary>As colunas e linhas que cabem na tela daqui; só valem com a vez e sem a janela de lá (ADR-098).</summary>
    public Task ResizeAsync(TerminalSize size) => _client?.ResizeAsync(size.Clamp()) ?? Task.CompletedTask;

    public async ValueTask DisposeAsync() => await CloseAsync();

    /// <summary>Uma tecla da barra. Ctrl+C é o 0x03 que o Claude lê como interromper.</summary>
    [RelayCommand]
    public Task PressAsync(string? key)
    {
        if (!CanType || _client is null)
        {
            return Task.CompletedTask;
        }

        var cursorKeys = Screen?.ApplicationCursorKeys == true;
        byte[]? bytes = key switch
        {
            "esc" => TerminalKeyBytes.Encode(TerminalKey.Escape),
            "tab" => TerminalKeyBytes.Encode(TerminalKey.Tab),
            "shift-tab" => TerminalKeyBytes.Encode(TerminalKey.Tab, TerminalKeyModifiers.Shift),
            "up" => TerminalKeyBytes.Encode(TerminalKey.Up, applicationCursorKeys: cursorKeys),
            "down" => TerminalKeyBytes.Encode(TerminalKey.Down, applicationCursorKeys: cursorKeys),
            "left" => TerminalKeyBytes.Encode(TerminalKey.Left, applicationCursorKeys: cursorKeys),
            "right" => TerminalKeyBytes.Encode(TerminalKey.Right, applicationCursorKeys: cursorKeys),
            "enter" => TerminalKeyBytes.Encode(TerminalKey.Enter),
            "ctrl-c" => TerminalKeyBytes.Control('c'),
            "slash" => TerminalKeyBytes.Text("/"),
            _ => null,
        };

        return bytes is null ? Task.CompletedTask : _client.TypeAsync(bytes);
    }

    /// <summary>Numa sessão sem as aprovações do Claude Code, pergunta antes: o que for pedido roda lá sem confirmação.</summary>
    [RelayCommand]
    public async Task TakeControlAsync()
    {
        if (_client is not { CanTakeControlNow: true } client)
        {
            return;
        }

        if (client.Status?.PermissionsBypassed == true
            && !await Dialogs.ConfirmAsync(
                "Assumir uma sessão sem as aprovações?",
                "Esta sessão foi aberta sem as aprovações do Claude Code: o que você pedir daqui roda na outra máquina sem perguntar antes. O MyTaskApp não muda isso.",
                "Assumir",
                "Só olhar"))
        {
            return;
        }

        await client.TakeControlAsync();
    }

    [RelayCommand]
    public Task ReleaseControlAsync() => _client?.ReleaseControlAsync() ?? Task.CompletedTask;

    [RelayCommand]
    public Task AllowAsync() => CanAnswer ? _client!.AnswerAsync(allow: true) : Task.CompletedTask;

    [RelayCommand]
    public Task DenyAsync() => CanAnswer ? _client!.AnswerAsync(allow: false) : Task.CompletedTask;

    /// <summary>O Esc do Claude: para o que ele está fazendo, a conversa fica.</summary>
    [RelayCommand]
    public Task InterruptAsync() => CanInterrupt ? _client!.InterruptAsync() : Task.CompletedTask;

    [RelayCommand]
    public async Task SendPromptAsync()
    {
        if (!CanPrompt || string.IsNullOrWhiteSpace(Prompt))
        {
            return;
        }

        var text = Prompt;
        Prompt = string.Empty;
        await _client!.PromptAsync(text);
    }

    [RelayCommand]
    public Task ReconnectAsync() => _client?.ConnectAsync() ?? (_open ? OpenAsync(DeviceId, SessionId) : Task.CompletedTask);

    /// <summary>Como fechar a janela do terminal lá: o Claude recebe o fim e salva a conversa.</summary>
    [RelayCommand]
    public async Task StopAsync()
    {
        if (CanStop
            && await Dialogs.ConfirmAsync("Encerrar o Claude?", "O terminal de lá fecha, e o Claude salva a conversa.", "Encerrar", "Voltar"))
        {
            await _client!.StopAsync(force: false);
        }
    }

    /// <summary>Mata a árvore da sessão, e só ela.</summary>
    [RelayCommand]
    public async Task ForceStopAsync()
    {
        if (CanStop
            && await Dialogs.ConfirmAsync(
                "Forçar o fim?",
                "O Claude e tudo o que ele abriu nesta sessão são encerrados na hora, sem salvar. Os outros programas da máquina não são tocados.",
                "Forçar o fim",
                "Voltar"))
        {
            await _client!.StopAsync(force: true);
        }
    }

    /// <summary>O "⋯": encerrar ou forçar o fim, cada um com a sua pergunta.</summary>
    [RelayCommand]
    public async Task MoreAsync()
    {
        switch (await Dialogs.ChooseAsync("O Claude de lá", ["Encerrar o Claude", "Forçar o fim"]))
        {
            case 0:
                await StopAsync();
                break;
            case 1:
                await ForceStopAsync();
                break;
        }
    }

    [RelayCommand]
    public void ZoomIn() => FontSize = Math.Min(MaxFontSize, FontSize + 1);

    [RelayCommand]
    public void ZoomOut() => FontSize = Math.Max(MinFontSize, FontSize - 1);

    /// <summary>A sessão na lista da máquina: o ambiente, o título da tarefa e o nome da máquina.</summary>
    private async Task<RemoteTerminalTarget?> FindAsync(Guid deviceId, Guid sessionId)
    {
        try
        {
            var list = await _runner.RunAsync<ListRemoteTerminalsHandler, RemoteTerminalList>(
                (handler, token) => handler.HandleAsync(new ListRemoteTerminals(deviceId), token));

            if (list.Problem is { } problem)
            {
                LoadError = problem;
                return null;
            }

            if (list.Items.FirstOrDefault(item => item.Session.SessionId == sessionId) is not { } item)
            {
                LoadError = "Este Claude não está mais no terminal do agente de lá.";
                return null;
            }

            Title = item.Title;
            Machine = await MachineNameAsync(deviceId);

            if (item.Target() is not { } target)
            {
                LoadError = "Esta sessão não é de um ambiente de desenvolvimento: não dá para seguir de longe.";
                return null;
            }

            return target;
        }
        catch (Exception exception) when (exception is DomainException or SyncApiException)
        {
            LoadError = exception.Message;
            return null;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogError(exception, "MobileRemoteTerminalLoadFailed");
            LoadError = UnexpectedFailure;
            return null;
        }
    }

    private async Task<string> MachineNameAsync(Guid deviceId)
    {
        try
        {
            var machines = await _runner.RunAsync<GetMachinesHandler, MachinesView>(
                (handler, token) => handler.HandleAsync(new GetMachines(), token));
            return machines.Machines.FirstOrDefault(machine => machine.DeviceId == deviceId)?.Nickname ?? "Outra máquina";
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            Logger.LogInformation(exception, "MobileRemoteTerminalMachineNameUnavailable");
            return "Outra máquina";
        }
    }

    private async Task DropClientAsync()
    {
        if (_client is { } client)
        {
            _client = null;
            client.ScreenChanged -= OnScreenChanged;
            client.StateChanged -= NotifyState;
            await client.DisposeAsync();
            NotifyState();
            OnScreenChanged();
        }
    }

    /// <summary>Segundo plano: a ligação cai de qualquer jeito; soltar já leva a chave da ligação junto.</summary>
    private void OnWentToBackground()
    {
        if (_client is { IsDisconnected: false } client)
        {
            _droppedInBackground = true;
            _dispatcher.Post(() => _ = client.DisconnectAsync("Em segundo plano: a ligação foi solta. O Claude segue rodando lá."));
        }
    }

    private void OnCameBack()
    {
        if (_open && _droppedInBackground && _client is { } client)
        {
            _droppedInBackground = false;
            _dispatcher.Post(() => _ = client.ConnectAsync());
        }
    }

    private void OnScreenChanged()
    {
        OnPropertyChanged(nameof(Screen));
        OnPropertyChanged(nameof(Version));
        ScreenChanged?.Invoke();
    }

    private void NotifyState()
    {
        foreach (var property in StateProperties)
        {
            OnPropertyChanged(property);
        }
    }
}
