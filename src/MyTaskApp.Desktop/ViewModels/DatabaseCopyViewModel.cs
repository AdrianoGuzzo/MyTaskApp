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
/// com as etapas, a barra e os logs.
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
    [NotifyPropertyChangedFor(nameof(Selection))]
    [NotifyCanExecuteChangedFor(nameof(ValidateCommand), nameof(ExecuteCommand))]
    private DatabaseConnectionItemViewModel? _selectedSource;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Selection))]
    [NotifyCanExecuteChangedFor(nameof(ValidateCommand), nameof(ExecuteCommand))]
    private DatabaseConnectionItemViewModel? _selectedDestination;

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

    public ObservableCollection<string> Violations { get; } = [];

    public ObservableCollection<CheckItemViewModel> Checks { get; } = [];

    public ObservableCollection<DatabaseCopyStepViewModel> Steps { get; } = [];

    public ObservableCollection<CheckItemViewModel> Verification { get; } = [];

    /// <summary>Os logs da cópia: as linhas das ferramentas, já mascaradas, no terminal de sempre.</summary>
    public CommandOutputViewModel Output { get; } = new();

    public bool Anonymizes => SelectedOperation.Value == DatabaseOperationType.CopyAndAnonymize;

    public bool CanEdit => !IsRunning;

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
                SelectedCopyProfile?.Id)
            : null;

    /// <summary>Terminou uma cópia — com qualquer desfecho. O histórico recarrega.</summary>
    public event Action? Finished;

    public void SetCatalog(
        IReadOnlyList<DatabaseConnectionItemViewModel> connections,
        IReadOnlyList<AnonymizationProfileRow> anonymizationProfiles,
        IReadOnlyList<DatabaseCopyProfileRow> copyProfiles)
    {
        var source = SelectedSource?.Id;
        var destination = SelectedDestination?.Id;
        var profile = SelectedAnonymizationProfile?.Id;

        Replace(Connections, connections.Where(connection => !connection.IsDisabled));
        Replace(AnonymizationProfiles, anonymizationProfiles.Where(item => item.IsEnabled));
        Replace(CopyProfiles, copyProfiles.Where(item => item.IsEnabled));

        SelectedSource = Connections.FirstOrDefault(connection => connection.Id == source);
        SelectedDestination = Connections.FirstOrDefault(connection => connection.Id == destination);
        SelectedAnonymizationProfile = AnonymizationProfiles.FirstOrDefault(item => item.Id == profile);
    }

    /// <summary>Preenche a tela com um perfil de cópia — o "ECO Production → ECO Development" de sempre.</summary>
    public void UseProfile(DatabaseCopyProfileRow profile)
    {
        SelectedCopyProfile = profile;
        SelectedSource = Connections.FirstOrDefault(connection => connection.Id == profile.SourceConnectionId);
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

    private bool CanValidate() => !IsBusy && !IsRunning && Selection is not null;

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

        if (request.Options.RecreateDestination)
        {
            message += $"\n\nO banco {validation.DestinationName} será apagado e criado de novo.";
        }

        return new ConfirmationRequest(
            "ATENÇÃO",
            message,
            "Confirmar operação",
            IsIrreversible: request.Options.RecreateDestination,
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
                $"✓ Cópia concluída. Dump anônimo mantido em {kept}.",
            DatabaseOperationStatus.Succeeded => "✓ Cópia concluída. Arquivos temporários removidos.",
            DatabaseOperationStatus.Canceled => "✗ Cancelada. O destino pode ter ficado incompleto.",
            _ => "✗ " + (result.Error ?? "A cópia falhou."),
        };
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
