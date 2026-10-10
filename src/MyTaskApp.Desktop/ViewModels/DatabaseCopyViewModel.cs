using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// A aba "Copiar Banco" (ADR-056): origem, destino, operação e perfil;
/// [Validar] antes de [Executar]; a confirmação de produção; e o progresso
/// com as etapas, a barra e os logs. Numa conexão só de servidor (ADR-057),
/// o banco é escolhido aqui — da lista do servidor, ou por um apelido salvo.
/// </summary>
/// <remarks>
/// A tela só pede: quem decide se pode é a política, de novo, no handler.
/// [Executar] fica apagado até a validação da <b>mesma</b> seleção dar
/// certo — mudar qualquer campo exige validar de novo.
/// </remarks>
public sealed partial class DatabaseCopyViewModel(
    IUseCaseRunner runner,
    IConfirmationDialog confirmation,
    ILogger<DatabaseCopyViewModel> logger) : ObservableObject
{
    private CancellationTokenSource? _run;

    /// <summary>A seleção que a última validação aprovou; qualquer diferença apaga o [Executar].</summary>
    private DatabaseCopyRequest? _validated;

    private DatabaseCopyValidation? _validation;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ValidateCommand), nameof(ExecuteCommand))]
    private bool _isBusy;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ValidateCommand), nameof(ExecuteCommand), nameof(CancelCommand))]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isRunning;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _resultMessage;

    [ObservableProperty]
    private bool _resultSucceeded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selection), nameof(NeedsSourceDatabase), nameof(DestinationPreview))]
    [NotifyCanExecuteChangedFor(nameof(ValidateCommand), nameof(ExecuteCommand), nameof(SaveAliasCommand))]
    private DatabaseConnectionItemViewModel? _selectedSource;

    /// <summary>O banco de origem, quando a origem é só o servidor: escolhido da lista ou digitado.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selection), nameof(DestinationPreview))]
    [NotifyCanExecuteChangedFor(nameof(ValidateCommand), nameof(ExecuteCommand), nameof(SaveAliasCommand))]
    private string? _sourceDatabase;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selection), nameof(CreatesNewDestination), nameof(DestinationPreview))]
    [NotifyCanExecuteChangedFor(nameof(ValidateCommand), nameof(ExecuteCommand))]
    private DatabaseConnectionItemViewModel? _selectedDestination;

    /// <summary>O apelido escolhido: preenche origem, banco e anonimização, e nomeia o banco copiado.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selection), nameof(DestinationPreview))]
    [NotifyCanExecuteChangedFor(nameof(ValidateCommand), nameof(ExecuteCommand))]
    private SavedDatabaseRow? _selectedSavedDatabase;

    /// <summary>O apelido a salvar para a origem e o banco escolhidos.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAliasCommand))]
    private string _newAlias = string.Empty;

    /// <summary>Por que a lista de bancos não veio; dá para digitar o nome mesmo assim.</summary>
    [ObservableProperty]
    private string? _databaseListMessage;

    [ObservableProperty]
    private string? _aliasMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selection), nameof(Anonymizes))]
    [NotifyCanExecuteChangedFor(nameof(ValidateCommand), nameof(ExecuteCommand))]
    private Choice<DatabaseOperationType> _selectedOperation = Operations[1];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selection))]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private AnonymizationProfileRow? _selectedAnonymizationProfile;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private bool _recreateDestination = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private bool _verifyAfterRestore = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private bool _includeSchema = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private bool _includeData = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExecuteCommand))]
    private bool _keepAnonymizedArtifact;

    /// <summary>O perfil de cópia escolhido, para a auditoria; os campos podem ter sido ajustados depois.</summary>
    [ObservableProperty]
    private DatabaseCopyProfileRow? _selectedCopyProfile;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private bool _isLogOpen;

    public static IReadOnlyList<Choice<DatabaseOperationType>> Operations { get; } =
    [
        new(DatabaseOperationType.Copy, DatabaseLabels.Operation(DatabaseOperationType.Copy)),
        new(DatabaseOperationType.CopyAndAnonymize, DatabaseLabels.Operation(DatabaseOperationType.CopyAndAnonymize)),
    ];

    public ObservableCollection<DatabaseConnectionItemViewModel> Connections { get; } = [];

    public ObservableCollection<AnonymizationProfileRow> AnonymizationProfiles { get; } = [];

    public ObservableCollection<DatabaseCopyProfileRow> CopyProfiles { get; } = [];

    public ObservableCollection<SavedDatabaseRow> SavedDatabases { get; } = [];

    /// <summary>Os bancos do servidor da origem, quando ela é só o servidor.</summary>
    public ObservableCollection<string> SourceDatabases { get; } = [];

    public ObservableCollection<string> Violations { get; } = [];

    public ObservableCollection<CheckItemViewModel> Checks { get; } = [];

    public ObservableCollection<DatabaseCopyStepViewModel> Steps { get; } = [];

    public ObservableCollection<CheckItemViewModel> Verification { get; } = [];

    /// <summary>Os logs da cópia: as linhas das ferramentas, já mascaradas, no terminal de sempre.</summary>
    public CommandOutputViewModel Output { get; } = new();

    public bool Anonymizes => SelectedOperation.Value == DatabaseOperationType.CopyAndAnonymize;

    public bool CanEdit => !IsRunning;

    /// <summary>A origem é só o servidor: falta escolher o banco.</summary>
    public bool NeedsSourceDatabase => SelectedSource is { HasDatabase: false };

    /// <summary>O destino é só o servidor: a cópia cria um banco novo, com data e hora no nome.</summary>
    public bool CreatesNewDestination => SelectedDestination is { HasDatabase: false };

    /// <summary>"Será criado: lock_eco_core_1010_aaaaMMdd_HHmmss" — a data e a hora são as da execução.</summary>
    public string? DestinationPreview =>
        CreatesNewDestination && (SelectedSavedDatabase?.Alias ?? EffectiveSourceDatabase) is { Length: > 0 } prefix
            ? $"Será criado: {SavedDatabase.CopyPrefix(prefix)}_aaaaMMdd_HHmmss (data e hora da execução)"
            : null;

    /// <summary>O banco de origem que vale: o fixo da conexão, ou o escolhido aqui.</summary>
    private string? EffectiveSourceDatabase => SelectedSource?.Row.Database
        ?? (string.IsNullOrWhiteSpace(SourceDatabase) ? null : SourceDatabase.Trim());

    public bool HasValidation => _validation is not null;

    public bool HasViolations => Violations.Count > 0;

    public bool HasSteps => Steps.Count > 0;

    public bool HasVerification => Verification.Count > 0;

    public bool HasResult => !string.IsNullOrEmpty(ResultMessage);

    public string PercentText => string.Create(CultureInfo.InvariantCulture, $"{Percent:0}%");

    public string ValidationSummary => _validation switch
    {
        null => string.Empty,
        { CanRun: true } => "✓ Pronto para executar.",
        { Decision.IsAllowed: false } => "✗ Bloqueado pela política de segurança.",
        _ => "✗ O ambiente não está pronto.",
    };

    /// <summary>Muda junto com qualquer campo: é o que invalida a validação anterior.</summary>
    public DatabaseCopyRequest? Selection =>
        SelectedSource is { } source && SelectedDestination is { } destination
            ? new DatabaseCopyRequest(
                source.Id,
                destination.Id,
                SelectedOperation.Value,
                Anonymizes ? SelectedAnonymizationProfile?.Id : null,
                new DatabaseCopyOptions(
                    Anonymizes,
                    IncludeSchema,
                    IncludeData,
                    RecreateDestination,
                    VerifyAfterRestore,
                    KeepAnonymizedArtifact && Anonymizes),
                SelectedCopyProfile?.Id,
                NeedsSourceDatabase ? EffectiveSourceDatabase : null,
                SelectedSavedDatabase?.Id)
            : null;

    /// <summary>Terminou uma cópia — com qualquer desfecho. O histórico recarrega.</summary>
    public event Action? Finished;

    /// <summary>Um apelido foi salvo: os seletores das outras abas recarregam.</summary>
    public event Action? Changed;

    public void SetCatalog(
        IReadOnlyList<DatabaseConnectionItemViewModel> connections,
        IReadOnlyList<AnonymizationProfileRow> anonymizationProfiles,
        IReadOnlyList<DatabaseCopyProfileRow> copyProfiles,
        IReadOnlyList<SavedDatabaseRow>? savedDatabases = null)
    {
        var source = SelectedSource?.Id;
        var destination = SelectedDestination?.Id;
        var profile = SelectedAnonymizationProfile?.Id;
        var saved = _pendingSavedDatabase ?? SelectedSavedDatabase?.Id;
        var database = SourceDatabase;

        _applyingSaved = true;

        try
        {
            Replace(Connections, connections.Where(connection => !connection.IsDisabled));
            Replace(AnonymizationProfiles, anonymizationProfiles.Where(item => item.IsEnabled));
            Replace(CopyProfiles, copyProfiles.Where(item => item.IsEnabled));
            Replace(SavedDatabases, savedDatabases ?? []);

            SelectedSource = Connections.FirstOrDefault(connection => connection.Id == source);
            SourceDatabase = database;
            SelectedDestination = Connections.FirstOrDefault(connection => connection.Id == destination);
            SelectedAnonymizationProfile = AnonymizationProfiles.FirstOrDefault(item => item.Id == profile);
            SelectedSavedDatabase = SavedDatabases.FirstOrDefault(item => item.Id == saved);
        }
        finally
        {
            _applyingSaved = false;
            _pendingSavedDatabase = null;
        }
    }

    /// <summary>
    /// Preenche a tela com um apelido: a origem, o banco e a anonimização dele.
    /// O destino fica como está — é escolhido a cada cópia.
    /// </summary>
    public void UseSavedDatabase(SavedDatabaseRow saved)
    {
        _applyingSaved = true;

        try
        {
            SelectedSavedDatabase = SavedDatabases.FirstOrDefault(item => item.Id == saved.Id) ?? saved;
            SelectedCopyProfile = null;
            SelectedSource = Connections.FirstOrDefault(connection => connection.Id == saved.ConnectionId);
            SourceDatabase = saved.DatabaseName;
            SelectedOperation = Operations[saved.AnonymizationProfileId is null ? 0 : 1];
            SelectedAnonymizationProfile = AnonymizationProfiles.FirstOrDefault(item => item.Id == saved.AnonymizationProfileId);
            NewAlias = saved.Alias;
        }
        finally
        {
            _applyingSaved = false;
        }
    }

    private bool _applyingSaved;

    private Guid? _pendingSavedDatabase;

    partial void OnSelectedSavedDatabaseChanged(SavedDatabaseRow? value)
    {
        if (value is not null && !_applyingSaved)
        {
            UseSavedDatabase(value);
        }
    }

    partial void OnSelectedSourceChanged(DatabaseConnectionItemViewModel? value)
    {
        SourceDatabases.Clear();
        DatabaseListMessage = null;

        if (!_applyingSaved)
        {
            ForgetSavedDatabaseIfChanged();
        }

        if (value is { HasDatabase: false } && !IsRunning)
        {
            _ = LoadSourceDatabasesAsync(value);
        }
    }

    partial void OnSourceDatabaseChanged(string? value)
    {
        if (!_applyingSaved)
        {
            ForgetSavedDatabaseIfChanged();
        }
    }

    /// <summary>O apelido vale enquanto a origem e o banco forem os dele; mudou um dos dois, a escolha sai.</summary>
    private void ForgetSavedDatabaseIfChanged()
    {
        if (SelectedSavedDatabase is { } saved
            && (SelectedSource?.Id != saved.ConnectionId
                || !string.Equals(EffectiveSourceDatabase, saved.DatabaseName, StringComparison.Ordinal)))
        {
            SelectedSavedDatabase = null;
        }
    }

    /// <summary>A lista do servidor. Se não vier (sem acesso ao banco de manutenção), o nome pode ser digitado.</summary>
    private async Task LoadSourceDatabasesAsync(DatabaseConnectionItemViewModel source)
    {
        try
        {
            var databases = await runner.RunAsync<ListServerDatabasesHandler, IReadOnlyList<string>>(
                (handler, token) => handler.HandleAsync(new ListServerDatabases(source.Id), token), CancellationToken.None);

            // A origem pode ter mudado enquanto o servidor respondia.
            if (SelectedSource?.Id != source.Id)
            {
                return;
            }

            Replace(SourceDatabases, databases);
            DatabaseListMessage = databases.Count == 0 ? "Nenhum banco encontrado no servidor; digite o nome." : null;
        }
        catch (DomainException exception)
        {
            DatabaseListMessage = $"{exception.Message} Digite o nome do banco.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "ServerDatabasesListFailed");
            DatabaseListMessage = "Não foi possível listar os bancos do servidor; digite o nome.";
        }
    }

    private bool CanSaveAlias() =>
        !IsRunning && !IsBusy && SelectedSource is not null && !string.IsNullOrWhiteSpace(EffectiveSourceDatabase)
        && !string.IsNullOrWhiteSpace(NewAlias);

    /// <summary>
    /// Salva a origem, o banco e a anonimização escolhidos com o apelido. Com
    /// um apelido escolhido, atualiza aquele (renomear é isto); sem, cria.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveAlias))]
    public async Task SaveAliasAsync(CancellationToken cancellationToken = default)
    {
        AliasMessage = null;
        ErrorMessage = null;

        var command = new SaveSavedDatabase(
            SelectedSavedDatabase?.Id,
            NewAlias,
            SelectedSource!.Id,
            EffectiveSourceDatabase!,
            Anonymizes ? SelectedAnonymizationProfile?.Id : null);

        try
        {
            var id = await runner.RunAsync<SaveSavedDatabaseHandler, Guid>(
                (handler, token) => handler.HandleAsync(command, token), cancellationToken);

            _pendingSavedDatabase = id;
            AliasMessage = $"Apelido {SavedDatabase.NormalizeAlias(NewAlias)} salvo.";
            Changed?.Invoke();
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
            logger.LogError(exception, "SavedDatabaseSaveFailed");
            ErrorMessage = "Não foi possível salvar o apelido.";
        }
    }

    /// <summary>Preenche a tela com um perfil de cópia — o "ECO Production → ECO Development" de sempre.</summary>
    public void UseProfile(DatabaseCopyProfileRow profile)
    {
        SelectedCopyProfile = profile;
        SelectedSavedDatabase = null;
        SelectedSource = Connections.FirstOrDefault(connection => connection.Id == profile.SourceConnectionId);
        SourceDatabase = profile.SourceDatabase;
        SelectedDestination = Connections.FirstOrDefault(connection => connection.Id == profile.DestinationConnectionId);
        SelectedOperation = Operations[profile.Options.RequireAnonymization ? 1 : 0];
        SelectedAnonymizationProfile = AnonymizationProfiles.FirstOrDefault(item => item.Id == profile.AnonymizationProfileId);
        RecreateDestination = profile.Options.RecreateDestination;
        VerifyAfterRestore = profile.Options.VerifyAfterRestore;
        IncludeSchema = profile.Options.IncludeSchema;
        IncludeData = profile.Options.IncludeData;
        KeepAnonymizedArtifact = profile.Options.KeepAnonymizedArtifact;
    }

    partial void OnSelectedCopyProfileChanged(DatabaseCopyProfileRow? value)
    {
        if (value is not null && !ReferenceEquals(value, _applying))
        {
            _applying = value;
            UseProfile(value);
            _applying = null;
        }
    }

    private DatabaseCopyProfileRow? _applying;

    private bool CanValidate() =>
        !IsBusy && !IsRunning && Selection is not null && (!NeedsSourceDatabase || EffectiveSourceDatabase is not null);

    [RelayCommand(CanExecute = nameof(CanValidate))]
    public async Task ValidateAsync(CancellationToken cancellationToken = default)
    {
        if (Selection is not { } request)
        {
            return;
        }

        IsBusy = true;
        ErrorMessage = null;
        ResultMessage = null;

        try
        {
            var validation = await runner.RunAsync<ValidateDatabaseCopyHandler, DatabaseCopyValidation>(
                (handler, token) => handler.HandleAsync(new ValidateDatabaseCopy(request), token), cancellationToken);

            ShowValidation(validation, request);
        }
        catch (OperationCanceledException)
        {
        }
        catch (DomainException exception)
        {
            ClearValidation();
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "DatabaseCopyValidationFailed");
            ClearValidation();
            ErrorMessage = "Não foi possível validar a cópia.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanExecute() =>
        !IsBusy && !IsRunning && _validation is { CanRun: true } && _validated is not null && _validated == Selection;

    [RelayCommand(CanExecute = nameof(CanExecute))]
    public async Task ExecuteAsync()
    {
        if (_validation is not { CanRun: true } validation || Selection is not { } request)
        {
            return;
        }

        string? typed = null;

        if (validation.RequiresProductionConfirmation)
        {
            var confirmed = await confirmation.AskAsync(ProductionConfirmation(validation, request));

            if (!confirmed)
            {
                return;
            }

            typed = validation.RequiresTypedConfirmation ? validation.ConfirmationText : null;
        }

        BeginRun();
        var progress = new UiProgress<DatabaseCopyProgress>(Apply);

        try
        {
            var result = await runner.RunAsync<RunDatabaseCopyHandler, DatabaseCopyResult>(
                (handler, token) => handler.HandleAsync(
                    new RunDatabaseCopy(request, validation.RequiresProductionConfirmation, typed), progress, token),
                _run!.Token);

            Complete(result);
        }
        catch (OperationCanceledException)
        {
            ResultSucceeded = false;
            ResultMessage = "✗ Cancelada.";
        }
        catch (DomainException exception)
        {
            ResultSucceeded = false;
            ResultMessage = "✗ " + exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "DatabaseCopyFailed");
            ResultSucceeded = false;
            ResultMessage = "✗ A cópia falhou por um erro inesperado; os detalhes estão no log do app.";
        }
        finally
        {
            IsRunning = false;
            ClearValidation();
            Finished?.Invoke();
        }
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel() => _run?.Cancel();

    /// <summary>Fechando o app: a cópia para, e a limpeza do diretório temporário roda no caminho de volta.</summary>
    public void CancelForShutdown() => _run?.Cancel();

    [RelayCommand]
    public void ToggleLog() => IsLogOpen = !IsLogOpen;

    /// <summary>
    /// O texto do pedido (ADR-056): a origem é produção, a leitura é só
    /// leitura, os dados chegam anonimizados — e onde, de onde, com que perfil.
    /// </summary>
    internal static ConfirmationRequest ProductionConfirmation(DatabaseCopyValidation validation, DatabaseCopyRequest request)
    {
        var message =
            "Você está copiando dados originados de PRODUÇÃO.\n\n" +
            "A operação será executada somente em modo de leitura na origem.\n\n" +
            "Os dados serão anonimizados antes de serem disponibilizados no ambiente de destino.\n\n" +
            $"Origem:\n{validation.SourceName}\n\n" +
            $"Destino:\n{validation.DestinationName}\n\n" +
            $"Perfil:\n{validation.AnonymizationProfileName ?? "—"}";

        var drops = request.Options.RecreateDestination && !validation.NewDestinationDatabase;

        if (validation.NewDestinationDatabase)
        {
            message += $"\n\nUm banco novo será criado em {validation.DestinationName}.";
        }
        else if (drops)
        {
            message += $"\n\nO banco {validation.DestinationDatabase ?? validation.DestinationName} será apagado e criado de novo.";
        }

        return new ConfirmationRequest(
            "ATENÇÃO",
            message,
            "Confirmar operação",
            IsIrreversible: drops,
            RequiredText: validation.RequiresTypedConfirmation ? validation.ConfirmationText : null);
    }

    private void ShowValidation(DatabaseCopyValidation validation, DatabaseCopyRequest request)
    {
        _validation = validation;
        _validated = request;
        Replace(Violations, validation.Decision.Violations.Select(violation => violation.Message).Distinct());
        Replace(Checks, validation.Checks.Select(check => new CheckItemViewModel(check)));
        NotifyValidation();
    }

    private void ClearValidation()
    {
        _validation = null;
        _validated = null;
        Violations.Clear();
        Checks.Clear();
        NotifyValidation();
    }

    private void NotifyValidation()
    {
        OnPropertyChanged(nameof(HasValidation));
        OnPropertyChanged(nameof(HasViolations));
        OnPropertyChanged(nameof(ValidationSummary));
        ExecuteCommand.NotifyCanExecuteChanged();
    }

    private void BeginRun()
    {
        _run?.Dispose();
        _run = new CancellationTokenSource();
        IsRunning = true;
        ResultMessage = null;
        ErrorMessage = null;
        Percent = 0;
        OnPropertyChanged(nameof(PercentText));
        Verification.Clear();
        Output.Reset(Anonymizes ? "Copiar + Anonimizar" : "Copiar");

        Replace(Steps, DatabaseCopySteps.All.Select(step => new DatabaseCopyStepViewModel(step, DatabaseCopySteps.Label(step, Anonymizes))));
        OnPropertyChanged(nameof(HasSteps));
        OnPropertyChanged(nameof(HasVerification));
    }

    internal void Apply(DatabaseCopyProgress progress)
    {
        if (Steps.FirstOrDefault(step => step.Step == progress.Step) is { } item)
        {
            item.State = progress.State;

            if (progress.Detail is not null)
            {
                item.Detail = progress.Detail;
            }
        }

        Percent = progress.Percent;
        OnPropertyChanged(nameof(PercentText));

        if (progress.Line is { } line)
        {
            Output.Append(line);
        }
    }

    private void Complete(DatabaseCopyResult result)
    {
        foreach (var (step, state) in result.Steps)
        {
            if (Steps.FirstOrDefault(item => item.Step == step) is { } item)
            {
                item.State = state;
            }
        }

        Output.State = result.Succeeded ? CommandStepState.Succeeded : CommandStepState.Failed;
        Replace(Verification, result.Verification?.Checks.Select(check => new CheckItemViewModel(check)) ?? []);
        OnPropertyChanged(nameof(HasVerification));

        ResultSucceeded = result.Succeeded;
        ResultMessage = result.Status switch
        {
            DatabaseOperationStatus.Succeeded when result.KeptArtifactPath is { } kept =>
                $"✓ Cópia concluída{Into(result)}. Dump da estrutura mantido em {kept}.",
            DatabaseOperationStatus.Succeeded => $"✓ Cópia concluída{Into(result)}. Arquivos temporários removidos.",
            DatabaseOperationStatus.Canceled => "✗ Cancelada. O destino pode ter ficado incompleto.",
            _ => "✗ " + (result.Error ?? "A cópia falhou."),
        };
    }

    private static string Into(DatabaseCopyResult result) =>
        result.DestinationDatabase is { } database ? $" em {database}" : string.Empty;

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();

        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
