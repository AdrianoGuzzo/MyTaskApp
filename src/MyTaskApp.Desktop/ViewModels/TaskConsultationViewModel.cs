using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Consultations;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>Uma consulta anterior, com a linha que a lista mostra.</summary>
public sealed record ConsultationSessionItem(ConsultationSessionView Session, string Text)
{
    public bool CanResume => Session.CanResume;
}

/// <summary>Uma pasta da lista "Projeto": escolhida ou não, e o que fazer quando não está neste computador.</summary>
public sealed partial class ConsultationSourceItemViewModel(
    TaskConsultationViewModel owner,
    ConsultationSourceOption option) : ObservableObject
{
    [ObservableProperty]
    private bool _isSelected = option.IsSelected;

    [ObservableProperty]
    private bool _isCloning;

    public ConsultationSourceOption Option { get; } = option;

    public string Name => Option.Name;

    /// <summary>O caminho, ou de onde ela viria.</summary>
    public string Detail => Option.Path ?? Option.RepositoryUrl ?? "Sem pasta neste computador";

    public bool IsReady => Option.IsReady;

    public bool IsWorktree => Option.Kind == ConsultationSourceKind.Worktree;

    public string KindText => IsWorktree ? "Ambiente da tarefa" : "Projeto da etiqueta";

    public string StateText => Option.State switch
    {
        ConsultationSourceState.Ready => "Pronto",
        ConsultationSourceState.FolderMissing => "Pasta não encontrada",
        ConsultationSourceState.NotCloned => "Não clonado neste computador",
        ConsultationSourceState.NotMapped => "Sem pasta neste computador",
        _ => "Removido",
    };

    public bool CanClone => Option.State == ConsultationSourceState.NotCloned;

    public bool CanPickFolder =>
        !IsWorktree && Option.State is ConsultationSourceState.NotMapped or ConsultationSourceState.FolderMissing;

    partial void OnIsSelectedChanged(bool value) => owner.OnSourceToggled();
}

/// <summary>
/// A aba Consulta Inteligente da janela da tarefa (ADR-081): assunto, projeto,
/// dados, preparo e pergunta, em cartões. Cada escolha é gravada na hora; a
/// pergunta abre o Claude Code num terminal, só lendo.
/// </summary>
/// <remarks>
/// Nada de processo nem de banco aqui: tudo passa por casos de uso. A cópia de
/// banco é a da aba Desenvolvimento (<see cref="DevelopmentDatabaseViewModel"/>),
/// com as mesmas confirmações — só o banco criado vai para a consulta.
/// </remarks>
public sealed partial class TaskConsultationViewModel : ObservableObject
{
    private readonly IUseCaseRunner _runner;
    private readonly IConfirmationDialog _confirmation;
    private readonly ILogger<TaskConsultationViewModel> _logger;

    /// <summary>Uma gravação por vez, sempre com o estado inteiro da tela.</summary>
    private readonly SemaphoreSlim _saving = new(1, 1);

    private Guid _taskId;

    /// <summary>Ligado enquanto a tela é preenchida pelo que veio do banco: nada disso é "o usuário mudou".</summary>
    private bool _applying;

    /// <summary>A carga mais recente: uma resposta atrasada de outra tarefa não sobrescreve a atual.</summary>
    private int _load;

    private string _savedInstructions = string.Empty;

    public TaskConsultationViewModel(
        IUseCaseRunner runner,
        IConfirmationDialog confirmation,
        DevelopmentDatabaseViewModel copy,
        ILogger<TaskConsultationViewModel> logger)
    {
        _runner = runner;
        _confirmation = confirmation;
        _logger = logger;
        Copy = copy;
        Copy.ManageRequested += () => DatabaseOperationsRequested?.Invoke();
        Copy.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(DevelopmentDatabaseViewModel.IsRunning) or nameof(DevelopmentDatabaseViewModel.IsSelecting))
            {
                OnPropertyChanged(nameof(CanEditDatabase));
                OnPropertyChanged(nameof(IsCopying));
            }
        };
    }

    /// <summary>"Criar nova cópia local…": o seletor e o progresso da cópia da aba Desenvolvimento.</summary>
    public DevelopmentDatabaseViewModel Copy { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsReadOnly), nameof(ActiveSession), nameof(IsSessionActive), nameof(StatusText),
        nameof(HasStatus), nameof(IsWaitingForYou), nameof(CanEditDatabase), nameof(ToolsAvailable), nameof(ShowsMcpHint),
        nameof(StartBlockedReason), nameof(CanStart), nameof(IsConfigured))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(GoToTerminalCommand), nameof(ClearCommand))]
    private TaskConsultationView? _view;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartBlockedReason), nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string? _message;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasNoProjects), nameof(HasTag))]
    private ConsultationTagOption? _selectedTag;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowsDatabase), nameof(ShowsMcpHint), nameof(StartBlockedReason), nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(CheckDatabaseCommand))]
    private bool _usesDatabase;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedDatabase), nameof(DatabaseServerText), nameof(DatabaseEnvironmentText))]
    [NotifyCanExecuteChangedFor(nameof(CheckDatabaseCommand), nameof(PrepareReadAccessCommand))]
    private ConsultationDatabaseOption? _selectedDatabase;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DatabaseCheckText), nameof(HasDatabaseCheck), nameof(DatabaseReadOnly), nameof(CanPrepareReadAccess))]
    [NotifyCanExecuteChangedFor(nameof(PrepareReadAccessCommand))]
    private ConsultationDatabaseCheck? _databaseCheck;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckDatabaseCommand), nameof(PrepareReadAccessCommand))]
    private bool _isCheckingDatabase;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckDatabaseCommand), nameof(PrepareReadAccessCommand))]
    private bool _isPreparingAccess;

    [ObservableProperty]
    private string _instructions = string.Empty;

    [ObservableProperty]
    private IReadOnlyList<AgentCliOption> _modelOptions = [];

    [ObservableProperty]
    private IReadOnlyList<AgentCliOption> _effortOptions = [];

    [ObservableProperty]
    private AgentCliOption? _selectedModel;

    [ObservableProperty]
    private AgentCliOption? _selectedEffort;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StartBlockedReason), nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private string _question = string.Empty;

    [ObservableProperty]
    private bool _isPreviewOpen;

    [ObservableProperty]
    private string? _preview;

    /// <summary>"Editar só para esta sessão": o texto abaixo substitui as instruções geradas no próximo "Iniciar".</summary>
    [ObservableProperty]
    private bool _isEditingInstructions;

    [ObservableProperty]
    private string _sessionInstructions = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsAgentMissing), nameof(InstallGuide), nameof(StartBlockedReason), nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private AgentCliStatus? _cli;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(HasStatus), nameof(StartBlockedReason), nameof(CanStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(GoToTerminalCommand), nameof(ResumeCommand))]
    private bool _isStarting;

    public ObservableCollection<ConsultationTagOption> Tags { get; } = [];

    public ObservableCollection<ConsultationSourceItemViewModel> Sources { get; } = [];

    public ObservableCollection<ConsultationDatabaseOption> Databases { get; } = [];

    public ObservableCollection<ConsultationSessionItem> Sessions { get; } = [];

    /// <summary>O que impede de abrir agora.</summary>
    public ObservableCollection<string> Problems { get; } = [];

    /// <summary>Avisos que não impedem: uma pasta escolhida que sumiu fica de fora.</summary>
    public ObservableCollection<string> Notes { get; } = [];

    /// <summary>
    /// Escolhe uma pasta no sistema; quem preenche é a janela, que tem o
    /// seletor. Sem ela (testes), "Escolher pasta…" não faz nada.
    /// </summary>
    public Func<string, Task<string?>>? PickFolder { get; set; }

    /// <summary>"Configurar em Etiquetas": a janela de etiquetas, para associar projetos.</summary>
    public event Action? TagsRequested;

    /// <summary>"Gerenciar em Bancos de Dados…".</summary>
    public event Action? DatabaseOperationsRequested;

    /// <summary>"Abrir servidor MCP": para ligar o servidor que as ferramentas de banco usam.</summary>
    public event Action? McpServerRequested;

    public bool IsReadOnly => View?.IsReadOnly ?? false;

    public bool IsConfigured => View?.IsConfigured ?? false;

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasMessage => !string.IsNullOrEmpty(Message);

    public bool HasTag => SelectedTag is not null;

    /// <summary>A etiqueta não tem projeto nenhum: a consulta não tem o que ler.</summary>
    public bool HasNoProjects => SelectedTag is { DirectoryCount: 0 } && Sources.All(source => !source.IsWorktree);

    public bool HasSources => Sources.Count > 0;

    public bool HasSessions => Sessions.Count > 0;

    public bool ShowsDatabase => UsesDatabase;

    public bool HasDatabases => Databases.Count > 0;

    public bool HasSelectedDatabase => SelectedDatabase is not null;

    public string? DatabaseServerText => SelectedDatabase is { } database
        ? $"PostgreSQL · {database.Server} · conexão {database.ConnectionName}"
        : null;

    public string? DatabaseEnvironmentText => SelectedDatabase is { } database
        ? $"Ambiente: {EnvironmentName(database.Environment)}"
        : null;

    public bool HasDatabaseCheck => DatabaseCheck is not null;

    public string? DatabaseCheckText => DatabaseCheck?.Message;

    public bool DatabaseReadOnly => DatabaseCheck is { IsReadOnly: true };

    public bool CanPrepareReadAccess => DatabaseCheck is { IsReadOnly: false, CanPrepareReadAccess: true };

    public bool ToolsAvailable => View?.ToolsAvailable ?? false;

    /// <summary>Banco escolhido com o MCP desligado: as ferramentas de banco não existem.</summary>
    public bool ShowsMcpHint => UsesDatabase && View is not null && !ToolsAvailable;

    /// <summary>O banco de uma sessão aberta não troca: é o que ela recebeu nas instruções.</summary>
    public bool CanEditDatabase => !IsReadOnly && !IsSessionActive && !Copy.IsRunning;

    public bool IsCopying => Copy.IsSelecting || Copy.IsRunning;

    public AgentSessionView? ActiveSession => View?.ActiveSession is { IsActive: true } active ? active : null;

    public bool IsSessionActive => ActiveSession is not null;

    public bool IsWaitingForYou => ActiveSession is { Activity: AgentActivity.WaitingForUser or AgentActivity.WaitingReview };

    public bool IsAgentMissing => Cli is { Detection.IsInstalled: false };

    public AgentCliInstallGuide? InstallGuide => Cli?.InstallGuide;

    public bool HasStatus => StatusText is not null;

    /// <summary>A linha de estado do cartão "Perguntar".</summary>
    public string? StatusText
    {
        get
        {
            if (IsStarting)
            {
                return "Iniciando o Claude Code…";
            }

            if (ActiveSession is { } active)
            {
                return active.Activity switch
                {
                    AgentActivity.WaitingForUser => "Aguardando você no terminal.",
                    AgentActivity.WaitingReview => "Resposta pronta no terminal.",
                    AgentActivity.Failed => "A resposta falhou no terminal.",
                    _ => "Sessão ativa — continue a conversa no terminal.",
                };
            }

            return View?.Sessions.FirstOrDefault() switch
            {
                { Status: AgentSessionStatus.Failed, FailureReason: var reason } => $"Não foi possível iniciar o Claude Code: {reason}",
                { Status: AgentSessionStatus.Exited } => "Terminal encerrado. Use Reabrir para voltar à conversa.",
                _ => null,
            };
        }
    }

    /// <summary>Por que "Iniciar consulta" está desligado — à vista, e não só o botão cinza.</summary>
    public string? StartBlockedReason
    {
        get
        {
            if (View is null || IsLoading)
            {
                return null;
            }

            if (IsSessionActive)
            {
                return "Já há uma consulta aberta: continue no terminal.";
            }

            if (View.Problems.Count > 0)
            {
                return View.Problems[0];
            }

            if (IsAgentMissing)
            {
                return "O Claude Code não foi encontrado neste computador.";
            }

            if (UsesDatabase && !ToolsAvailable)
            {
                return "Para consultar o banco, o servidor MCP do MyTaskApp precisa estar ligado.";
            }

            return string.IsNullOrWhiteSpace(Question) ? "Escreva a pergunta." : null;
        }
    }

    public bool CanStart => !IsStarting && !IsReadOnly && View is not null && StartBlockedReason is null;

    /// <summary>A janela trocou de tarefa (ou abriu): a aba esquece a anterior e carrega na próxima vez que aparecer.</summary>
    public void Load(Guid taskId)
    {
        _taskId = taskId;
        _load++;
        _applying = true;

        try
        {
            View = null;
            ErrorMessage = null;
            Message = null;
            DatabaseCheck = null;
            Question = string.Empty;
            Instructions = string.Empty;
            _savedInstructions = string.Empty;
            Preview = null;
            IsPreviewOpen = false;
            IsEditingInstructions = false;
            SessionInstructions = string.Empty;
            Tags.Clear();
            Sources.Clear();
            Databases.Clear();
            Sessions.Clear();
            Problems.Clear();
            Notes.Clear();
        }
        finally
        {
            _applying = false;
        }
    }

    /// <summary>A aba apareceu: tudo de novo — pastas, bancos e sessões podem ter mudado em outra janela.</summary>
    public Task ActivateAsync(CancellationToken cancellationToken) => ReloadAsync(clearMessages: true, cancellationToken);

    /// <summary>
    /// Lê a aba de novo. Depois de uma ação (iniciar, reabrir, ir ao terminal),
    /// sem apagar o que ela acabou de dizer.
    /// </summary>
    private async Task ReloadAsync(bool clearMessages, CancellationToken cancellationToken)
    {
        var load = ++_load;
        var taskId = _taskId;
        IsLoading = true;

        if (clearMessages)
        {
            ErrorMessage = null;
        }

        try
        {
            var view = await _runner.RunAsync<GetTaskConsultationHandler, TaskConsultationView>(
                (handler, token) => handler.HandleAsync(new GetTaskConsultation(taskId), token),
                cancellationToken);

            Cli ??= await _runner.RunAsync<DetectAgentCliHandler, AgentCliStatus>(
                (handler, token) => handler.HandleAsync(new DetectAgentCli(), token),
                cancellationToken);

            if (load != _load)
            {
                return;
            }

            Apply(view);

            if (UsesDatabase && SelectedDatabase is not null && DatabaseCheck is null)
            {
                _ = CheckDatabaseAsync();
            }
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
            _logger.LogError(exception, "TaskConsultationLoadFailed {TaskId}", taskId);
            ErrorMessage = "Não foi possível carregar a Consulta Inteligente desta tarefa.";
        }
        finally
        {
            if (load == _load)
            {
                IsLoading = false;
            }
        }
    }

    /// <summary>Uma sessão abriu ou fechou (o monitor avisou): só a linha de estado e o histórico.</summary>
    public Task RefreshSessionsAsync() =>
        View is null ? Task.CompletedTask : ReloadAsync(clearMessages: false, CancellationToken.None);

    [RelayCommand]
    public Task RefreshAsync() => ActivateAsync(CancellationToken.None);

    // --- Escolhas, gravadas a cada mudança -----------------------------------

    partial void OnSelectedTagChanged(ConsultationTagOption? value)
    {
        if (!_applying)
        {
            // Trocar de assunto troca os projetos: as pastas prontas da etiqueta nova.
            _ = SaveAsync(chooseSources: true);
        }
    }

    internal void OnSourceToggled()
    {
        if (!_applying)
        {
            _ = SaveAsync(chooseSources: false);
        }
    }

    partial void OnUsesDatabaseChanged(bool value)
    {
        if (_applying)
        {
            return;
        }

        DatabaseCheck = null;

        if (value && SelectedDatabase is null)
        {
            // Ligar sem banco escolhido escolhe o primeiro da lista; sem lista, só mostra como criar uma cópia.
            _applying = true;
            SelectedDatabase = Databases.FirstOrDefault();
            _applying = false;
        }

        _ = SaveAsync(chooseSources: false, checkDatabase: value);
    }

    partial void OnSelectedDatabaseChanged(ConsultationDatabaseOption? value)
    {
        if (!_applying)
        {
            DatabaseCheck = null;
            _ = SaveAsync(chooseSources: false, checkDatabase: true);
        }
    }

    partial void OnSelectedModelChanged(AgentCliOption? value)
    {
        if (!_applying)
        {
            _ = SaveAsync(chooseSources: false);
        }
    }

    partial void OnSelectedEffortChanged(AgentCliOption? value)
    {
        if (!_applying)
        {
            _ = SaveAsync(chooseSources: false);
        }
    }

    /// <summary>As instruções gravam ao sair da caixa, e não a cada tecla.</summary>
    [RelayCommand]
    public async Task CommitInstructionsAsync()
    {
        if (!string.Equals(Instructions.Trim(), _savedInstructions, StringComparison.Ordinal))
        {
            await SaveAsync(chooseSources: false);
        }
    }

    private async Task SaveAsync(bool chooseSources, bool checkDatabase = false)
    {
        if (View is null || IsReadOnly)
        {
            return;
        }

        var taskId = _taskId;
        var load = _load;

        await _saving.WaitAsync();

        try
        {
            ErrorMessage = null;

            var command = new SaveTaskConsultation(
                taskId,
                SelectedTag?.Id,
                chooseSources ? null : Sources.Where(source => source.IsSelected).Select(source => source.Option.Reference).ToList(),
                UsesDatabase ? SelectedDatabase?.ConnectionId : null,
                UsesDatabase ? SelectedDatabase?.DatabaseName : null,
                Instructions,
                SelectedModel?.Value,
                SelectedEffort?.Value);

            var view = await _runner.RunAsync<SaveTaskConsultationHandler, TaskConsultationView>(
                (handler, token) => handler.HandleAsync(command, token),
                CancellationToken.None);

            if (load == _load)
            {
                Apply(view);
            }
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;

            // A tela volta ao que está gravado: a escolha recusada não fica parecendo aceita.
            await ReloadAsync(clearMessages: false, CancellationToken.None);
            return;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "TaskConsultationSaveFailed {TaskId}", taskId);
            ErrorMessage = "Não foi possível gravar a configuração da consulta.";
            return;
        }
        finally
        {
            _saving.Release();
        }

        if (IsPreviewOpen)
        {
            await LoadPreviewAsync();
        }

        if (checkDatabase && UsesDatabase && SelectedDatabase is not null)
        {
            await CheckDatabaseAsync();
        }
    }

    private void Apply(TaskConsultationView view)
    {
        _applying = true;

        try
        {
            View = view;

            Replace(Tags, view.Tags);
            SelectedTag = Tags.FirstOrDefault(tag => tag.Id == view.TagId);

            Sources.Clear();

            foreach (var source in view.Sources)
            {
                Sources.Add(new ConsultationSourceItemViewModel(this, source));
            }

            var databases = view.Databases.ToList();

            // O banco escolhido continua à vista mesmo fora da lista (excluído, desativado): é o problema a mostrar.
            if (view.Database is { } chosen && databases.All(option => !option.Matches(chosen.ConnectionId, chosen.DatabaseName)))
            {
                databases.Insert(0, new ConsultationDatabaseOption(
                    chosen.ConnectionId,
                    chosen.DatabaseName ?? string.Empty,
                    chosen.Label,
                    chosen.ConnectionName ?? "—",
                    chosen.Server ?? "—",
                    chosen.Environment ?? Domain.DatabaseOperations.DatabaseEnvironment.Development,
                    ConsultationDatabaseOrigin.Connection,
                    chosen.IsReadAccess));
            }

            Replace(Databases, databases);

            // Banco ligado sem nenhum escolhido (lista vazia) fica ligado: é onde a tela mostra como criar uma cópia.
            UsesDatabase = view.Database is not null || (UsesDatabase && SelectedDatabase is null && Databases.Count == 0);
            SelectedDatabase = view.Database is { } selection
                ? Databases.FirstOrDefault(option => option.Matches(selection.ConnectionId, selection.DatabaseName))
                : null;

            // Texto ainda não gravado (a caixa com o foco) não é atropelado por uma recarga.
            var editing = !string.Equals(Instructions.Trim(), _savedInstructions, StringComparison.Ordinal);
            _savedInstructions = view.Instructions ?? string.Empty;

            if (!editing)
            {
                Instructions = _savedInstructions;
            }

            ModelOptions = [AgentCliOption.Default("Padrão"), .. view.Models];
            EffortOptions = [AgentCliOption.Default("Padrão"), .. view.Efforts];
            SelectedModel = AgentCliOption.Find(ModelOptions, view.Model ?? string.Empty) ?? ModelOptions[0];
            SelectedEffort = AgentCliOption.Find(EffortOptions, view.Effort ?? string.Empty) ?? EffortOptions[0];

            if (string.IsNullOrWhiteSpace(Question) && view.LastQuestion is { } last)
            {
                Question = last;
            }

            Replace(Sessions, view.Sessions.Select(session => new ConsultationSessionItem(session, Describe(session))));
            Replace(Problems, view.Problems);
            Replace(Notes, view.Notes);
        }
        finally
        {
            _applying = false;
        }

        OnPropertyChanged(nameof(HasNoProjects));
        OnPropertyChanged(nameof(HasSources));
        OnPropertyChanged(nameof(HasSessions));
        OnPropertyChanged(nameof(HasDatabases));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(HasStatus));
        OnPropertyChanged(nameof(StartBlockedReason));
        OnPropertyChanged(nameof(CanStart));
        StartCommand.NotifyCanExecuteChanged();
        ResumeCommand.NotifyCanExecuteChanged();
    }

    // --- Projeto: clonar e escolher pasta --------------------------------------

    [RelayCommand]
    public void ManageTags() => TagsRequested?.Invoke();

    /// <summary>"Clonar…": sempre com confirmação, mostrando de onde e para onde.</summary>
    [RelayCommand]
    public async Task CloneAsync(ConsultationSourceItemViewModel item)
    {
        ErrorMessage = null;
        Message = null;

        try
        {
            var target = await _runner.RunAsync<CloneConsultationRepositoryHandler, string>(
                (handler, token) => handler.TargetAsync(_taskId, item.Option.ReferenceId, token),
                CancellationToken.None);

            var confirmed = await _confirmation.AskAsync(new ConfirmationRequest(
                "Clonar o repositório?",
                $"O repositório {item.Option.RepositoryUrl} será clonado em {target}, e essa pasta passa a ser a de "
                + $"{item.Name} neste computador. Nada é alterado no repositório.",
                "Clonar"));

            if (!confirmed)
            {
                return;
            }

            item.IsCloning = true;

            var result = await _runner.RunAsync<CloneConsultationRepositoryHandler, ConsultationCloneResult>(
                (handler, token) => handler.HandleAsync(new CloneConsultationRepository(_taskId, item.Option.ReferenceId), token),
                CancellationToken.None);

            Apply(result.View);
            Message = result.AlreadyCloned
                ? $"A pasta {result.Path} já era um clone deste repositório."
                : $"Repositório clonado em {result.Path}.";
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "TaskConsultationCloneFailed {TaskId}", _taskId);
            ErrorMessage = "Não foi possível clonar o repositório.";
        }
        finally
        {
            item.IsCloning = false;
        }
    }

    [RelayCommand]
    public async Task PickFolderAsync(ConsultationSourceItemViewModel item)
    {
        if (PickFolder is null || await PickFolder($"Escolher a pasta de {item.Name}") is not { } path)
        {
            return;
        }

        ErrorMessage = null;

        try
        {
            Apply(await _runner.RunAsync<SetConsultationFolderHandler, TaskConsultationView>(
                (handler, token) => handler.HandleAsync(new SetConsultationFolder(_taskId, item.Option.ReferenceId, path), token),
                CancellationToken.None));
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "TaskConsultationFolderFailed {TaskId}", _taskId);
            ErrorMessage = "Não foi possível gravar a pasta.";
        }
    }

    // --- Dados ------------------------------------------------------------------

    private bool CanCheckDatabase() => UsesDatabase && SelectedDatabase is not null && !IsCheckingDatabase && !IsPreparingAccess;

    [RelayCommand(CanExecute = nameof(CanCheckDatabase))]
    public async Task CheckDatabaseAsync()
    {
        var taskId = _taskId;
        IsCheckingDatabase = true;

        try
        {
            var check = await _runner.RunAsync<CheckConsultationDatabaseHandler, ConsultationDatabaseCheck>(
                (handler, token) => handler.HandleAsync(new CheckConsultationDatabase(taskId), token),
                CancellationToken.None);

            if (taskId == _taskId)
            {
                DatabaseCheck = check;
            }
        }
        catch (DomainException exception)
        {
            DatabaseCheck = new ConsultationDatabaseCheck(false, null, null, false, [], false, exception.Message);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "TaskConsultationCheckFailed {TaskId}", taskId);
            DatabaseCheck = new ConsultationDatabaseCheck(false, null, null, false, [], false, "Não foi possível conferir o banco.");
        }
        finally
        {
            IsCheckingDatabase = false;
        }
    }

    private bool CanPrepare() => CanPrepareReadAccess && !IsCheckingDatabase && !IsPreparingAccess;

    /// <summary>
    /// Cria (ou acerta) o usuário de leitura no servidor. Altera o servidor,
    /// então só depois de dizer exatamente o quê.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanPrepare))]
    public async Task PrepareReadAccessAsync()
    {
        if (SelectedDatabase is not { } database)
        {
            return;
        }

        var confirmed = await _confirmation.AskAsync(new ConfirmationRequest(
            "Preparar acesso de leitura?",
            $"No servidor {database.Server}, o MyTaskApp vai criar (ou acertar) o usuário "
            + $"{IConsultationReadAccessProvisioner.RoleName}, que só consegue entrar e ler, e dar a ele leitura no banco "
            + $"{database.DatabaseName}. A senha dele é gerada agora e fica guardada no cofre do sistema, numa conexão "
            + "nova \"(leitura)\". Nada mais muda no servidor, e a operação fica no histórico de Bancos de Dados.",
            "Preparar"));

        if (!confirmed)
        {
            return;
        }

        var taskId = _taskId;
        IsPreparingAccess = true;
        ErrorMessage = null;

        try
        {
            DatabaseCheck = await _runner.RunAsync<PrepareConsultationReadAccessHandler, ConsultationDatabaseCheck>(
                (handler, token) => handler.HandleAsync(new PrepareConsultationReadAccess(taskId), token),
                CancellationToken.None);
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "TaskConsultationPrepareFailed {TaskId}", taskId);
            ErrorMessage = "Não foi possível preparar o acesso de leitura.";
        }
        finally
        {
            IsPreparingAccess = false;
        }
    }

    [RelayCommand]
    public async Task BeginCopyAsync()
    {
        Copy.Show(_taskId, null);
        Copy.BeginSelecting();
        await Copy.LoadCatalogAsync(CancellationToken.None);
    }

    /// <summary>Valida, confirma e copia — e só então o banco novo vira o da consulta.</summary>
    [RelayCommand]
    public async Task CopyNowAsync()
    {
        if (await Copy.PrepareAsync(_taskId) is not { } plan)
        {
            return;
        }

        if (await Copy.RunForConsultationAsync(_taskId, plan) is { } view)
        {
            Apply(view);
            await CheckDatabaseAsync();
        }
    }

    [RelayCommand]
    public void OpenMcpServer() => McpServerRequested?.Invoke();

    // --- Preparar ---------------------------------------------------------------

    partial void OnIsPreviewOpenChanged(bool value)
    {
        if (value)
        {
            _ = LoadPreviewAsync();
        }
    }

    private async Task LoadPreviewAsync()
    {
        var taskId = _taskId;

        try
        {
            var text = await _runner.RunAsync<PreviewConsultationContextHandler, string>(
                (handler, token) => handler.HandleAsync(new PreviewConsultationContext(taskId), token),
                CancellationToken.None);

            if (taskId == _taskId)
            {
                Preview = text;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogWarning(exception, "TaskConsultationPreviewFailed {TaskId}", taskId);
            Preview = "Não foi possível montar a prévia das instruções.";
        }
    }

    [RelayCommand]
    public void EditForSession()
    {
        SessionInstructions = Preview ?? string.Empty;
        IsEditingInstructions = true;
    }

    [RelayCommand]
    public void DiscardSessionEdit()
    {
        IsEditingInstructions = false;
        SessionInstructions = string.Empty;
    }

    // --- Perguntar --------------------------------------------------------------

    [RelayCommand(CanExecute = nameof(CanStart))]
    public async Task StartAsync()
    {
        var taskId = _taskId;
        await CommitInstructionsAsync();

        IsStarting = true;
        ErrorMessage = null;
        Message = null;

        try
        {
            var session = await _runner.RunAsync<StartTaskConsultationHandler, AgentSessionView>(
                (handler, token) => handler.HandleAsync(
                    new StartTaskConsultation(taskId, Question, IsEditingInstructions ? SessionInstructions : null),
                    token),
                CancellationToken.None);

            if (session.Status == AgentSessionStatus.Failed)
            {
                ErrorMessage = $"Não foi possível iniciar o Claude Code: {session.FailureReason}";
            }
            else
            {
                IsEditingInstructions = false;
                Message = "Consulta aberta num terminal. A conversa continua lá.";
            }
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "TaskConsultationStartFailed {TaskId}", taskId);
            ErrorMessage = "Não foi possível iniciar a consulta.";
        }
        finally
        {
            IsStarting = false;
        }

        await ReloadAsync(clearMessages: false, CancellationToken.None);
    }

    private bool CanGoToTerminal() => IsSessionActive && !IsStarting;

    [RelayCommand(CanExecute = nameof(CanGoToTerminal))]
    public async Task GoToTerminalAsync()
    {
        if (ActiveSession is not { } session)
        {
            return;
        }

        ErrorMessage = null;

        try
        {
            var result = await _runner.RunAsync<FocusAgentSessionHandler, AgentFocusResult>(
                (handler, token) => handler.HandleAsync(new FocusAgentSession(_taskId, SessionId: session.SessionId), token),
                CancellationToken.None);

            if (!result.Focused)
            {
                ErrorMessage = result.Session.IsActive
                    ? "Não foi possível localizar a janela do terminal. Procure-a na barra de tarefas."
                    : "Esta consulta já foi encerrada.";
            }
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "TaskConsultationFocusFailed {TaskId}", _taskId);
            ErrorMessage = "Não foi possível trazer o terminal para a frente.";
        }

        await ReloadAsync(clearMessages: false, CancellationToken.None);
    }

    private bool CanResume(ConsultationSessionItem? item) =>
        item is { Session.CanResume: true } && !IsSessionActive && !IsStarting && !IsReadOnly;

    [RelayCommand(CanExecute = nameof(CanResume))]
    public async Task ResumeAsync(ConsultationSessionItem? item)
    {
        if (item?.Session is not { } session)
        {
            return;
        }

        var taskId = _taskId;
        IsStarting = true;
        ErrorMessage = null;
        Message = null;

        try
        {
            var reopened = await _runner.RunAsync<ResumeTaskConsultationHandler, AgentSessionView>(
                (handler, token) => handler.HandleAsync(new ResumeTaskConsultation(taskId, session.SessionId), token),
                CancellationToken.None);

            if (reopened.Status == AgentSessionStatus.Failed)
            {
                ErrorMessage = $"Não foi possível reabrir: {reopened.FailureReason}";
            }
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "TaskConsultationResumeFailed {TaskId}", taskId);
            ErrorMessage = "Não foi possível reabrir a consulta.";
        }
        finally
        {
            IsStarting = false;
        }

        await ReloadAsync(clearMessages: false, CancellationToken.None);
    }

    private bool CanClear() => IsConfigured && !IsReadOnly && !IsSessionActive;

    /// <summary>Esquece só a configuração: etiquetas, pastas, bancos e conexões ficam.</summary>
    [RelayCommand(CanExecute = nameof(CanClear))]
    public async Task ClearAsync()
    {
        var confirmed = await _confirmation.AskAsync(new ConfirmationRequest(
            "Limpar a configuração da consulta?",
            "A etiqueta, as pastas, o banco e as instruções escolhidos aqui são esquecidos. Repositórios, bancos, cópias "
            + "e conexões continuam onde estão, e as consultas anteriores seguem no histórico.",
            "Limpar"));

        if (!confirmed)
        {
            return;
        }

        try
        {
            DatabaseCheck = null;
            Apply(await _runner.RunAsync<ClearTaskConsultationHandler, TaskConsultationView>(
                (handler, token) => handler.HandleAsync(new ClearTaskConsultation(_taskId), token),
                CancellationToken.None));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "TaskConsultationClearFailed {TaskId}", _taskId);
            ErrorMessage = exception is DomainException ? exception.Message : "Não foi possível limpar a configuração.";
        }
    }

    /// <summary>"23/10 14:05 · encerrada" — uma linha do histórico.</summary>
    public static string Describe(ConsultationSessionView session)
    {
        var when = session.StartedAt.ToLocalTime().ToString("dd/MM HH:mm", CultureInfo.GetCultureInfo("pt-BR"));

        return session.Status switch
        {
            AgentSessionStatus.Starting or AgentSessionStatus.Running => $"{when} · aberta",
            AgentSessionStatus.Failed => $"{when} · não abriu",
            _ => $"{when} · encerrada",
        };
    }

    private static string EnvironmentName(Domain.DatabaseOperations.DatabaseEnvironment environment) => environment switch
    {
        Domain.DatabaseOperations.DatabaseEnvironment.Development => "Desenvolvimento",
        Domain.DatabaseOperations.DatabaseEnvironment.Test => "Teste",
        Domain.DatabaseOperations.DatabaseEnvironment.Staging => "Homologação",
        Domain.DatabaseOperations.DatabaseEnvironment.Production => "Produção",
        _ => "Produção crítica",
    };

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();

        foreach (var item in items)
        {
            target.Add(item);
        }
    }
}
