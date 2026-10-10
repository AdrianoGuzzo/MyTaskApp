using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// A janela "Bancos de Dados…" (ADR-056): as cinco abas e o cadastro que elas
/// compartilham. Cada aba é um ViewModel; este só carrega e redistribui.
/// </summary>
public sealed partial class DatabaseOperationsViewModel : ObservableObject
{
    public const int ConnectionsTab = 0;
    public const int DiagnosticsTab = 1;
    public const int CopyTab = 2;
    public const int ProfilesTab = 3;
    public const int HistoryTab = 4;

    private readonly IUseCaseRunner _runner;

    private readonly ILogger<DatabaseOperationsViewModel> _logger;

    private bool _recovered;

    [ObservableProperty]
    private int _selectedTabIndex;

    [ObservableProperty]
    private string? _errorMessage;

    public DatabaseOperationsViewModel(
        IUseCaseRunner runner,
        DatabaseConnectionsViewModel connections,
        DatabaseDiagnosticsViewModel diagnostics,
        DatabaseCopyViewModel copy,
        DatabaseProfilesViewModel profiles,
        DatabaseHistoryViewModel history,
        ILogger<DatabaseOperationsViewModel> logger)
    {
        _runner = runner;
        _logger = logger;
        Connections = connections;
        Diagnostics = diagnostics;
        Copy = copy;
        Profiles = profiles;
        History = history;

        Connections.Changed += Reload;
        Profiles.Changed += Reload;
        Copy.Changed += Reload;
        Copy.Finished += () => _ = History.LoadAsync(CancellationToken.None);
        Profiles.UseRequested += profile =>
        {
            Copy.UseProfile(profile);
            SelectedTabIndex = CopyTab;
        };
        Profiles.UseSavedDatabaseRequested += saved =>
        {
            Copy.UseSavedDatabase(saved);
            SelectedTabIndex = CopyTab;
        };
    }

    public DatabaseConnectionsViewModel Connections { get; }

    public DatabaseDiagnosticsViewModel Diagnostics { get; }

    public DatabaseCopyViewModel Copy { get; }

    public DatabaseProfilesViewModel Profiles { get; }

    public DatabaseHistoryViewModel History { get; }

    /// <summary>
    /// A cada abertura. Na primeira, antes de tudo, as operações que uma queda
    /// deixou "em andamento" viram interrompidas e os dumps esquecidos são apagados.
    /// </summary>
    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;

        try
        {
            if (!_recovered)
            {
                await _runner.RunAsync<RecoverInterruptedDatabaseOperationsHandler, int>(
                    (handler, token) => handler.HandleAsync(new RecoverInterruptedDatabaseOperations(), token), cancellationToken);
                _recovered = true;
            }

            var connections = await _runner.RunAsync<GetDatabaseConnectionsHandler, IReadOnlyList<DatabaseConnectionRow>>(
                (handler, token) => handler.HandleAsync(new GetDatabaseConnections(), token), cancellationToken);
            var anonymization = await _runner.RunAsync<GetAnonymizationProfilesHandler, IReadOnlyList<AnonymizationProfileRow>>(
                (handler, token) => handler.HandleAsync(new GetAnonymizationProfiles(), token), cancellationToken);
            var copies = await _runner.RunAsync<GetDatabaseCopyProfilesHandler, IReadOnlyList<DatabaseCopyProfileRow>>(
                (handler, token) => handler.HandleAsync(new GetDatabaseCopyProfiles(), token), cancellationToken);
            var saved = await _runner.RunAsync<GetSavedDatabasesHandler, IReadOnlyList<SavedDatabaseRow>>(
                (handler, token) => handler.HandleAsync(new GetSavedDatabases(), token), cancellationToken);

            Connections.SetConnections(connections);
            var items = Connections.Connections.ToList();
            Diagnostics.SetConnections(items);
            Copy.SetCatalog(items, anonymization, copies, saved);
            Profiles.SetCatalog(items, anonymization, copies, saved);

            await History.LoadAsync(cancellationToken);

            if (Diagnostics.ClientTools.Count == 0)
            {
                await Diagnostics.CheckToolsAsync(refresh: false, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "DatabaseOperationsLoadFailed");
            ErrorMessage = "Não foi possível carregar as conexões de banco.";
        }
    }

    /// <summary>O app está fechando: a cópia em andamento para, para a limpeza rodar.</summary>
    public void CancelForShutdown() => Copy.CancelForShutdown();

    private void Reload() => _ = LoadAsync(CancellationToken.None);
}
