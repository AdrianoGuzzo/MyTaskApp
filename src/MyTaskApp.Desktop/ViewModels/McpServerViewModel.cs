using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Mcp;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Mcp.Hosting;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Uma linha de "Logs recentes".</summary>
public sealed record McpActivityRow(string Time, string Operation, string Outcome, string Elapsed, string? Detail, bool IsProblem)
{
    public string Summary => Detail is null ? $"{Time}  {Operation} · {Outcome}" : $"{Time}  {Operation} · {Outcome} — {Detail}";
}

/// <summary>As ferramentas de uma área, para a lista da tela.</summary>
public sealed record McpToolGroup(string Area, IReadOnlyList<McpToolDescriptor> Tools)
{
    public string Header => $"{Area} ({Tools.Count})";
}

/// <summary>
/// A janela "Servidor MCP…" (ADR-059): habilitar, porta, iniciar e parar, o
/// estado real do servidor, o token e as instruções para o cliente.
/// </summary>
/// <remarks>
/// O estado mostrado é sempre o do <see cref="IMcpServerManager"/>, e não o da
/// configuração: "habilitado" é desejo, "ativo" é fato. Nada aqui bloqueia a
/// thread da tela — subir e parar o Kestrel são assíncronos no gerente.
/// </remarks>
public sealed partial class McpServerViewModel : ObservableObject
{
    private readonly IUseCaseRunner _runner;
    private readonly IMcpServerManager _manager;
    private readonly IClipboardWriter _clipboard;
    private readonly IConfirmationDialog _confirmation;
    private readonly ILogger<McpServerViewModel> _logger;

    /// <summary>A troca de valor vinda da carga, que não deve gravar de volta.</summary>
    private bool _loading;

    private string? _token;

    public McpServerViewModel(
        IUseCaseRunner runner,
        IMcpServerManager manager,
        IClipboardWriter clipboard,
        IConfirmationDialog confirmation,
        ILogger<McpServerViewModel> logger)
    {
        _runner = runner;
        _manager = manager;
        _clipboard = clipboard;
        _confirmation = confirmation;
        _logger = logger;

        ToolGroups = manager.Tools
            .GroupBy(tool => tool.Area)
            .Select(group => new McpToolGroup(group.Key, group.ToList()))
            .ToList();

        // O gerente vive com o app, e este ViewModel também (singleton): a
        // assinatura não prende nada que deveria morrer antes.
        manager.StatusChanged += status => OnUi(() => Show(status));
        manager.Activity.Changed += QueueActivityRefresh;

        Show(manager.Status);
        RefreshActivity();
    }

    [ObservableProperty]
    private bool _isEnabled;

    [ObservableProperty]
    private bool _startWithApp;

    [ObservableProperty]
    private bool _isReadOnly;

    [ObservableProperty]
    private string _portText = McpServerSettings.DefaultPort.ToString(CultureInfo.InvariantCulture);

    /// <summary>A porta gravada — a do endereço que o cliente usa.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(EndpointText))]
    private int _savedPort = McpServerSettings.DefaultPort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateLabel), nameof(IsRunning), nameof(IsStopped), nameof(IsFailed), nameof(IsTransitioning), nameof(EndpointText), nameof(CanStart))]
    private McpServerStatus _status = McpServerStatus.Stopped(DateTimeOffset.UtcNow);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart))]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    private string? _portCheckMessage;

    [ObservableProperty]
    private bool _portCheckFailed;

    [ObservableProperty]
    private string _tokenLabel = "—";

    [ObservableProperty]
    private McpActivityRow? _selectedActivity;

    public ObservableCollection<McpActivityRow> Activity { get; } = [];

    public bool HasActivity => Activity.Count > 0;

    public IReadOnlyList<McpToolGroup> ToolGroups { get; }

    public int ToolCount => ToolGroups.Sum(group => group.Tools.Count);

    public bool IsRunning => Status.State is McpServerState.Running;

    public bool IsStopped => Status.State is McpServerState.Stopped;

    public bool IsFailed => Status.State is McpServerState.Error;

    public bool IsTransitioning => Status.State is McpServerState.Starting or McpServerState.Stopping;

    public bool CanStart => IsEnabled && !IsRunning && !IsTransitioning && !IsBusy;

    public string StateLabel => Status.State switch
    {
        McpServerState.Starting => "Iniciando…",
        McpServerState.Running => "Ativo",
        McpServerState.Error => "Erro",
        McpServerState.Stopping => "Encerrando…",
        _ => "Parado",
    };

    /// <summary>O endereço no ar, ou o que será usado quando subir.</summary>
    public string EndpointText => (Status.Endpoint ?? McpServerSettings.EndpointFor(SavedPort)).ToString();

    public static string PortHelp =>
        "Porta em uso? \"Testar porta\" confirma o conflito. Escolha outra porta (de 1024 a 65535) e aplique, ou feche o programa " +
        "que a usa — no PowerShell, Get-NetTCPConnection -LocalPort <porta> mostra o processo (OwningProcess). Depois de mudar a " +
        "porta, atualize o endereço no cliente MCP.";

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await TryAsync(
            async () =>
            {
                var settings = await _runner.RunAsync<GetMcpServerSettingsHandler, McpServerSettings>(
                    (handler, token) => handler.HandleAsync(new GetMcpServerSettings(), token),
                    cancellationToken);

                ShowSettings(settings);

                if (settings.Enabled)
                {
                    await LoadTokenAsync(cancellationToken);
                }
            },
            "Não foi possível carregar a configuração do servidor MCP.");

        Show(_manager.Status);
        RefreshActivity();
    }

    partial void OnIsEnabledChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStart));
        SaveToggles();
    }

    partial void OnStartWithAppChanged(bool value) => SaveToggles();

    partial void OnIsReadOnlyChanged(bool value) => SaveToggles();

    /// <summary>Aplica a porta digitada: grava e, se o servidor estiver no ar, reinicia nela.</summary>
    [RelayCommand]
    private Task ApplyPortAsync() =>
        TryAsync(
            async () =>
            {
                var port = ParsePort();
                await SaveAsync(IsEnabled, port, StartWithApp, IsReadOnly);
                StatusMessage = IsRunning ? $"Servidor reiniciado na porta {port}." : $"Porta {port} gravada.";
            },
            "Não foi possível gravar a porta.");

    [RelayCommand]
    private Task TestPortAsync() =>
        TryAsync(
            async () =>
            {
                var check = await _manager.CheckPortAsync(ParsePort());
                PortCheckMessage = check.Message;
                PortCheckFailed = !check.IsAvailable;
            },
            "Não foi possível testar a porta.");

    [RelayCommand]
    private Task StartAsync() =>
        TryAsync(
            async () =>
            {
                var status = await _manager.StartAsync();
                await LoadTokenAsync(CancellationToken.None);
                StatusMessage = status.IsRunning ? "Servidor MCP no ar." : null;
            },
            "Não foi possível iniciar o servidor MCP.");

    [RelayCommand]
    private Task StopAsync() =>
        TryAsync(
            async () =>
            {
                await _manager.StopAsync();
                StatusMessage = "Servidor MCP parado.";
            },
            "Não foi possível parar o servidor MCP.");

    [RelayCommand]
    private Task CopyEndpointAsync() => CopyAsync(EndpointText, "Endereço copiado.");

    /// <summary>O comando do Claude Code e o JSON para os outros clientes, já com o token.</summary>
    [RelayCommand]
    private Task CopyClientConfigAsync() =>
        TryAsync(
            async () =>
            {
                var token = await LoadTokenAsync(CancellationToken.None);
                await CopyAsync(ClientInstructions(EndpointText, token), "Instruções copiadas — elas contêm o token: cole só no seu cliente MCP.");
            },
            "Não foi possível montar as instruções.");

    [RelayCommand]
    private Task CopyTokenAsync() =>
        TryAsync(
            async () => await CopyAsync(await LoadTokenAsync(CancellationToken.None), "Token copiado."),
            "Não foi possível ler o token.");

    [RelayCommand]
    private async Task RegenerateTokenAsync()
    {
        var confirmed = await _confirmation.AskAsync(new ConfirmationRequest(
            "Gerar um token novo?",
            "Os clientes MCP configurados com o token atual deixam de entrar na hora, até você atualizar a configuração deles.",
            "Gerar token novo"));

        if (!confirmed)
        {
            return;
        }

        await TryAsync(
            async () =>
            {
                _token = await _manager.RegenerateAccessTokenAsync();
                TokenLabel = Mask(_token);
                StatusMessage = "Token novo gerado. Atualize a configuração do cliente MCP.";
            },
            "Não foi possível gerar o token.");
    }

    [RelayCommand]
    private void ClearActivity() => _manager.Activity.Clear();

    /// <summary>O texto copiado em "Copiar configuração do cliente".</summary>
    public static string ClientInstructions(string endpoint, string token) =>
        $$"""
        # Claude Code (terminal)
        claude mcp add --transport http mytaskapp {{endpoint}} --header "Authorization: Bearer {{token}}"

        # Outros clientes (JSON com transporte HTTP)
        {
          "mcpServers": {
            "mytaskapp": {
              "type": "http",
              "url": "{{endpoint}}",
              "headers": { "Authorization": "Bearer {{token}}" }
            }
          }
        }
        """;

    internal static string Mask(string token) => token.Length <= 4 ? "••••" : $"••••••••{token[^4..]}";

    private void SaveToggles()
    {
        if (_loading)
        {
            return;
        }

        _ = TryAsync(
            () => SaveAsync(IsEnabled, SavedPort, StartWithApp, IsReadOnly),
            "Não foi possível gravar a configuração do servidor MCP.");
    }

    private async Task SaveAsync(bool enabled, int port, bool startWithApp, bool readOnly)
    {
        var settings = await _runner.RunAsync<UpdateMcpServerSettingsHandler, McpServerSettings>(
            (handler, token) => handler.HandleAsync(new UpdateMcpServerSettings(enabled, port, startWithApp, readOnly), token),
            CancellationToken.None);

        ShowSettings(settings);

        // A configuração é o desejo; o gerente faz dela o estado.
        Show(await _manager.ApplySettingsAsync(settings));

        if (settings.Enabled)
        {
            await LoadTokenAsync(CancellationToken.None);
        }
    }

    private async Task<string> LoadTokenAsync(CancellationToken cancellationToken)
    {
        _token ??= await _manager.GetAccessTokenAsync(cancellationToken);
        TokenLabel = Mask(_token);
        return _token;
    }

    private int ParsePort() =>
        int.TryParse(PortText?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            ? port
            : throw new DomainException("Digite a porta só com números, de 1024 a 65535.");

    private void ShowSettings(McpServerSettings settings)
    {
        _loading = true;

        try
        {
            IsEnabled = settings.Enabled;
            StartWithApp = settings.StartWithApp;
            IsReadOnly = settings.ReadOnly;
            SavedPort = settings.Port;
            PortText = settings.Port.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Show(McpServerStatus status) => Status = status;

    private void RefreshActivity()
    {
        Activity.Clear();

        foreach (var entry in _manager.Activity.Snapshot())
        {
            Activity.Add(new McpActivityRow(
                entry.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                entry.Operation,
                entry.Outcome switch
                {
                    McpActivityOutcome.Succeeded => "ok",
                    McpActivityOutcome.Rejected => "recusado",
                    McpActivityOutcome.Failed => "erro",
                    McpActivityOutcome.Denied => "barrado",
                    _ => "servidor",
                },
                entry.Elapsed == TimeSpan.Zero ? string.Empty : $"{entry.Elapsed.TotalMilliseconds:0} ms",
                entry.Detail,
                entry.Outcome is McpActivityOutcome.Failed or McpActivityOutcome.Denied));
        }

        OnPropertyChanged(nameof(HasActivity));
    }

    private async Task CopyAsync(string text, string done)
    {
        try
        {
            await _clipboard.WriteAsync(text);
            StatusMessage = done;
        }
        catch (InvalidOperationException)
        {
            ErrorMessage = "Não foi possível acessar a área de transferência.";
        }
    }

    private async Task<bool> TryAsync(Func<Task> operation, string fallbackMessage)
    {
        IsBusy = true;
        ErrorMessage = null;

        try
        {
            await operation();
            return true;
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "McpServerScreenOperationFailed");
            ErrorMessage = fallbackMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Uma recarga pendente por vez: mil registros seguidos viram uma
    /// reconstrução da lista, e não mil na fila da tela.
    /// </summary>
    private void QueueActivityRefresh()
    {
        if (Interlocked.Exchange(ref _activityRefreshQueued, 1) == 1)
        {
            return;
        }

        OnUi(() =>
        {
            Volatile.Write(ref _activityRefreshQueued, 0);
            RefreshActivity();
        });
    }

    private int _activityRefreshQueued;

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
