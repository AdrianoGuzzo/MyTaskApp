using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.DatabaseOperations;
using MyTaskApp.Application.Development;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.DatabaseOperations;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Uma cópia já validada e confirmada, esperando o worktree ficar pronto.</summary>
public sealed record PlannedDatabaseCopy(
    DatabaseCopyRequest Request,
    bool ProductionConfirmed,
    string? TypedConfirmation);

/// <summary>
/// O banco do ambiente, na aba Desenvolvimento (ADR-060): escolher o que
/// copiar — um perfil de cópia, ou um apelido e o servidor de destino —,
/// validar e confirmar antes, acompanhar a cópia, e mostrar o banco criado,
/// que os comandos recebem em <c>{database}</c>.
/// </summary>
/// <remarks>
/// <para>
/// A cópia é a da janela Bancos de Dados: política, confirmação de produção,
/// uma operação por vez e auditoria valem igual. Os cadastros também são os
/// de lá — esta tela só escolhe entre eles.
/// </para>
/// <para>
/// No modo apelido, o destino é sempre um servidor sem banco fixo: cada
/// ambiente ganha um banco <b>novo</b>, com data e hora no nome, e nada é
/// apagado para dar lugar a ele.
/// </para>
/// </remarks>
public sealed partial class DevelopmentDatabaseViewModel(
    IUseCaseRunner runner,
    IConfirmationDialog confirmation,
    IClipboardWriter clipboard,
    DatabaseCopyRuns runs,
    ILogger<DevelopmentDatabaseViewModel> logger) : ObservableObject
{
    private CancellationTokenSource? _run;

    private bool _anonymizes;

    private Guid _taskId;

    private Guid? _developmentId;

    /// <summary>"Copiar banco para este ambiente", no formulário de criar o worktree.</summary>
    [ObservableProperty]
    private bool _copyOnCreate;

    /// <summary>Com o worktree pronto: o seletor aberto para copiar (de novo).</summary>
    [ObservableProperty]
    private bool _isSelecting;

    /// <summary>Escolher por perfil de cópia; senão, por apelido e servidor de destino.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selection), nameof(DestinationPreview), nameof(ByAlias), nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(CopyNowCommand))]
    private bool _byProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selection), nameof(DestinationPreview), nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(CopyNowCommand))]
    private DatabaseCopyProfileRow? _selectedCopyProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selection), nameof(DestinationPreview), nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(CopyNowCommand))]
    private SavedDatabaseRow? _selectedSavedDatabase;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selection), nameof(DestinationPreview), nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(CopyNowCommand))]
    private DatabaseConnectionItemViewModel? _selectedDestination;

    /// <summary>Anonimizar a cópia do apelido; vem ligado quando o apelido tem perfil de anonimização.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selection))]
    private bool _anonymize;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selection))]
    private AnonymizationProfileRow? _selectedAnonymizationProfile;

    /// <summary>O banco copiado para o ambiente; <c>null</c> = nenhum ainda.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDatabase), nameof(DatabaseServer), nameof(CopiedAtText), nameof(CopyActionLabel))]
    [NotifyCanExecuteChangedFor(nameof(CopyNameCommand))]
    private DevelopmentDatabaseView? _current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(CopyNowCommand), nameof(CancelCommand), nameof(BeginSelectingCommand))]
    private bool _isRunning;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyNowCommand))]
    private bool _isValidating;

    [ObservableProperty]
    private double _percent;

    [ObservableProperty]
    private string? _currentStep;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasResult))]
    private string? _resultMessage;

    [ObservableProperty]
    private bool _resultSucceeded;

    [ObservableProperty]
    private bool _isLogOpen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCatalogMessage))]
    private string? _catalogMessage;

    public ObservableCollection<DatabaseCopyProfileRow> CopyProfiles { get; } = [];

    public ObservableCollection<SavedDatabaseRow> SavedDatabases { get; } = [];

    /// <summary>Só servidores sem banco fixo, fora de produção e ativos: o destino de um banco novo.</summary>
    public ObservableCollection<DatabaseConnectionItemViewModel> Destinations { get; } = [];

    public ObservableCollection<AnonymizationProfileRow> AnonymizationProfiles { get; } = [];

    /// <summary>O que impediu a cópia na validação: política e checagens que falharam.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    /// <summary>Os logs da cópia: as linhas das ferramentas, já mascaradas.</summary>
    public CommandOutputViewModel Output { get; } = new();

    private readonly List<DatabaseConnectionItemViewModel> _connections = [];

    /// <summary>O outro lado de <see cref="ByProfile"/>, para o par de opções da tela.</summary>
    public bool ByAlias
    {
        get => !ByProfile;
        set => ByProfile = !value;
    }

    public bool CanEdit => !IsRunning;

    public bool HasDatabase => Current is not null;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasResult => !string.IsNullOrEmpty(ResultMessage);

    public bool HasProblems => Problems.Count > 0;

    public bool HasCatalogMessage => !string.IsNullOrEmpty(CatalogMessage);

    public bool HasSelection => Selection is not null;

    public string PercentText => string.Create(CultureInfo.InvariantCulture, $"{Percent:0}%");

    public string? DatabaseServer => Current is { } database
        ? string.Create(CultureInfo.InvariantCulture, $"{database.Host}:{database.Port} · usuário {database.Username} · {database.ConnectionName}")
        : null;

    public string? CopiedAtText => Current is { } database
        ? "Copiado em " + database.CopiedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"))
        : null;

    public string CopyActionLabel => HasDatabase ? "Copiar de novo…" : "Copiar banco…";

    /// <summary>Como os comandos usam o banco: direto, ou no parâmetro de um comando pronto.</summary>
    public static string VariablesHint =>
        "Os comandos pós-Worktree rodam depois da cópia e recebem o banco em {database} "
        + "({database.host}, {database.port}, {database.user}). Num comando pronto com parâmetro: "
        + "@configura-ambiente nomebanco={database}.";

    /// <summary>O pedido de cópia da escolha atual; <c>null</c> enquanto falta algo.</summary>
    public DatabaseCopyRequest? Selection => ByProfile ? ProfileRequest() : AliasRequest();

    /// <summary>"Será criado: lock_eco_core_1010_aaaaMMdd_HHmmss" — a data e a hora são as da cópia.</summary>
    public string? DestinationPreview
    {
        get
        {
            if (ByProfile)
            {
                if (SelectedCopyProfile is not { } profile)
                {
                    return null;
                }

                var destination = _connections.FirstOrDefault(connection => connection.Id == profile.DestinationConnectionId);
                var source = _connections.FirstOrDefault(connection => connection.Id == profile.SourceConnectionId);

                if (destination is { HasDatabase: true })
                {
                    return profile.Options.RecreateDestination
                        ? $"Destino: {destination.Row.Database} em {destination.Name} — será apagado e criado de novo."
                        : $"Destino: {destination.Row.Database} em {destination.Name}.";
                }

                return (profile.SourceDatabase ?? source?.Row.Database) is { Length: > 0 } prefix
                    ? NewDatabasePreview(prefix, destination?.Name)
                    : null;
            }

            return SelectedSavedDatabase is { } saved && SelectedDestination is { } server
                ? NewDatabasePreview(saved.Alias, server.Name)
                : null;
        }
    }

    /// <summary>Pede a janela Bancos de Dados, para cadastrar conexões, apelidos e perfis.</summary>
    public event Action? ManageRequested;

    /// <summary>A cópia pelo card do ambiente pronto terminou bem: o ambiente mudou.</summary>
    public event Action<TaskDevelopmentView>? Copied;

    /// <summary>O ambiente que esta seção mostra. Sem ele, só o formulário de criar.</summary>
    public void Show(Guid taskId, TaskDevelopmentView? development)
    {
        _taskId = taskId;
        _developmentId = development?.Id;
        Current = development?.Database;
        CopyNowCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Os cadastros da janela Bancos de Dados. A cada vez que a escolha abre: podem ter mudado lá.</summary>
    public async Task LoadCatalogAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connections = await runner.RunAsync<GetDatabaseConnectionsHandler, IReadOnlyList<DatabaseConnectionRow>>(
                (handler, token) => handler.HandleAsync(new GetDatabaseConnections(), token), cancellationToken);
            var anonymization = await runner.RunAsync<GetAnonymizationProfilesHandler, IReadOnlyList<AnonymizationProfileRow>>(
                (handler, token) => handler.HandleAsync(new GetAnonymizationProfiles(), token), cancellationToken);
            var copies = await runner.RunAsync<GetDatabaseCopyProfilesHandler, IReadOnlyList<DatabaseCopyProfileRow>>(
                (handler, token) => handler.HandleAsync(new GetDatabaseCopyProfiles(), token), cancellationToken);
            var saved = await runner.RunAsync<GetSavedDatabasesHandler, IReadOnlyList<SavedDatabaseRow>>(
                (handler, token) => handler.HandleAsync(new GetSavedDatabases(), token), cancellationToken);

            SetCatalog(connections, anonymization, copies, saved);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            // Sem os cadastros, a cópia só não é oferecida; o resto da aba segue.
            logger.LogWarning(exception, "DevelopmentDatabaseCatalogLoadFailed");
            CatalogMessage = "Não foi possível carregar as conexões de banco.";
        }
    }

    internal void SetCatalog(
        IReadOnlyList<DatabaseConnectionRow> connections,
        IReadOnlyList<AnonymizationProfileRow> anonymizationProfiles,
        IReadOnlyList<DatabaseCopyProfileRow> copyProfiles,
        IReadOnlyList<SavedDatabaseRow> savedDatabases)
    {
        var profile = SelectedCopyProfile?.Id;
        var saved = SelectedSavedDatabase?.Id;
        var destination = SelectedDestination?.Id;
        var anonymization = SelectedAnonymizationProfile?.Id;
        var anonymize = Anonymize;

        _connections.Clear();
        _connections.AddRange(connections.Where(row => row.IsEnabled).Select(row => new DatabaseConnectionItemViewModel(row)));

        Replace(CopyProfiles, copyProfiles.Where(item => item.IsEnabled));
        Replace(SavedDatabases, savedDatabases);
        Replace(Destinations, _connections.Where(item => !item.HasDatabase && !item.IsProduction));
        Replace(AnonymizationProfiles, anonymizationProfiles.Where(item => item.IsEnabled));

        _restoring = true;

        try
        {
            SelectedCopyProfile = CopyProfiles.FirstOrDefault(item => item.Id == profile);
            SelectedSavedDatabase = SavedDatabases.FirstOrDefault(item => item.Id == saved);
            SelectedDestination = Destinations.FirstOrDefault(item => item.Id == destination)
                ?? (Destinations.Count == 1 ? Destinations[0] : null);
            SelectedAnonymizationProfile = AnonymizationProfiles.FirstOrDefault(item => item.Id == anonymization);
            Anonymize = anonymize;
        }
        finally
        {
            _restoring = false;
        }

        // Sem apelido e com perfil, começa pelo que existe.
        if (SelectedCopyProfile is null && SelectedSavedDatabase is null)
        {
            ByProfile = SavedDatabases.Count == 0 && CopyProfiles.Count > 0;
        }

        CatalogMessage = CopyProfiles.Count == 0 && SavedDatabases.Count == 0
            ? "Nenhum apelido de banco nem perfil de cópia cadastrado. Cadastre em Bancos de Dados…"
            : SavedDatabases.Count > 0 && Destinations.Count == 0 && CopyProfiles.Count == 0
                ? "Nenhuma conexão só de servidor fora de produção para receber o banco novo. Cadastre em Bancos de Dados…"
                : null;

        OnPropertyChanged(nameof(Selection));
        OnPropertyChanged(nameof(DestinationPreview));
        OnPropertyChanged(nameof(HasSelection));
        CopyNowCommand.NotifyCanExecuteChanged();
    }

    private bool _restoring;

    /// <summary>
    /// Os cadastros só são lidos quando a escolha abre — marcar a cópia no
    /// formulário, ou "Copiar banco…" no ambiente pronto. Abrir a aba não
    /// pergunta nada ao banco de operações; reabrir a escolha relê o que pode
    /// ter mudado na janela Bancos de Dados.
    /// </summary>
    partial void OnCopyOnCreateChanged(bool value)
    {
        if (value)
        {
            _ = LoadCatalogAsync(CancellationToken.None);
        }
    }

    partial void OnIsSelectingChanged(bool value)
    {
        if (value)
        {
            _ = LoadCatalogAsync(CancellationToken.None);
        }
    }

    partial void OnSelectedSavedDatabaseChanged(SavedDatabaseRow? value)
    {
        if (_restoring || value is null)
        {
            return;
        }

        // A anonimização do apelido vem junto; o usuário ainda pode trocar.
        Anonymize = value.AnonymizationProfileId is not null;
        SelectedAnonymizationProfile = AnonymizationProfiles.FirstOrDefault(item => item.Id == value.AnonymizationProfileId);
    }

    /// <summary>
    /// Valida a escolha e pede a confirmação de produção — sem processo
    /// nenhum. É chamado antes do Git, para recusar sem criar nada, e antes
    /// de copiar de novo num ambiente pronto.
    /// </summary>
    /// <param name="developmentId">O ambiente pronto; <c>null</c> = o worktree ainda vai ser criado.</param>
    /// <returns>A cópia pronta para rodar, ou <c>null</c> com o motivo na tela.</returns>
    public async Task<PlannedDatabaseCopy?> PrepareAsync(Guid taskId, Guid? developmentId = null)
    {
        ErrorMessage = null;
        ResultMessage = null;
        Problems.Clear();
        OnPropertyChanged(nameof(HasProblems));

        if (Selection is not { } request)
        {
            ErrorMessage = ByProfile
                ? "Escolha o perfil de cópia do banco."
                : "Escolha o apelido do banco e o servidor que vai receber a cópia.";
            return null;
        }

        IsValidating = true;

        try
        {
            var validation = await runner.RunAsync<ValidateDevelopmentDatabaseCopyHandler, DatabaseCopyValidation>(
                (handler, token) => handler.HandleAsync(new ValidateDevelopmentDatabaseCopy(taskId, request, developmentId), token),
                CancellationToken.None);

            if (!validation.CanRun)
            {
                Replace(Problems, validation.Decision.Violations.Select(violation => violation.Message)
                    .Concat(validation.Checks
                        .Where(check => check.Outcome == CheckOutcome.Fail)
                        .Select(check => check.Detail is { Length: > 0 } detail ? $"{check.Name}: {detail}" : check.Name))
                    .Distinct());
                OnPropertyChanged(nameof(HasProblems));
                ErrorMessage = validation.Decision.IsAllowed
                    ? "A cópia do banco não pode rodar: o ambiente não está pronto."
                    : "A cópia do banco foi bloqueada pela política de segurança.";
                return null;
            }

            string? typed = null;

            if (validation.RequiresProductionConfirmation)
            {
                if (!await confirmation.AskAsync(DatabaseCopyViewModel.ProductionConfirmation(validation, request)))
                {
                    return null;
                }

                typed = validation.RequiresTypedConfirmation ? validation.ConfirmationText : null;
            }

            return new PlannedDatabaseCopy(request, validation.RequiresProductionConfirmation, typed);
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "DevelopmentDatabaseValidationFailed {TaskId}", taskId);
            ErrorMessage = "Não foi possível validar a cópia do banco.";
        }
        finally
        {
            IsValidating = false;
        }

        return null;
    }

    /// <summary>
    /// Copia para o ambiente pronto. Devolve o ambiente com o banco novo, ou
    /// <c>null</c> se a cópia não terminou bem — o motivo fica na tela, e o
    /// banco anterior, se havia, continua o do ambiente.
    /// </summary>
    public async Task<TaskDevelopmentView?> RunAsync(Guid taskId, Guid developmentId, PlannedDatabaseCopy plan)
    {
        BeginRun(plan.Request);
        using var tracked = runs.Track(_run!);
        var progress = new UiProgress<DatabaseCopyProgress>(Apply);

        try
        {
            var result = await runner.RunAsync<CopyDevelopmentDatabaseHandler, DevelopmentDatabaseCopyResult>(
                (handler, token) => handler.HandleAsync(
                    new CopyDevelopmentDatabase(taskId, developmentId, plan.Request, plan.ProductionConfirmed, plan.TypedConfirmation),
                    progress,
                    token),
                _run!.Token);

            Complete(result.Copy);

            if (result.Copy.Succeeded)
            {
                Current = result.Development.Database;
                IsSelecting = false;
                return result.Development;
            }
        }
        catch (OperationCanceledException)
        {
            Fail("✗ Cópia do banco cancelada. O banco de destino pode ter ficado incompleto.");
        }
        catch (DomainException exception)
        {
            Fail("✗ " + exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "DevelopmentDatabaseCopyFailed {TaskId}", taskId);
            Fail("✗ A cópia do banco falhou por um erro inesperado; os detalhes estão no log do app.");
        }
        finally
        {
            IsRunning = false;
            CurrentStep = null;
            _run?.Dispose();
            _run = null;
        }

        return null;
    }

    private bool CanCopyNow() => !IsRunning && !IsValidating && Selection is not null && _developmentId is not null;

    /// <summary>"Copiar" no card do ambiente pronto: valida, confirma e copia.</summary>
    [RelayCommand(CanExecute = nameof(CanCopyNow))]
    public async Task CopyNowAsync()
    {
        if (_developmentId is not { } developmentId)
        {
            return;
        }

        if (await PrepareAsync(_taskId, developmentId) is not { } plan)
        {
            return;
        }

        if (await RunAsync(_taskId, developmentId, plan) is { } development)
        {
            Copied?.Invoke(development);
        }
    }

    private bool CanBeginSelecting() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanBeginSelecting))]
    public void BeginSelecting()
    {
        ErrorMessage = null;
        ResultMessage = null;
        IsSelecting = true;
    }

    [RelayCommand]
    public void CancelSelecting()
    {
        ErrorMessage = null;
        Problems.Clear();
        OnPropertyChanged(nameof(HasProblems));
        IsSelecting = false;
    }

    private bool CanCancel() => IsRunning;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void Cancel() => _run?.Cancel();

    [RelayCommand]
    public void ToggleLog() => IsLogOpen = !IsLogOpen;

    [RelayCommand]
    public void Manage() => ManageRequested?.Invoke();

    private bool CanCopyName() => Current is not null;

    [RelayCommand(CanExecute = nameof(CanCopyName))]
    public async Task CopyNameAsync()
    {
        if (Current is not { } database)
        {
            return;
        }

        try
        {
            await clipboard.WriteAsync(database.Name);
            ResultSucceeded = true;
            ResultMessage = $"Nome copiado: {database.Name}";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "DevelopmentDatabaseNameCopyFailed");
            ResultSucceeded = false;
            ResultMessage = "Não foi possível copiar o nome do banco.";
        }
    }

    /// <summary>A cópia escondida não para sozinha: só pelo [Cancelar cópia] ou fechando o app.</summary>
    public void CancelRun() => _run?.Cancel();

    private DatabaseCopyRequest? ProfileRequest() =>
        SelectedCopyProfile is { } profile
            ? new DatabaseCopyRequest(
                profile.SourceConnectionId,
                profile.DestinationConnectionId,
                profile.Options.RequireAnonymization ? DatabaseOperationType.CopyAndAnonymize : DatabaseOperationType.Copy,
                profile.Options.RequireAnonymization ? profile.AnonymizationProfileId : null,
                profile.Options,
                profile.Id,
                profile.SourceDatabase)
            : null;

    private DatabaseCopyRequest? AliasRequest()
    {
        if (SelectedSavedDatabase is not { } saved || SelectedDestination is not { } destination)
        {
            return null;
        }

        var anonymizes = Anonymize && SelectedAnonymizationProfile is not null;

        // Um banco novo a cada cópia: nada a recriar, nem dump a guardar.
        return new DatabaseCopyRequest(
            saved.ConnectionId,
            destination.Id,
            anonymizes ? DatabaseOperationType.CopyAndAnonymize : DatabaseOperationType.Copy,
            anonymizes ? SelectedAnonymizationProfile!.Id : null,
            new DatabaseCopyOptions(
                RequireAnonymization: anonymizes,
                IncludeSchema: true,
                IncludeData: true,
                RecreateDestination: false,
                VerifyAfterRestore: true,
                KeepAnonymizedArtifact: false),
            SourceDatabase: saved.DatabaseName,
            SavedDatabaseId: saved.Id);
    }

    private static string NewDatabasePreview(string prefix, string? server) =>
        $"Será criado: {SavedDatabase.CopyPrefix(prefix)}_aaaaMMdd_HHmmss"
        + (server is null ? string.Empty : $" em {server}")
        + " (data e hora da cópia)";

    private void BeginRun(DatabaseCopyRequest request)
    {
        _run?.Dispose();
        _run = new CancellationTokenSource();
        _anonymizes = request.Operation == DatabaseOperationType.CopyAndAnonymize;
        IsRunning = true;
        ErrorMessage = null;
        ResultMessage = null;
        Percent = 0;
        OnPropertyChanged(nameof(PercentText));
        CurrentStep = "Preparando a cópia…";
        Output.Reset(_anonymizes ? "Copiar + Anonimizar" : "Copiar");
    }

    internal void Apply(DatabaseCopyProgress progress)
    {
        if (progress.State == CommandStepState.Running)
        {
            CurrentStep = DatabaseCopySteps.Label(progress.Step, _anonymizes)
                +(progress.Detail is { Length: > 0 } detail ? $" — {detail}" : string.Empty);
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
        Output.State = result.Succeeded ? CommandStepState.Succeeded : CommandStepState.Failed;

        if (result.Succeeded)
        {
            Percent = 100;
            OnPropertyChanged(nameof(PercentText));
        }

        ResultSucceeded = result.Succeeded;
        ResultMessage = result.Status switch
        {
            DatabaseOperationStatus.Succeeded => $"✓ Banco copiado: {result.DestinationDatabase}.",
            DatabaseOperationStatus.Canceled => "✗ Cópia do banco cancelada. O banco de destino pode ter ficado incompleto.",
            _ => "✗ " + (result.Error ?? "A cópia do banco falhou."),
        };
    }

    private void Fail(string message)
    {
        Output.State = CommandStepState.Failed;
        ResultSucceeded = false;
        ResultMessage = message;
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();

        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
