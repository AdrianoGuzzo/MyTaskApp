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
    [NotifyCanExecuteChangedFor(nameof(CopyNowCommand), nameof(SaveAliasCommand))]
    private bool _byProfile;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(Selection), nameof(DestinationPreview), nameof(HasSelection), nameof(NeedsSourceDatabase),
        nameof(ProfileAlias), nameof(CanOfferAlias), nameof(ProfileAliasText))]
    [NotifyCanExecuteChangedFor(nameof(CopyNowCommand), nameof(SaveAliasCommand))]
    private DatabaseCopyProfileRow? _selectedCopyProfile;

    /// <summary>
    /// O banco de origem do perfil, quando a origem dele é só o servidor
    /// (ADR-057): vem do perfil, e pode ser trocado aqui — da lista do servidor
    /// ou digitado.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(Selection), nameof(DestinationPreview), nameof(HasSelection), nameof(ProfileAlias), nameof(CanOfferAlias),
        nameof(ProfileAliasText))]
    [NotifyCanExecuteChangedFor(nameof(CopyNowCommand), nameof(SaveAliasCommand))]
    private string? _sourceDatabase;

    /// <summary>Por que a lista de bancos do servidor não veio; dá para digitar o nome.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDatabaseListMessage))]
    private string? _databaseListMessage;

    /// <summary>O apelido a salvar para a origem e o banco escolhidos no perfil.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAliasCommand))]
    private string _newAlias = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAliasMessage))]
    private string? _aliasMessage;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveAliasCommand))]
    private bool _isSavingAlias;

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
    [NotifyPropertyChangedFor(nameof(HasDatabase), nameof(DatabaseServer), nameof(CopiedAtText), nameof(CopyActionLabel), nameof(VariablesText))]
    [NotifyCanExecuteChangedFor(nameof(CopyNameCommand))]
    private DevelopmentDatabaseView? _current;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    [NotifyCanExecuteChangedFor(nameof(CopyNowCommand), nameof(CancelCommand), nameof(BeginSelectingCommand), nameof(SaveAliasCommand))]
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

    /// <summary>Os bancos do servidor de origem do perfil, quando ele é só o servidor.</summary>
    public ObservableCollection<string> SourceDatabases { get; } = [];

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

    public bool HasDatabaseListMessage => !string.IsNullOrEmpty(DatabaseListMessage);

    public bool HasAliasMessage => !string.IsNullOrEmpty(AliasMessage);

    /// <summary>A origem do perfil escolhido, se a conexão ainda está cadastrada e ativa.</summary>
    private DatabaseConnectionItemViewModel? ProfileSource =>
        SelectedCopyProfile is { } profile
            ? _connections.FirstOrDefault(connection => connection.Id == profile.SourceConnectionId)
            : null;

    /// <summary>A origem do perfil é só o servidor: o banco é escolhido aqui.</summary>
    public bool NeedsSourceDatabase => SelectedCopyProfile is not null && ProfileSource is { HasDatabase: false };

    /// <summary>O banco de origem que vale no perfil: o fixo da conexão, o escolhido aqui, ou o salvo no perfil.</summary>
    private string? ProfileSourceDatabase
    {
        get
        {
            if (SelectedCopyProfile is not { } profile)
            {
                return null;
            }

            if (ProfileSource?.Row.Database is { } fixedDatabase)
            {
                return fixedDatabase;
            }

            return NeedsSourceDatabase
                ? (string.IsNullOrWhiteSpace(SourceDatabase) ? null : SourceDatabase.Trim())
                : profile.SourceDatabase;
        }
    }

    /// <summary>
    /// O apelido já salvo para a origem e o banco do perfil. Com ele, o banco
    /// novo leva o nome do apelido, como na aba Copiar Banco (ADR-057).
    /// </summary>
    public SavedDatabaseRow? ProfileAlias =>
        SelectedCopyProfile is { } profile && ProfileSourceDatabase is { } database
            ? SavedDatabases.FirstOrDefault(saved =>
                saved.ConnectionId == profile.SourceConnectionId
                && string.Equals(saved.DatabaseName, database, StringComparison.Ordinal))
            : null;

    /// <summary>Há origem e banco para guardar como apelido, e eles ainda não têm um.</summary>
    public bool CanOfferAlias => ProfileSource is not null && ProfileSourceDatabase is not null && ProfileAlias is null;

    public string? ProfileAliasText => ProfileAlias is { } alias
        ? $"Já salvo como o apelido {alias.Alias}: na próxima vez, escolha-o em Apelido + servidor."
        : null;

    public string PercentText => string.Create(CultureInfo.InvariantCulture, $"{Percent:0}%");

    public string? DatabaseServer => Current is { } database
        ? string.Create(CultureInfo.InvariantCulture, $"{database.Host}:{database.Port} · usuário {database.Username} · {database.ConnectionName}")
        : null;

    /// <summary>"Nos comandos: {database} ou {lock_eco_core_1010}" — o nome é a variável.</summary>
    public string? VariablesText => Current is { } database
        ? database.Alias is { } alias
            ? $"Nos comandos: {{database}} ou {{{alias}}} (copiado do apelido {alias})"
            : "Nos comandos: {database}"
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

    /// <summary>
    /// O apelido de onde a cópia marcada vem — o que <c>{apelido}</c> vai valer
    /// (ADR-060). <c>null</c> sem cópia marcada, ou num perfil sem apelido.
    /// </summary>
    public string? ScheduledAlias => !CopyOnCreate || Selection is null
        ? null
        : ByProfile ? ProfileAlias?.Alias : SelectedSavedDatabase?.Alias;

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

                if (destination is { HasDatabase: true })
                {
                    return profile.Options.RecreateDestination
                        ? $"Destino: {destination.Row.Database} em {destination.Name} — será apagado e criado de novo."
                        : $"Destino: {destination.Row.Database} em {destination.Name}.";
                }

                return (ProfileAlias?.Alias ?? ProfileSourceDatabase) is { Length: > 0 } prefix
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
        OnPropertyChanged(nameof(NeedsSourceDatabase));
        OnPropertyChanged(nameof(ProfileAlias));
        OnPropertyChanged(nameof(CanOfferAlias));
        OnPropertyChanged(nameof(ProfileAliasText));
        CopyNowCommand.NotifyCanExecuteChanged();
        SaveAliasCommand.NotifyCanExecuteChanged();
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

    private DatabaseCopyRequest? ProfileRequest()
    {
        if (SelectedCopyProfile is not { } profile)
        {
            return null;
        }

        // Origem só de servidor sem banco escolhido: falta escolher.
        if (NeedsSourceDatabase && ProfileSourceDatabase is null)
        {
            return null;
        }

        return new DatabaseCopyRequest(
            profile.SourceConnectionId,
            profile.DestinationConnectionId,
            profile.Options.RequireAnonymization ? DatabaseOperationType.CopyAndAnonymize : DatabaseOperationType.Copy,
            profile.Options.RequireAnonymization ? profile.AnonymizationProfileId : null,
            profile.Options,
            profile.Id,
            NeedsSourceDatabase ? ProfileSourceDatabase : profile.SourceDatabase,
            ProfileAlias?.Id);
    }

    partial void OnSelectedCopyProfileChanged(DatabaseCopyProfileRow? value)
    {
        if (_restoring)
        {
            return;
        }

        // O banco salvo no perfil vem preenchido; o usuário pode trocar.
        SourceDatabase = value?.SourceDatabase;
        AliasMessage = null;
        NewAlias = string.Empty;
        _ = LoadSourceDatabasesAsync();
    }

    /// <summary>A lista do servidor de origem. Se não vier (sem acesso ao banco de manutenção), o nome pode ser digitado.</summary>
    private async Task LoadSourceDatabasesAsync()
    {
        SourceDatabases.Clear();
        DatabaseListMessage = null;

        if (!NeedsSourceDatabase || ProfileSource is not { } source)
        {
            return;
        }

        try
        {
            var databases = await runner.RunAsync<ListServerDatabasesHandler, IReadOnlyList<string>>(
                (handler, token) => handler.HandleAsync(new ListServerDatabases(source.Id), token), CancellationToken.None);

            // O perfil pode ter mudado enquanto o servidor respondia.
            if (ProfileSource?.Id != source.Id)
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
            logger.LogWarning(exception, "DevelopmentDatabaseServerListFailed");
            DatabaseListMessage = "Não foi possível listar os bancos do servidor; digite o nome.";
        }
    }

    private bool CanSaveAlias() =>
        !IsRunning && !IsSavingAlias && ByProfile && CanOfferAlias && !string.IsNullOrWhiteSpace(NewAlias);

    /// <summary>
    /// Guarda a origem, o banco e a anonimização do perfil como um apelido
    /// (ADR-057): repetir a cópia vira escolher o apelido, e o banco novo leva
    /// o nome dele.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanSaveAlias))]
    public async Task SaveAliasAsync()
    {
        if (SelectedCopyProfile is not { } profile || ProfileSourceDatabase is not { } database)
        {
            return;
        }

        AliasMessage = null;
        IsSavingAlias = true;

        var command = new SaveSavedDatabase(
            null,
            NewAlias,
            profile.SourceConnectionId,
            database,
            profile.Options.RequireAnonymization ? profile.AnonymizationProfileId : null);

        try
        {
            await runner.RunAsync<SaveSavedDatabaseHandler, Guid>(
                (handler, token) => handler.HandleAsync(command, token), CancellationToken.None);

            var saved = SavedDatabase.NormalizeAlias(NewAlias);
            await LoadCatalogAsync(CancellationToken.None);
            NewAlias = string.Empty;
            AliasMessage = $"✓ Apelido {saved} salvo. O banco novo já leva o nome dele.";
        }
        catch (DomainException exception)
        {
            AliasMessage = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "DevelopmentDatabaseAliasSaveFailed");
            AliasMessage = "Não foi possível salvar o apelido.";
        }
        finally
        {
            IsSavingAlias = false;
        }
    }

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
