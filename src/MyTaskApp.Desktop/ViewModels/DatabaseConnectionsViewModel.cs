using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// A aba "Conexões" (ADR-056): a lista com o ambiente de cada uma, e o
/// formulário que cadastra, testa e edita.
/// </summary>
/// <remarks>
/// <para>
/// As permissões seguem o ambiente <b>ao vivo</b>: escolher Produção marca e
/// trava exatamente o conjunto obrigatório — a tela mostra o que vai ser
/// gravado. Quem decide de verdade é o domínio, que corta de novo ao salvar.
/// </para>
/// <para>
/// A senha só entra: é limpa do formulário depois de salva, e a tela nunca
/// recebe uma senha guardada — só se há uma.
/// </para>
/// </remarks>
public sealed partial class DatabaseConnectionsViewModel(
    IUseCaseRunner runner,
    IConfirmationDialog confirmation,
    ILogger<DatabaseConnectionsViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _statusMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEditing), nameof(FormTitle), nameof(SaveLabel))]
    private Guid? _editingId;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    private string _name = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    private string _host = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    private string _port = DatabaseConnection.DefaultPort.ToString(CultureInfo.InvariantCulture);

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    private string _database = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(TestCommand))]
    private string _username = string.Empty;

    [ObservableProperty]
    private string _password = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProductionSelected), nameof(EnvironmentHint))]
    private Choice<DatabaseEnvironment> _selectedEnvironment = Environments[0];

    [ObservableProperty]
    private Choice<DatabaseSslMode> _selectedSslMode = SslModes[0];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PasswordHint))]
    private bool _hasStoredPassword;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasTestResult))]
    private string? _testResult;

    [ObservableProperty]
    private bool _testSucceeded;

    public static IReadOnlyList<Choice<DatabaseEnvironment>> Environments { get; } =
        Enum.GetValues<DatabaseEnvironment>().Select(value => new Choice<DatabaseEnvironment>(value, DatabaseLabels.Environment(value))).ToList();

    public static IReadOnlyList<Choice<DatabaseSslMode>> SslModes { get; } =
        Enum.GetValues<DatabaseSslMode>().Select(value => new Choice<DatabaseSslMode>(value, DatabaseLabels.Ssl(value))).ToList();

    public ObservableCollection<DatabaseConnectionItemViewModel> Connections { get; } = [];

    /// <summary>Na ordem do pedido: o que se pode fazer, depois os papéis, depois a obrigação.</summary>
    public IReadOnlyList<PermissionOptionViewModel> Permissions { get; } = CreatePermissions();

    private static List<PermissionOptionViewModel> CreatePermissions()
    {
        List<PermissionOptionViewModel> options =
        [
        new(ConnectionPermission.Read, "Ler (diagnóstico e verificação)"),
        new(ConnectionPermission.Dump, "Fazer dump"),
        new(ConnectionPermission.Restore, "Receber restore"),
        new(ConnectionPermission.Modify, "Alterar dados"),
        new(ConnectionPermission.CreateDatabase, "Criar o banco"),
        new(ConnectionPermission.DropDatabase, "Apagar o banco"),
        new(ConnectionPermission.ExecuteSql, "Executar SQL"),
        new(ConnectionPermission.UseAsSource, "Pode ser origem"),
        new(ConnectionPermission.UseAsDestination, "Pode ser destino"),
        new(ConnectionPermission.RequireAnonymization, "Exige anonimização"),
        ];

        var rules = EnvironmentPolicy.For(Environments[0].Value);

        foreach (var option in options)
        {
            option.IsChecked = rules.Defaults.HasFlag(option.Permission);
            option.IsEditable = !rules.IsLocked(option.Permission);
        }

        return options;
    }

    public bool IsEmpty => Connections.Count == 0;

    public bool IsEditing => EditingId is not null;

    public string FormTitle => IsEditing ? "Editar conexão" : "Nova conexão";

    public string SaveLabel => IsEditing ? "Salvar alterações" : "Criar conexão";

    public bool IsProductionSelected => EnvironmentPolicy.IsProtected(SelectedEnvironment.Value);

    public string EnvironmentHint => SelectedEnvironment.Value switch
    {
        DatabaseEnvironment.Production =>
            "Produção: só leitura e dump anônimo. Nunca é destino, nunca é alterada.",
        DatabaseEnvironment.CriticalProduction =>
            "Produção crítica: como produção, e ainda só alimenta Teste e Homologação, com verificação obrigatória e o nome do banco digitado para confirmar.",
        _ => "Ambiente que pode receber cópias. As permissões abaixo são escolhas suas.",
    };

    public string PasswordHint => HasStoredPassword
        ? "Senha guardada no cofre do sistema. Deixe em branco para mantê-la."
        : "A senha vai para o cofre do sistema (DPAPI no Windows, chaveiro no Linux), nunca para o banco do app.";

    public bool HasTestResult => !string.IsNullOrEmpty(TestResult);

    /// <summary>Uma conexão foi criada, editada ou excluída: os seletores das outras abas recarregam.</summary>
    public event Action? Changed;

    public void SetConnections(IReadOnlyList<DatabaseConnectionRow> rows)
    {
        Connections.Clear();

        foreach (var row in rows)
        {
            Connections.Add(new DatabaseConnectionItemViewModel(row));
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnSelectedEnvironmentChanged(Choice<DatabaseEnvironment> value)
    {
        var rules = EnvironmentPolicy.For(value.Value);

        // Conexão nova nasce com os padrões do ambiente; editando, o que está
        // marcado é encaixado — sem ganhar nada que o ambiente não admite.
        var requested = IsEditing ? CurrentPermissions() : rules.Defaults;
        ApplyPermissions(rules.Clamp(requested), rules);
    }

    [RelayCommand]
    public void New()
    {
        ResetForm();
        StatusMessage = null;
        ErrorMessage = null;
    }

    [RelayCommand]
    public void Edit(DatabaseConnectionItemViewModel item)
    {
        var row = item.Row;
        EditingId = row.Id;
        Name = row.Name;
        Host = row.Host;
        Port = row.Port.ToString(CultureInfo.InvariantCulture);
        Database = row.Database;
        Username = row.Username;
        Description = row.Description ?? string.Empty;
        Password = string.Empty;
        HasStoredPassword = row.HasPassword;
        SelectedSslMode = SslModes.First(choice => choice.Value == row.SslMode);
        SelectedEnvironment = Environments.First(choice => choice.Value == row.Environment);

        var rules = EnvironmentPolicy.For(row.Environment);
        ApplyPermissions(rules.Clamp(row.Permissions.ToFlags()), rules);
        TestResult = null;
        StatusMessage = null;
        ErrorMessage = null;
    }

    [RelayCommand]
    public void CancelEdit() => ResetForm();

    private bool CanSave() =>
        !string.IsNullOrWhiteSpace(Name) && CanTest();

    private bool CanTest() =>
        !string.IsNullOrWhiteSpace(Host) && !string.IsNullOrWhiteSpace(Database)
        && !string.IsNullOrWhiteSpace(Username) && int.TryParse(Port, CultureInfo.InvariantCulture, out _);

    [RelayCommand(CanExecute = nameof(CanSave))]
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        var creating = !IsEditing;
        var command = new SaveDatabaseConnection(
            EditingId,
            Name,
            Host,
            ParsedPort(),
            Database,
            Username,
            SelectedEnvironment.Value,
            SelectedSslMode.Value,
            Description,
            ConnectionPermissions.FromFlags(CurrentPermissions()),
            SecretText.FromOptional(Password));

        var saved = await TryAsync(
            () => runner.RunAsync<SaveDatabaseConnectionHandler, Guid>(
                (handler, token) => handler.HandleAsync(command, token), cancellationToken),
            "Não foi possível salvar a conexão.");

        // A senha sai da memória da tela assim que o cofre a recebeu.
        Password = string.Empty;

        if (saved)
        {
            ResetForm();
            StatusMessage = creating ? "Conexão criada." : "Conexão atualizada.";
            Changed?.Invoke();
        }
    }

    [RelayCommand(CanExecute = nameof(CanTest))]
    public async Task TestAsync(CancellationToken cancellationToken = default)
    {
        TestResult = null;
        var command = new TestDatabaseConnection(
            EditingId,
            Name,
            Host,
            ParsedPort(),
            Database,
            Username,
            SelectedEnvironment.Value,
            SelectedSslMode.Value,
            SecretText.FromOptional(Password));

        ServerDiagnostics? result = null;

        await TryAsync(
            async () => result = await runner.RunAsync<TestDatabaseConnectionHandler, ServerDiagnostics>(
                (handler, token) => handler.HandleAsync(command, token), cancellationToken),
            "Não foi possível testar a conexão.");

        if (result is null)
        {
            return;
        }

        TestSucceeded = result.Connected;
        TestResult = result.Connected
            ? $"✓ Conectado como {result.CurrentUser} em {result.Database} — PostgreSQL {result.ServerVersion}."
            : $"✗ {result.Error}";
    }

    [RelayCommand]
    public async Task DeleteAsync(DatabaseConnectionItemViewModel item, CancellationToken cancellationToken = default)
    {
        var confirmed = await confirmation.AskAsync(new ConfirmationRequest(
            $"Excluir {item.Name}?",
            "A conexão sai do cadastro e a senha sai do cofre do sistema. O banco em si não é tocado; o histórico das operações fica.",
            "Excluir"));

        if (!confirmed)
        {
            return;
        }

        var deleted = await TryAsync(
            () => runner.RunAsync<DeleteDatabaseConnectionHandler>(
                (handler, token) => handler.HandleAsync(new DeleteDatabaseConnection(item.Id), token), cancellationToken),
            "Não foi possível excluir a conexão.");

        if (deleted)
        {
            if (EditingId == item.Id)
            {
                ResetForm();
            }

            StatusMessage = "Conexão excluída.";
            Changed?.Invoke();
        }
    }

    [RelayCommand]
    public async Task ToggleEnabledAsync(DatabaseConnectionItemViewModel item, CancellationToken cancellationToken = default)
    {
        var enabled = !item.Row.IsEnabled;

        if (await TryAsync(
                () => runner.RunAsync<SetDatabaseConnectionEnabledHandler>(
                    (handler, token) => handler.HandleAsync(new SetDatabaseConnectionEnabled(item.Id, enabled), token), cancellationToken),
                "Não foi possível alterar a conexão."))
        {
            StatusMessage = enabled ? $"{item.Name} ativada." : $"{item.Name} desativada.";
            Changed?.Invoke();
        }
    }

    private ConnectionPermission CurrentPermissions() =>
        Permissions.Where(option => option.IsChecked).Aggregate(ConnectionPermission.None, (all, option) => all | option.Permission);

    private void ApplyPermissions(ConnectionPermission flags, EnvironmentRules rules)
    {
        foreach (var option in Permissions)
        {
            option.IsChecked = flags.HasFlag(option.Permission);
            option.IsEditable = !rules.IsLocked(option.Permission);
        }
    }

    private int ParsedPort() =>
        int.TryParse(Port, CultureInfo.InvariantCulture, out var port) ? port : 0;

    private void ResetForm()
    {
        EditingId = null;
        Name = string.Empty;
        Host = string.Empty;
        Port = DatabaseConnection.DefaultPort.ToString(CultureInfo.InvariantCulture);
        Database = string.Empty;
        Username = string.Empty;
        Password = string.Empty;
        Description = string.Empty;
        HasStoredPassword = false;
        TestResult = null;
        SelectedSslMode = SslModes[0];
        SelectedEnvironment = Environments[0];
        var rules = EnvironmentPolicy.For(SelectedEnvironment.Value);
        ApplyPermissions(rules.Defaults, rules);
    }

    private async Task<bool> TryAsync(Func<Task> operation, string fallbackMessage)
    {
        IsBusy = true;
        ErrorMessage = null;
        StatusMessage = null;

        try
        {
            await operation();
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
            return false;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "DatabaseConnectionsScreenOperationFailed");
            ErrorMessage = fallbackMessage;
            return false;
        }
        finally
        {
            IsBusy = false;
        }
    }

}
