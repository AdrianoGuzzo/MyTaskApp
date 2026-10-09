using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Domain;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// A aba "Diagnóstico" — "PostgreSQL Environment" (ADR-056): as ferramentas
/// desta máquina, o servidor e o Anonymizer de uma conexão, item por item,
/// com as instruções quando falta algo.
/// </summary>
public sealed partial class DatabaseDiagnosticsViewModel(
    IUseCaseRunner runner,
    IClipboardWriter clipboard,
    ILogger<DatabaseDiagnosticsViewModel> logger) : ObservableObject
{
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

    public ObservableCollection<CheckItemViewModel> Anonymizer { get; } = [];

    public ObservableCollection<string> InstallSteps { get; } = [];

    public ObservableCollection<string> CheckCommands { get; } = [];

    public bool HasMissingTools => ToolsMissing;

    public bool HasServer => Server.Count > 0;

    public bool HasAnonymizer => Anonymizer.Count > 0;

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

    private async Task DiagnoseCoreAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (SelectedConnection is not { } connection)
        {
            return;
        }

        EnvironmentDiagnosticsReport? report = null;

        await TryAsync(
            async () => report = await runner.RunAsync<DiagnoseDatabaseHandler, EnvironmentDiagnosticsReport>(
                (handler, token) => handler.HandleAsync(new DiagnoseDatabase(connection.Id, null, refresh), token), cancellationToken),
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
            Fill(Anonymizer, report.Anonymizer);
        }

        Platform = report.Guide.Platform;
        ToolsMissing = report.ClientTools.Any(check => check.Outcome == CheckOutcome.Fail);
        Fill(InstallSteps, report.Guide.Steps);
        Fill(CheckCommands, report.Guide.CheckCommands);
        OnPropertyChanged(nameof(HasServer));
        OnPropertyChanged(nameof(HasAnonymizer));
    }

    private static void Fill(ObservableCollection<CheckItemViewModel> target, IEnumerable<CheckResult> checks)
    {
        target.Clear();

        foreach (var check in checks)
        {
            target.Add(new CheckItemViewModel(check));
        }
    }

    private static void Fill(ObservableCollection<string> target, IEnumerable<string> lines)
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
