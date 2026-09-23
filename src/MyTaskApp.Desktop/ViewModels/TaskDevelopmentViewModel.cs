using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Commands;
using MyTaskApp.Application.Development;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Desktop.Development;
using MyTaskApp.Desktop.Views;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>O que a aba Desenvolvimento está mostrando.</summary>
public enum DevelopmentPanelState
{
    /// <summary>Ainda perguntando ao banco e ao Git.</summary>
    Loading,

    /// <summary>Sem Git: só as instruções de instalação.</summary>
    GitMissing,

    /// <summary>O formulário: diretório, origem, nova branch.</summary>
    Setup,

    /// <summary>"Iniciar implementação" em andamento.</summary>
    Running,

    /// <summary>A pasta do worktree já existe; o usuário escolhe o que fazer.</summary>
    Conflict,

    /// <summary>Uma etapa falhou; o formulário volta, com o motivo em cima.</summary>
    Failed,

    /// <summary>O worktree existe e está gravado na tarefa.</summary>
    Ready,
}

/// <summary>
/// A aba Desenvolvimento da tarefa (ADR-027): prepara um worktree Git para
/// implementar a tarefa, e depois dá os atalhos para trabalhar nele.
/// </summary>
/// <remarks>
/// <para>
/// Nada de Git aqui: tudo passa por casos de uso via <see cref="IUseCaseRunner"/>.
/// O ViewModel só orquestra as duas metades — preparar e criar — porque entre
/// elas pode haver uma pergunta ao usuário (a pasta já existe), e pergunta é
/// assunto de tela.
/// </para>
/// <para>
/// O progresso chega por um <see cref="IProgress{T}"/> síncrono, e não pelo
/// <see cref="Progress{T}"/> do BCL: os casos de uso continuam na thread de UI
/// depois de cada <c>await</c> (não usam <c>ConfigureAwait(false)</c>), então o
/// aviso já chega onde precisa. O <see cref="Progress{T}"/> postaria cada
/// aviso para depois, e a lista andaria fora de ordem com o resultado.
/// </para>
/// </remarks>
public sealed partial class TaskDevelopmentViewModel(
    IUseCaseRunner runner,
    IClipboardWriter clipboard,
    IShellLauncher shell,
    IConfirmationDialog confirmation,
    TimeProvider timeProvider,
    AgentSessionViewModel agent,
    ILogger<TaskDevelopmentViewModel> logger) : ObservableObject
{
    /// <summary>Espera entre a última tecla no campo Diretório e a pergunta ao Git.</summary>
    public static readonly TimeSpan InspectionDelay = TimeSpan.FromMilliseconds(400);

    private static readonly DevelopmentStep[] RunSteps =
    [
        DevelopmentStep.CheckGit,
        DevelopmentStep.ValidateDirectory,
        DevelopmentStep.ValidateRepository,
        DevelopmentStep.Fetch,
        DevelopmentStep.ValidateSource,
        DevelopmentStep.CheckChanges,
        DevelopmentStep.UpdateSource,
        DevelopmentStep.ValidateBranchName,
        DevelopmentStep.PlanWorktreePath,
        DevelopmentStep.CreateWorktree,
        DevelopmentStep.ValidateWorktree,
        DevelopmentStep.SaveTask,
    ];

    private Guid _taskId;

    private string _taskTitle = string.Empty;

    /// <summary>A origem a escolher quando as branches chegarem (a da tentativa anterior).</summary>
    private string? _preferredSource;

    private CancellationTokenSource? _inspection;

    private CancellationTokenSource? _run;

    /// <summary>Mudança de texto vinda do próprio ViewModel, que não deve agendar inspeção.</summary>
    private bool _settingDirectory;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsLoading), nameof(IsGitMissing), nameof(ShowForm), nameof(IsFormEnabled),
        nameof(IsRunning), nameof(ShowSteps), nameof(IsConflict), nameof(ShowFailure), nameof(IsReady))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private DevelopmentPanelState _state = DevelopmentPanelState.Loading;

    /// <summary>Concluída, a tarefa não começa implementação nova — mas o que existe continua acessível.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFormEnabled))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isReadOnly;

    // --- Git ---------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(GitStatusText))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private GitInstallation? _git;

    [ObservableProperty]
    private bool _isCheckingGit;

    public GitInstallInstructions Instructions { get; } = GitInstallGuide.ForCurrentSystem();

    public string GitStatusText => Git switch
    {
        null => "Verificando o Git…",
        { IsInstalled: true, Version: var version } => $"✓ Git encontrado — versão {version}",
        _ => "✗ Git CLI não encontrado",
    };

    // --- Diretório ---------------------------------------------------------

    /// <summary>O <c>@alias</c> no campo Diretório vira o caminho do diretório da etiqueta.</summary>
    public AliasCompletionViewModel DirectoryCompletion { get; } = new();

    [ObservableProperty]
    private string _directoryText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectoryStatusText), nameof(HasDirectoryStatus), nameof(DirectoryFound))]
    private bool? _directoryExists;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RepositoryStatusText), nameof(HasRepositoryStatus), nameof(RepositoryValid))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool? _isRepository;

    /// <summary>O worktree principal: de onde sai o nome do projeto e a pasta irmã.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(WorktreePreview))]
    private string? _repositoryPath;

    [ObservableProperty]
    private bool _isInspecting;

    public bool HasDirectoryStatus => DirectoryExists is not null;

    public bool DirectoryFound => DirectoryExists is true;

    public string DirectoryStatusText => DirectoryExists is true
        ? "✓ Diretório encontrado"
        : "✗ Diretório não encontrado";

    public bool HasRepositoryStatus => DirectoryExists is true && IsRepository is not null;

    public bool RepositoryValid => IsRepository is true;

    public string RepositoryStatusText => IsRepository is true
        ? "✓ Repositório Git válido"
        : "✗ Não é um repositório Git";

    // --- Branches ----------------------------------------------------------

    public ObservableCollection<BranchOptionViewModel> BranchOptions { get; } = [];

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private BranchOptionViewModel? _selectedBranchOption;

    [ObservableProperty]
    private bool _isLoadingBranches;

    [ObservableProperty]
    private string? _branchesError;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BranchNameError), nameof(HasBranchNameError), nameof(WorktreePreview))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _newBranchName = string.Empty;

    public string? BranchNameError => GitBranchName.Validate(NewBranchName?.Trim());

    public bool HasBranchNameError => BranchNameError is not null && !string.IsNullOrEmpty(NewBranchName);

    /// <summary>Onde o worktree vai nascer, calculado enquanto o usuário digita.</summary>
    public string? WorktreePreview
    {
        get
        {
            if (RepositoryPath is null || BranchNameError is not null)
            {
                return null;
            }

            try
            {
                return WorktreePathPlanner.Plan(RepositoryPath, NewBranchName.Trim());
            }
            catch (DomainException)
            {
                return null;
            }
        }
    }

    // --- Execução ----------------------------------------------------------

    public ObservableCollection<DevelopmentStepViewModel> Steps { get; } = [];

    /// <summary>Só a preparação pode ser cancelada; criar o worktree, não.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CancelRunCommand))]
    private bool _canCancel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowFailure))]
    private DevelopmentFailureViewModel? _failure;

    [ObservableProperty]
    private bool _isDetailsOpen;

    [ObservableProperty]
    private DevelopmentPlan? _plan;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ConflictMessage), nameof(CanAdopt), nameof(AdoptLabel))]
    private WorktreeConflict? _conflict;

    [ObservableProperty]
    private string _alternativePath = string.Empty;

    [ObservableProperty]
    private bool _isChoosingAlternative;

    public string ConflictMessage => Conflict is null ? string.Empty
        : Conflict.CanAdopt
            ? $"O diretório de worktree já existe e é um worktree deste repositório.{Environment.NewLine}{Conflict.Path}"
            : $"O diretório de worktree já existe.{Environment.NewLine}{Conflict.Path}";

    public bool CanAdopt => Conflict?.CanAdopt == true;

    public string AdoptLabel => Conflict?.Registered?.BranchName is { } branch && branch != Plan?.NewBranch
        ? $"Usar Worktree existente (branch {branch})"
        : "Usar Worktree existente";

    // --- Comandos pós-Worktree (ADR-028) ------------------------------------

    /// <summary>
    /// A lista que roda depois de o worktree ficar pronto. Editável no formulário
    /// e com o ambiente pronto; nunca durante a criação.
    /// </summary>
    public PostWorktreeCommandsViewModel Commands { get; } = new();

    /// <summary>Pede a janela de comandos globais.</summary>
    public event Action? CommandsRequested;

    // --- Agente de IA (ADR-029) ---------------------------------------------

    /// <summary>O card do Claude Code: só aparece com o worktree pronto.</summary>
    public AgentSessionViewModel Agent { get; } = agent;

    // --- Pronto ------------------------------------------------------------

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviousAttempt))]
    private TaskDevelopmentView? _development;

    /// <summary>O que aconteceu na tentativa anterior (falhou, foi interrompida, foi removida).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviousAttempt))]
    private string? _previousAttempt;

    public bool HasPreviousAttempt => !string.IsNullOrEmpty(PreviousAttempt);

    /// <summary>O worktree tem alterações e a remoção foi recusada.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRemoveBlocked), nameof(RemoveChangesText))]
    private IReadOnlyList<string>? _removeChanges;

    [ObservableProperty]
    private bool _isShowingRemoveChanges;

    [ObservableProperty]
    private bool _isRemoving;

    public bool IsRemoveBlocked => RemoveChanges is { Count: > 0 };

    public string RemoveChangesText => string.Join(Environment.NewLine, RemoveChanges ?? []);

    /// <summary>Um recado curto: "Caminho copiado", "Não foi possível abrir…".</summary>
    [ObservableProperty]
    private string? _message;

    // --- Estados derivados, para o XAML -------------------------------------

    public bool IsLoading => State is DevelopmentPanelState.Loading;

    public bool IsGitMissing => State is DevelopmentPanelState.GitMissing;

    public bool ShowForm => State is DevelopmentPanelState.Setup or DevelopmentPanelState.Running
        or DevelopmentPanelState.Conflict or DevelopmentPanelState.Failed;

    public bool IsFormEnabled => !IsReadOnly && State is DevelopmentPanelState.Setup or DevelopmentPanelState.Failed;

    public bool IsRunning => State is DevelopmentPanelState.Running;

    public bool ShowSteps => State is DevelopmentPanelState.Running or DevelopmentPanelState.Conflict
        or DevelopmentPanelState.Failed;

    public bool IsConflict => State is DevelopmentPanelState.Conflict;

    public bool ShowFailure => State is DevelopmentPanelState.Failed && Failure is not null;

    public bool IsReady => State is DevelopmentPanelState.Ready;

    public bool CanStart =>
        !IsReadOnly
        && State is DevelopmentPanelState.Setup or DevelopmentPanelState.Failed
        && Git is { IsInstalled: true }
        && IsRepository is true
        && SelectedBranchOption is { IsSelectable: true }
        && BranchNameError is null;

    partial void OnStateChanged(DevelopmentPanelState value)
    {
        SyncCommandsEditable();

        // O agente abre no worktree: só com ele pronto há o que mostrar.
        if (value is DevelopmentPanelState.Ready)
        {
            _ = Agent.RefreshAsync(CancellationToken.None);
        }
    }

    partial void OnIsReadOnlyChanged(bool value) => SyncCommandsEditable();

    private void SyncCommandsEditable() =>
        Commands.IsEditable = !IsReadOnly
            && State is DevelopmentPanelState.Setup or DevelopmentPanelState.Failed or DevelopmentPanelState.Ready;

    /// <summary>A tarefa que esta aba prepara. Chamado uma vez, na abertura da janela.</summary>
    public void Load(Guid taskId, string taskTitle, bool isReadOnly)
    {
        _taskId = taskId;
        _taskTitle = taskTitle;
        IsReadOnly = isReadOnly;
        Agent.Load(taskId, isReadOnly);
        DirectoryCompletion.IsEnabled = !isReadOnly;
        NewBranchName = GitBranchName.Suggest(taskTitle);
        State = DevelopmentPanelState.Loading;
    }

    /// <summary>
    /// A cada vez que a aba aparece: o que está gravado na tarefa, e se o Git
    /// está lá. Não mexe numa execução em andamento nem numa pergunta aberta.
    /// </summary>
    public async Task ActivateAsync(CancellationToken cancellationToken)
    {
        if (State is DevelopmentPanelState.Running or DevelopmentPanelState.Conflict)
        {
            return;
        }

        try
        {
            var development = await runner.RunAsync<GetTaskDevelopmentHandler, TaskDevelopmentView?>(
                (handler, token) => handler.HandleAsync(new GetTaskDevelopment(_taskId), token),
                cancellationToken);

            Development = development;
            ShowSavedCommands(development);
            await LoadGlobalCommandsAsync(cancellationToken);

            if (development is { Status: TaskDevelopmentStatus.Ready })
            {
                State = DevelopmentPanelState.Ready;
                return;
            }

            ShowPreviousAttempt(development);

            await DetectGitAsync(cancellationToken);

            if (State is DevelopmentPanelState.Setup && DirectoryExists is null && DirectoryText.Length > 0)
            {
                await InspectDirectoryAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "TaskDevelopmentLoadFailed {TaskId}", _taskId);
            Message = "Não foi possível carregar o ambiente de desenvolvimento desta tarefa.";
            State = DevelopmentPanelState.Setup;
        }
    }

    /// <summary>"Verificar novamente": procura o Git de novo, do zero.</summary>
    [RelayCommand]
    public async Task RecheckGitAsync(CancellationToken cancellationToken)
    {
        await DetectGitAsync(cancellationToken);

        if (Git is { IsInstalled: true } && DirectoryText.Length > 0)
        {
            await InspectDirectoryAsync(cancellationToken);
        }
    }

    private async Task DetectGitAsync(CancellationToken cancellationToken)
    {
        IsCheckingGit = true;

        try
        {
            Git = await runner.RunAsync<DetectGitHandler, GitInstallation>(
                (handler, token) => handler.HandleAsync(new DetectGit(), token),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "GitDetectionFailed");
            Git = GitInstallation.Missing;
        }
        finally
        {
            IsCheckingGit = false;
        }

        // Pronto não depende do Git; e uma falha na tela continua lá quando o
        // usuário volta de outra aba — os detalhes são justamente o que ele foi ler.
        if (State is DevelopmentPanelState.Ready
            || (State is DevelopmentPanelState.Failed && Git.IsInstalled))
        {
            return;
        }

        State = Git.IsInstalled ? DevelopmentPanelState.Setup : DevelopmentPanelState.GitMissing;
    }

    partial void OnDirectoryTextChanged(string value)
    {
        if (_settingDirectory)
        {
            return;
        }

        // O que se sabia era da pasta anterior.
        DirectoryExists = null;
        IsRepository = null;
        RepositoryPath = null;
        BranchOptions.Clear();
        SelectedBranchOption = null;
        BranchesError = null;

        _inspection?.Cancel();
        _inspection = new CancellationTokenSource();

        _ = InspectLaterAsync(_inspection.Token);
    }

    private async Task InspectLaterAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(InspectionDelay, timeProvider, cancellationToken);
            await InspectDirectoryAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
    }

    /// <summary>
    /// Confere a pasta e, se for repositório, carrega as branches. Um <c>@</c>
    /// ainda sendo digitado não é pasta: espera o alias virar caminho.
    /// </summary>
    public async Task InspectDirectoryAsync(CancellationToken cancellationToken)
    {
        var path = DirectoryText.Trim().Trim('"').Trim();

        if (path.Length == 0 || path.StartsWith('@') || Git is not { IsInstalled: true })
        {
            return;
        }

        IsInspecting = true;

        try
        {
            var inspection = await runner.RunAsync<InspectDirectoryHandler, DirectoryInspection>(
                (handler, token) => handler.HandleAsync(new InspectDirectory(path), token),
                cancellationToken);

            if (!string.Equals(path, DirectoryText.Trim().Trim('"').Trim(), StringComparison.Ordinal))
            {
                return;
            }

            DirectoryExists = inspection.Exists;
            IsRepository = inspection.IsRepository;
            RepositoryPath = inspection.RepositoryPath;

            if (inspection is { IsRepository: true, RepositoryPath: { } repository })
            {
                await LoadBranchesAsync(repository, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "DirectoryInspectionFailed {Path}", path);
            DirectoryExists = null;
            Message = "Não foi possível conferir o diretório.";
        }
        finally
        {
            IsInspecting = false;
        }
    }

    private async Task LoadBranchesAsync(string repository, CancellationToken cancellationToken)
    {
        IsLoadingBranches = true;
        BranchesError = null;

        try
        {
            var list = await runner.RunAsync<ListBranchesHandler, BranchList>(
                (handler, token) => handler.HandleAsync(new ListBranches(repository), token),
                cancellationToken);

            var previous = SelectedBranchOption?.Branch?.FullRef;

            BranchOptions.Clear();

            foreach (var option in BranchOptionViewModel.Group(list.Branches))
            {
                BranchOptions.Add(option);
            }

            // Só branches: o cabeçalho de grupo tem Branch nulo, e "nulo == nulo"
            // o escolheria quando não há escolha anterior.
            var selectable = BranchOptions.Where(option => option.IsSelectable).ToList();

            SelectedBranchOption =
                selectable.FirstOrDefault(option => option.Branch!.FullRef == previous)
                ?? selectable.FirstOrDefault(option => option.Branch!.ShortName == _preferredSource)
                ?? selectable.FirstOrDefault(option => option.Branch == list.Suggested)
                ?? selectable.FirstOrDefault();

            if (list.Branches.Count == 0)
            {
                BranchesError = "O repositório não tem nenhuma branch ainda. Faça o primeiro commit.";
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "BranchListFailed {Repository}", repository);
            BranchesError = "Não foi possível listar as branches do repositório.";
        }
        finally
        {
            IsLoadingBranches = false;
        }
    }

    /// <summary>Cabeçalho de grupo não é escolha: volta para a branch anterior.</summary>
    partial void OnSelectedBranchOptionChanged(BranchOptionViewModel? oldValue, BranchOptionViewModel? newValue)
    {
        if (newValue is { IsHeader: true })
        {
            SelectedBranchOption = oldValue is { IsSelectable: true } ? oldValue : null;
        }
    }

    // --- Iniciar implementação ---------------------------------------------

    [RelayCommand(CanExecute = nameof(CanStart))]
    public async Task StartAsync()
    {
        if (!CanStart || SelectedBranchOption?.Branch is not { } source)
        {
            return;
        }

        // Um @alias que não existe é descoberto agora, e não com o worktree já
        // criado e a lista parada na primeira etapa.
        if (!await ValidateCommandsAsync())
        {
            return;
        }

        ResetRun();
        State = DevelopmentPanelState.Running;
        CanCancel = true;

        _run = new CancellationTokenSource();
        var progress = new InlineProgress(OnProgress);

        DevelopmentPlan plan;

        try
        {
            var command = new PrepareDevelopment(_taskId, DirectoryText, source.FullRef, NewBranchName.Trim());

            plan = await runner.RunAsync<PrepareDevelopmentHandler, DevelopmentPlan>(
                (handler, token) => handler.HandleAsync(command, progress, token),
                _run.Token);
        }
        catch (Exception exception)
        {
            HandleFailure(exception);
            return;
        }
        finally
        {
            CanCancel = false;
        }

        Plan = plan;

        if (plan.Conflict is { } conflict)
        {
            Conflict = conflict;
            AlternativePath = conflict.SuggestedPath;
            IsChoosingAlternative = false;
            State = DevelopmentPanelState.Conflict;
            return;
        }

        await CreateAsync(plan, plan.WorktreePath, adopt: false);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    public void CancelRun() => _run?.Cancel();

    /// <summary>"Usar Worktree existente".</summary>
    [RelayCommand]
    public async Task UseExistingAsync()
    {
        if (Plan is { } plan && Conflict is { CanAdopt: true } conflict)
        {
            await CreateAsync(plan, conflict.Path, adopt: true);
        }
    }

    /// <summary>"Escolher outro caminho": mostra o campo, já com uma sugestão livre.</summary>
    [RelayCommand]
    public void ChooseAnotherPath() => IsChoosingAlternative = true;

    [RelayCommand]
    public async Task UseAlternativePathAsync()
    {
        if (Plan is { } plan && !string.IsNullOrWhiteSpace(AlternativePath))
        {
            await CreateAsync(plan, AlternativePath.Trim(), adopt: false);
        }
    }

    [RelayCommand]
    public void CancelConflict()
    {
        Conflict = null;
        Plan = null;
        IsChoosingAlternative = false;
        State = DevelopmentPanelState.Setup;
    }

    private async Task CreateAsync(DevelopmentPlan plan, string path, bool adopt)
    {
        State = DevelopmentPanelState.Running;
        Conflict = null;

        var progress = new InlineProgress(OnProgress);

        try
        {
            // Sem token: criar o worktree não se interrompe (ver StartDevelopmentHandler).
            var commands = Commands.Entries;

            var development = await runner.RunAsync<StartDevelopmentHandler, TaskDevelopmentView>(
                (handler, token) => handler.HandleAsync(
                    new StartDevelopment(plan, path, adopt, commands), progress, token),
                CancellationToken.None);

            Development = development;
            PreviousAttempt = null;
            Commands.MarkSaved();
            State = DevelopmentPanelState.Ready;
            Message = "✓ Implementação iniciada.";
        }
        catch (Exception exception)
        {
            HandleFailure(exception);
            return;
        }

        // Só aqui, com o worktree pronto: a falha acima retorna antes (ADR-028).
        if (Commands.Entries.Count > 0)
        {
            await RunCommandsAsync();
        }
    }

    // --- Comandos pós-Worktree ---------------------------------------------

    /// <summary>
    /// Roda a lista gravada, em ordem, no worktree. Com a lista alterada, grava
    /// antes: roda o que está na tela, e o que está na tela é o que fica.
    /// </summary>
    [RelayCommand]
    public async Task RunCommandsAsync()
    {
        if (State is not DevelopmentPanelState.Ready || Commands.IsRunning)
        {
            return;
        }

        if (Commands.IsDirty && !await SaveCommandsCoreAsync())
        {
            return;
        }

        if (Commands.Entries.Count == 0)
        {
            Message = "Nenhum comando para executar. Adicione um na lista.";
            return;
        }

        var token = Commands.BeginRun();
        var progress = new UiProgress<CommandStepProgress>(Commands.Apply);

        try
        {
            var summary = await runner.RunAsync<RunDevelopmentCommandsHandler, CommandRunSummary>(
                (handler, cancel) => handler.HandleAsync(new RunDevelopmentCommands(_taskId), progress, cancel),
                token);

            Commands.Complete(summary);
        }
        catch (OperationCanceledException)
        {
            Commands.Fail("Execução cancelada antes de começar. Nenhum comando rodou.");
        }
        catch (DomainException exception)
        {
            Commands.Fail(exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "DevelopmentCommandsFailed {TaskId}", _taskId);
            Commands.Fail("Não foi possível executar os comandos.");
        }
    }

    [RelayCommand]
    public void CancelCommands() => Commands.Cancel();

    /// <summary>"Salvar comandos", com o ambiente pronto.</summary>
    [RelayCommand]
    public async Task SaveCommandsAsync()
    {
        if (await SaveCommandsCoreAsync())
        {
            Message = "Comandos salvos.";
        }
    }

    [RelayCommand]
    public void OpenGlobalCommands() => CommandsRequested?.Invoke();

    /// <summary>
    /// Os comandos globais, para o autocomplete e o aviso de alias inexistente.
    /// A cada ativação: podem ter mudado na outra janela.
    /// </summary>
    public async Task LoadGlobalCommandsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var rows = await runner.RunAsync<GetDevelopmentCommandsHandler, IReadOnlyList<DevelopmentCommandRow>>(
                (handler, token) => handler.HandleAsync(new GetDevelopmentCommands(), token),
                cancellationToken);

            Commands.SetCatalog(rows);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Sem a lista, o autocomplete só não abre; digitar o comando continua valendo.
            logger.LogWarning(exception, "DevelopmentCommandsLoadFailed");
        }
    }

    private async Task<bool> SaveCommandsCoreAsync()
    {
        if (Development is null || State is not DevelopmentPanelState.Ready)
        {
            return false;
        }

        try
        {
            var entries = Commands.Entries;

            Development = await runner.RunAsync<SetDevelopmentCommandsHandler, TaskDevelopmentView>(
                (handler, token) => handler.HandleAsync(new SetDevelopmentCommands(_taskId, entries), token),
                CancellationToken.None);

            Commands.MarkSaved();
            return true;
        }
        catch (DomainException exception)
        {
            Message = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "DevelopmentCommandsSaveFailed {TaskId}", _taskId);
            Message = "Não foi possível salvar os comandos.";
        }

        return false;
    }

    private async Task<bool> ValidateCommandsAsync()
    {
        var entries = Commands.Entries;

        if (entries.Count == 0)
        {
            return true;
        }

        try
        {
            await runner.RunAsync<ValidateCommandEntriesHandler, IReadOnlyList<ResolvedCommand>>(
                (handler, token) => handler.HandleAsync(new ValidateCommandEntries(entries), token),
                CancellationToken.None);

            return true;
        }
        catch (DomainException exception)
        {
            Message = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "DevelopmentCommandsValidationFailed {TaskId}", _taskId);
            Message = "Não foi possível conferir os comandos pós-Worktree.";
        }

        return false;
    }

    /// <summary>
    /// A lista gravada, a menos que o usuário esteja no meio de uma edição ou de
    /// uma execução. Igual à da tela, não mexe: trocar apagaria o output.
    /// </summary>
    private void ShowSavedCommands(TaskDevelopmentView? development)
    {
        var saved = development?.Commands ?? [];

        if (Commands.IsDirty || Commands.IsRunning || saved.SequenceEqual(Commands.Entries))
        {
            return;
        }

        Commands.SetEntries(saved);
    }

    private void OnProgress(DevelopmentProgress progress)
    {
        var step = Steps.FirstOrDefault(item => item.Step == progress.Step);

        if (step is null)
        {
            step = new DevelopmentStepViewModel(progress.Step);
            Steps.Add(step);
        }

        step.State = progress.State switch
        {
            DevelopmentStepState.Running => StepVisualState.Running,
            DevelopmentStepState.Done => StepVisualState.Done,
            DevelopmentStepState.Warning => StepVisualState.Warning,
            _ => StepVisualState.Skipped,
        };

        if (progress.Note is not null || progress.State is not DevelopmentStepState.Running)
        {
            step.Note = progress.Note;
        }
    }

    private void HandleFailure(Exception exception)
    {
        var running = Steps.LastOrDefault(step => step.IsRunning);

        switch (exception)
        {
            case OperationCanceledException:
                MarkFailed(running, "Cancelado.");
                Failure = new DevelopmentFailureViewModel(
                    running?.Title ?? "Preparação",
                    "Operação cancelada. Nada foi criado.",
                    null,
                    []);
                break;

            case DevelopmentStepException step:
                var failed = Steps.FirstOrDefault(item => item.Step == step.Step) ?? running;
                MarkFailed(failed, null);
                Failure = new DevelopmentFailureViewModel(
                    DevelopmentStepViewModel.TitleOf(step.Step),
                    step.Message,
                    step.Command,
                    step.Changes);
                break;

            case DomainException domain:
                MarkFailed(running, null);
                Failure = new DevelopmentFailureViewModel(running?.Title ?? "Preparação", domain.Message, null, []);
                break;

            default:
                logger.LogError(exception, "TaskDevelopmentFailed {TaskId}", _taskId);
                MarkFailed(running, null);
                Failure = new DevelopmentFailureViewModel(
                    running?.Title ?? "Preparação",
                    "Não foi possível criar o ambiente de desenvolvimento.",
                    null,
                    []);
                break;
        }

        IsDetailsOpen = false;
        State = DevelopmentPanelState.Failed;
    }

    private static void MarkFailed(DevelopmentStepViewModel? step, string? note)
    {
        if (step is null)
        {
            return;
        }

        step.State = StepVisualState.Failed;
        step.Note = note ?? step.Note;
    }

    private void ResetRun()
    {
        Steps.Clear();

        foreach (var step in RunSteps)
        {
            Steps.Add(new DevelopmentStepViewModel(step));
        }

        Failure = null;
        IsDetailsOpen = false;
        Plan = null;
        Conflict = null;
        Message = null;
    }

    // --- Detalhes, cópia e atalhos ----------------------------------------

    [RelayCommand]
    public void ToggleDetails() => IsDetailsOpen = !IsDetailsOpen;

    [RelayCommand]
    public async Task CopyAsync(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            await clipboard.WriteAsync(text);
            Message = "Copiado para a área de transferência.";
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "DevelopmentCopyFailed");
            Message = "Não foi possível copiar. Tente de novo.";
        }
    }

    [RelayCommand]
    public Task CopyPathAsync() => CopyAsync(Development?.WorktreePath);

    [RelayCommand]
    public Task CopyFailureDetailsAsync() => CopyAsync(Failure?.DetailsText);

    [RelayCommand]
    public async Task OpenInstallPageAsync()
    {
        if (!await shell.OpenUriAsync(Instructions.OfficialSite))
        {
            Message = $"Não foi possível abrir o navegador. O endereço é {Instructions.OfficialSite}";
        }
    }

    [RelayCommand]
    public async Task OpenFolderAsync()
    {
        if (Development is { } development && !await shell.OpenFolderAsync(development.WorktreePath))
        {
            Message = $"Não foi possível abrir {development.WorktreePath}. A pasta ainda existe?";
        }
    }

    [RelayCommand]
    public void OpenTerminal() => OpenTerminalAt(Development?.WorktreePath);

    /// <summary>Com a origem bloqueada por alterações locais, o terminal é no repositório.</summary>
    [RelayCommand]
    public void OpenRepositoryTerminal() => OpenTerminalAt(RepositoryPath);

    private void OpenTerminalAt(string? path)
    {
        if (path is not null && !shell.OpenTerminal(path))
        {
            Message = $"Não foi possível abrir um terminal em {path}.";
        }
    }

    // --- Remover -----------------------------------------------------------

    /// <summary>
    /// Pergunta ao Git se há alterações antes de perguntar ao usuário. Com
    /// alterações, não remove e mostra por quê — nem oferece "remover assim
    /// mesmo": perder trabalho não commitado não é uma opção desta tela.
    /// </summary>
    [RelayCommand]
    public async Task RemoveWorktreeAsync()
    {
        if (Development is not { } development)
        {
            return;
        }

        if (Commands.IsRunning)
        {
            Message = "Aguarde os comandos terminarem, ou cancele, antes de remover o worktree.";
            return;
        }

        RemoveChanges = null;
        IsShowingRemoveChanges = false;
        Message = null;
        IsRemoving = true;

        try
        {
            var inspection = await runner.RunAsync<InspectWorktreeHandler, WorktreeInspection>(
                (handler, token) => handler.HandleAsync(new InspectWorktree(_taskId), token),
                CancellationToken.None);

            if (!inspection.IsClean)
            {
                RemoveChanges = inspection.Changes;
                return;
            }

            var confirmed = await confirmation.AskAsync(new ConfirmationRequest(
                "Deseja remover este Worktree?",
                inspection.Exists
                    ? $"A pasta {development.WorktreePath} será apagada pelo Git. "
                      + $"A branch {development.Branch} continua no repositório."
                    : $"A pasta {development.WorktreePath} não existe mais. "
                      + "O repositório vai esquecer este worktree.",
                "Remover",
                IsIrreversible: true));

            if (!confirmed)
            {
                return;
            }

            Development = await runner.RunAsync<RemoveWorktreeHandler, TaskDevelopmentView>(
                (handler, token) => handler.HandleAsync(new RemoveWorktree(_taskId), token),
                CancellationToken.None);

            ShowPreviousAttempt(Development);

            // Pronto, a aba nem perguntou pelo Git; o formulário que volta precisa saber.
            State = DevelopmentPanelState.Loading;
            await DetectGitAsync(CancellationToken.None);

            if (State is DevelopmentPanelState.Setup && DirectoryText.Length > 0)
            {
                await InspectDirectoryAsync(CancellationToken.None);
            }

            Message = "Worktree removido.";
        }
        catch (DevelopmentStepException exception) when (exception.Changes.Count > 0)
        {
            RemoveChanges = exception.Changes;
        }
        catch (DomainException exception)
        {
            Message = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "WorktreeRemoveFailed {TaskId}", _taskId);
            Message = "Não foi possível remover o worktree.";
        }
        finally
        {
            IsRemoving = false;
        }
    }

    [RelayCommand]
    public void ShowRemoveChanges() => IsShowingRemoveChanges = true;

    [RelayCommand]
    public void CancelRemove()
    {
        RemoveChanges = null;
        IsShowingRemoveChanges = false;
    }

    /// <summary>
    /// Uma tentativa que não terminou pronta deixa o formulário preenchido com o
    /// que foi usado, e um recado do que aconteceu.
    /// </summary>
    private void ShowPreviousAttempt(TaskDevelopmentView? development)
    {
        if (development is null)
        {
            PreviousAttempt = null;
            return;
        }

        PreviousAttempt = development.Status switch
        {
            TaskDevelopmentStatus.Creating =>
                $"A criação anterior do worktree foi interrompida ({development.WorktreePath}). "
                + "Se a pasta ficou lá, \"Iniciar implementação\" oferece reaproveitá-la.",
            TaskDevelopmentStatus.Error =>
                $"A última tentativa falhou: {development.FailureReason}",
            TaskDevelopmentStatus.Removed =>
                $"O worktree {development.WorktreePath} foi removido. "
                + $"A branch {development.Branch} continua no repositório.",
            _ => null,
        };

        _preferredSource = development.SourceBranch;

        if (DirectoryText.Length == 0)
        {
            SetDirectory(development.RepositoryPath);
        }

        if (development.Status is TaskDevelopmentStatus.Creating or TaskDevelopmentStatus.Error
            && NewBranchName == GitBranchName.Suggest(_taskTitle))
        {
            NewBranchName = development.Branch;
        }
    }

    /// <summary>Troca o texto do campo sem esperar o atraso da digitação.</summary>
    private void SetDirectory(string path)
    {
        _settingDirectory = true;

        try
        {
            DirectoryText = path;
        }
        finally
        {
            _settingDirectory = false;
        }

        DirectoryExists = null;
        IsRepository = null;
        RepositoryPath = null;
    }

    /// <summary>
    /// <see cref="IProgress{T}"/> que avisa na hora, na thread de quem reporta.
    /// Ver o remarks da classe.
    /// </summary>
    private sealed class InlineProgress(Action<DevelopmentProgress> report) : IProgress<DevelopmentProgress>
    {
        public void Report(DevelopmentProgress value) => report(value);
    }
}
