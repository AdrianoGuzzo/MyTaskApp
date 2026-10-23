using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>O banco local para a aba Conexões preencher. Sem senha quando o container já existia antes desta janela.</summary>
public sealed record LocalDatabaseDraft(string Host, int Port, string Username, string? Password);

/// <summary>Um passo na tela, numerado: "2. Cole o comando abaixo…" e o comando com o botão de copiar.</summary>
public sealed record InstallStepItem(int Number, string Text, string? Command)
{
    public string Label => $"{Number}. {Text}";

    public bool HasCommand => !string.IsNullOrEmpty(Command);
}

/// <summary>
/// A aba "Diagnóstico" — "PostgreSQL Environment" (ADR-056): as ferramentas
/// desta máquina e o servidor de uma conexão, item por item,
/// com as instruções quando falta algo. E o Docker do banco local (ADR-083):
/// como instalar, como ligar e o comando que cria o banco.
/// </summary>
public sealed partial class DatabaseDiagnosticsViewModel(
    IUseCaseRunner runner,
    IClipboardWriter clipboard,
    IShellLauncher shell,
    ILogger<DatabaseDiagnosticsViewModel> logger) : ObservableObject
{
    /// <summary>
    /// Uma por janela: é a que vai no <c>docker run</c> e no cadastro. Nunca vai
    /// para log nem para o SQLite — só para o cofre, se o usuário salvar a conexão.
    /// </summary>
    private readonly string _localPassword = LocalPostgresContainer.NewPassword();

    /// <summary>
    /// O container não existia quando esta janela olhou: se aparecer depois,
    /// foi o comando daqui que o criou, com <see cref="_localPassword"/>.
    /// </summary>
    private bool _sawNoContainer;

    private DockerEnvironment? _docker;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(DiagnoseCommand))]
    private DatabaseConnectionItemViewModel? _selectedConnection;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMissingTools))]
    private bool _toolsMissing;

    [ObservableProperty]
    private string _platform = string.Empty;

    public ObservableCollection<DatabaseConnectionItemViewModel> Connections { get; } = [];

    public ObservableCollection<CheckItemViewModel> ClientTools { get; } = [];

    public ObservableCollection<CheckItemViewModel> Server { get; } = [];

    [ObservableProperty]
    private Uri? _officialSite;

    public ObservableCollection<InstallStepItem> InstallSteps { get; } = [];

    public ObservableCollection<string> CheckCommands { get; } = [];

    public bool HasMissingTools => ToolsMissing;

    public bool HasServer => Server.Count > 0;

    public ObservableCollection<CheckItemViewModel> DockerChecks { get; } = [];

    /// <summary>Instalar ou ligar o Docker; vazio com ele rodando.</summary>
    public ObservableCollection<InstallStepItem> DockerSteps { get; } = [];

    /// <summary>Criar, ligar ou cuidar do banco local.</summary>
    public ObservableCollection<InstallStepItem> LocalDatabaseSteps { get; } = [];

    public bool HasDocker => _docker is not null;

    public bool HasDockerSteps => DockerSteps.Count > 0;

    public Uri? DockerOfficialSite => _docker?.Guide.OfficialSite;

    public string DockerStepsTitle => _docker is { Installed: false } ? "Primeiro, instale o Docker" : "Primeiro, ligue o Docker";

    public string LocalDatabaseTitle => _docker switch
    {
        { EngineRunning: false } => "Depois, crie o banco",
        { Container: LocalContainerState.Running } => "O banco local está pronto",
        { Container: LocalContainerState.Stopped } => "O banco local está parado",
        _ => "Crie o banco",
    };

    public void SetConnections(IReadOnlyList<DatabaseConnectionItemViewModel> connections)
    {
        var selected = SelectedConnection?.Id;
        Connections.Clear();

        foreach (var connection in connections)
        {
            Connections.Add(connection);
        }

        SelectedConnection = Connections.FirstOrDefault(connection => connection.Id == selected) ?? Connections.FirstOrDefault();
    }

    /// <summary>As ferramentas locais: não precisa de conexão. <paramref name="refresh"/> = "Verificar novamente".</summary>
    [RelayCommand]
    public async Task CheckToolsAsync(bool refresh = false, CancellationToken cancellationToken = default)
    {
        EnvironmentDiagnosticsReport? report = null;

        await TryAsync(
            async () => report = await runner.RunAsync<DetectPostgresToolsHandler, EnvironmentDiagnosticsReport>(
                (handler, token) => handler.HandleAsync(new DetectPostgresTools(refresh), token), cancellationToken),
            "Não foi possível procurar as ferramentas do PostgreSQL.");

        if (report is not null)
        {
            Show(report, includeServer: false);
        }
    }

    [RelayCommand]
    public Task RefreshAsync(CancellationToken cancellationToken = default) =>
        SelectedConnection is null ? CheckToolsAsync(refresh: true, cancellationToken) : DiagnoseCoreAsync(refresh: true, cancellationToken);

    private bool CanDiagnose() => SelectedConnection is not null;

    [RelayCommand(CanExecute = nameof(CanDiagnose))]
    public Task DiagnoseAsync(CancellationToken cancellationToken = default) => DiagnoseCoreAsync(refresh: false, cancellationToken);

    [RelayCommand]
    public async Task CopyCheckCommandsAsync()
    {
        try
        {
            await clipboard.WriteAsync(string.Join(Environment.NewLine, CheckCommands));
            StatusMessage = "Comandos copiados.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            ErrorMessage = "Não foi possível copiar.";
        }
    }

    [RelayCommand]
    public async Task CopyInstallCommandAsync(string? command)
    {
        if (string.IsNullOrEmpty(command))
        {
            return;
        }

        try
        {
            await clipboard.WriteAsync(command);
            ErrorMessage = null;
            StatusMessage = "Comando copiado. Cole no terminal e aperte Enter.";
        }
        catch (Exception exception) when (exception is InvalidOperationException or NotSupportedException)
        {
            ErrorMessage = "Não foi possível copiar.";
        }
    }

    [RelayCommand]
    public async Task OpenPageAsync(Uri? uri)
    {
        if (uri is not null && !await shell.OpenUriAsync(uri))
        {
            ErrorMessage = $"Não foi possível abrir o navegador. O endereço é {uri}";
        }
    }

    /// <summary>A aba Conexões assina: abre o formulário preenchido para o banco local, sem salvar nada.</summary>
    public event Action<LocalDatabaseDraft>? RegisterLocalDatabaseRequested;

    [RelayCommand]
    public void RegisterLocalDatabase()
    {
        // Um container de antes desta janela foi criado com outra senha, que o app não sabe.
        var ours = _sawNoContainer || _docker is null || _docker.Container is LocalContainerState.Missing or LocalContainerState.Unknown;

        RegisterLocalDatabaseRequested?.Invoke(new LocalDatabaseDraft(
            LocalPostgresContainer.Host,
            _docker?.LocalPort ?? LocalPostgresContainer.PreferredPort,
            LocalPostgresContainer.User,
            ours ? _localPassword : null));
    }

    private async Task DiagnoseCoreAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (SelectedConnection is not { } connection)
        {
            return;
        }

        EnvironmentDiagnosticsReport? report = null;

        await TryAsync(
            async () => report = await runner.RunAsync<DiagnoseDatabaseHandler, EnvironmentDiagnosticsReport>(
                (handler, token) => handler.HandleAsync(new DiagnoseDatabase(connection.Id, refresh), token), cancellationToken),
            "Não foi possível diagnosticar a conexão.");

        if (report is not null)
        {
            Show(report, includeServer: true);
            StatusMessage = report.HasFailures ? "Há itens com problema." : "Tudo certo.";
        }
    }

    private void Show(EnvironmentDiagnosticsReport report, bool includeServer)
    {
        Fill(ClientTools, report.ClientTools);

        if (includeServer)
        {
            Fill(Server, report.Server);
        }

        Platform = report.Guide.Platform;
        OfficialSite = report.Guide.OfficialSite;
        ToolsMissing = report.ClientTools.Any(check => check.Outcome == CheckOutcome.Fail);
        Fill(InstallSteps, Numbered(report.Guide.Steps));
        Fill(CheckCommands, report.Guide.CheckCommands);
        OnPropertyChanged(nameof(HasServer));
        ShowDocker(report.Docker);
    }

    private void ShowDocker(DockerEnvironment? docker)
    {
        _docker = docker;

        if (docker is { Installed: false } or { EngineRunning: true, Container: LocalContainerState.Missing })
        {
            _sawNoContainer = true;
        }

        Fill(DockerChecks, docker?.Checks ?? []);
        Fill(DockerSteps, Numbered(docker?.Guide.Steps ?? []));

        // Sem o Docker rodando, os passos do banco vêm junto: o tutorial inteiro de uma vez.
        Fill(LocalDatabaseSteps, Numbered(docker is null
            ? []
            : LocalPostgresContainer.Steps(docker.EngineRunning ? docker.Container : LocalContainerState.Unknown, _localPassword, docker.LocalPort)));

        OnPropertyChanged(nameof(HasDocker));
        OnPropertyChanged(nameof(HasDockerSteps));
        OnPropertyChanged(nameof(DockerOfficialSite));
        OnPropertyChanged(nameof(DockerStepsTitle));
        OnPropertyChanged(nameof(LocalDatabaseTitle));
    }

    private static void Fill(ObservableCollection<CheckItemViewModel> target, IEnumerable<CheckResult> checks)
    {
        target.Clear();

        foreach (var check in checks)
        {
            target.Add(new CheckItemViewModel(check));
        }
    }

    private static IEnumerable<InstallStepItem> Numbered(IEnumerable<InstallStep> steps) =>
        steps.Select((step, index) => new InstallStepItem(index + 1, step.Text, step.Command));

    private static void Fill<T>(ObservableCollection<T> target, IEnumerable<T> lines)
    {
        target.Clear();

        foreach (var line in lines)
        {
            target.Add(line);
        }
    }

    private async Task TryAsync(Func<Task> operation, string fallbackMessage)
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            await operation();
        }
        catch (OperationCanceledException)
        {
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "DatabaseDiagnosticsFailed");
            ErrorMessage = fallbackMessage;
        }
        finally
        {
            IsBusy = false;
        }
    }
}

/// <summary>A aba "Histórico" (ADR-056): as operações anteriores, do mais recente ao mais antigo.</summary>
public sealed partial class DatabaseHistoryViewModel(
    IUseCaseRunner runner,
    ILogger<DatabaseHistoryViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    private string? _errorMessage;

    public ObservableCollection<DatabaseOperationItemViewModel> Operations { get; } = [];

    public bool IsEmpty => Operations.Count == 0;

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;

        try
        {
            var rows = await runner.RunAsync<GetDatabaseOperationHistoryHandler, IReadOnlyList<DatabaseOperationRow>>(
                (handler, token) => handler.HandleAsync(new GetDatabaseOperationHistory(), token), cancellationToken);

            Operations.Clear();

            foreach (var row in rows)
            {
                Operations.Add(new DatabaseOperationItemViewModel(row));
            }

            OnPropertyChanged(nameof(IsEmpty));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "DatabaseHistoryLoadFailed");
            ErrorMessage = "Não foi possível carregar o histórico.";
        }
    }
}
