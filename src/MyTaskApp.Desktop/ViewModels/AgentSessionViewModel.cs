using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>O que o card do agente está mostrando.</summary>
public enum AgentPanelState
{
    /// <summary>Perguntando ao banco e ao sistema.</summary>
    Checking,

    /// <summary>O agente não está instalado: só as instruções.</summary>
    NotInstalled,

    /// <summary>Instalado, e nenhuma sessão ainda.</summary>
    Idle,

    /// <summary>"Iniciar" em andamento.</summary>
    Starting,

    /// <summary>Há um terminal aberto com o agente para esta tarefa.</summary>
    Running,

    /// <summary>A última sessão terminou.</summary>
    Finished,

    /// <summary>O terminal da última tentativa não abriu.</summary>
    Failed,
}

/// <summary>
/// O card do agente de IA na aba Desenvolvimento (ADR-030): se ele está
/// instalado, se há um terminal aberto para esta tarefa, e o botão que leva até
/// esse terminal.
/// </summary>
/// <remarks>
/// Filho do <see cref="TaskDevelopmentViewModel"/>, e não parte dele: aquele já
/// cuida do worktree, e o agente só existe depois que o worktree está pronto.
/// Nada de processo aqui — tudo passa por casos de uso.
/// </remarks>
public sealed partial class AgentSessionViewModel(
    IUseCaseRunner runner,
    IClipboardWriter clipboard,
    IShellLauncher shell,
    ILogger<AgentSessionViewModel> logger) : ObservableObject
{
    private Guid _taskId;

    private Guid _developmentId;

    /// <summary>O último valor vindo do banco: se o campo ainda o mostra, o usuário não mexeu.</summary>
    private string _loadedArguments = string.Empty;

    /// <summary>Como <see cref="_loadedArguments"/>, para a caixa do acompanhamento.</summary>
    private bool _loadedMonitor = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsChecking), nameof(IsNotInstalled), nameof(IsIdle), nameof(IsStarting), nameof(IsRunning),
        nameof(IsFinished), nameof(IsFailed), nameof(HasSession), nameof(ShowStart), nameof(StatusText),
        nameof(NeedsAttention), nameof(ActivityText), nameof(MonitoringText), nameof(IsRunningQuietly), nameof(ShowsWarning))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(FocusCommand))]
    private AgentPanelState _state = AgentPanelState.Checking;

    /// <summary>Tarefa concluída não abre agente novo; o que está aberto continua alcançável.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowStart))]
    [NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _isReadOnly;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(AgentName), nameof(StartLabel), nameof(NotFoundText), nameof(VersionText),
        nameof(ExecutablePath), nameof(InstallGuide), nameof(HasInstallGuide), nameof(StatusText),
        nameof(CommandPreview))]
    private AgentCliStatus? _cli;

    /// <summary>
    /// O campo "Parâmetros": vem com o padrão salvo e, ao iniciar, o que estiver
    /// nele vira o novo padrão.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CommandPreview))]
    private string _arguments = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(AgentName), nameof(StartLabel), nameof(ProcessText), nameof(StartedText), nameof(RangeText),
        nameof(WorkingDirectory), nameof(FailureReason), nameof(HasSession), nameof(StatusText),
        nameof(NeedsAttention), nameof(ActivityText), nameof(MonitoringText), nameof(IsRunningQuietly), nameof(ShowsWarning))]
    private AgentSessionView? _session;

    /// <summary>
    /// "Avisar quando precisar de mim": abre o agente com os hooks do app
    /// (ADR-037). Como os parâmetros, o valor usado ao iniciar vira o padrão.
    /// </summary>
    [ObservableProperty]
    private bool _monitor = true;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(FocusCommand))]
    private bool _isBusy;

    /// <summary>Um recado curto: "Não foi possível localizar a janela…".</summary>
    [ObservableProperty]
    private string? _message;

    /// <summary>
    /// O texto livre com que o agente abre. Gravado no ambiente ao iniciar, e
    /// de volta aqui ao reabrir a tarefa.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPrompt))]
    private string? _prompt;

    /// <summary>
    /// Com texto: executar direto em vez de só planejar. Não é gravado — cada
    /// ambiente abre desmarcado, e o padrão é o agente pedir aprovação.
    /// </summary>
    [ObservableProperty]
    private bool _runDirectly;

    public bool HasPrompt => !string.IsNullOrWhiteSpace(Prompt);

    public int MaxPromptLength => TaskDevelopment.MaxAgentPromptLength;

    public string AgentName => Cli?.Name ?? Session?.ProviderName ?? "Agente de IA";

    public string StartLabel => $"Iniciar {AgentName}";

    public string NotFoundText => $"{AgentName} não encontrado.";

    public string? VersionText => Cli?.Detection.Version is { } version ? $"Versão {version}" : null;

    public string? ExecutablePath => Cli?.Detection.ExecutablePath;

    /// <summary>"Roda: claude --dangerously-skip-permissions".</summary>
    public string CommandPreview => $"Roda: {$"{Cli?.Command ?? "claude"} {Arguments}".Trim()}";

    public AgentCliInstallGuide? InstallGuide => Cli?.InstallGuide;

    public bool HasInstallGuide => InstallGuide is not null;

    public string? ProcessText => Session?.ProcessId is { } processId ? $"PID {processId}" : null;

    public string? WorkingDirectory => Session?.WorkingDirectory;

    public string? FailureReason => Session?.FailureReason;

    public string? StartedText => Session is null ? null : $"Iniciado às {Clock(Session.StartedAt)}";

    /// <summary>"18:42 → 19:17", para a sessão encerrada.</summary>
    public string? RangeText => Session is { EndedAt: { } ended } finished
        ? $"{Clock(finished.StartedAt)} → {Clock(ended)}"
        : null;

    public string StatusText => State switch
    {
        AgentPanelState.Checking => $"Verificando o {AgentName}…",
        AgentPanelState.Starting => $"Abrindo o {AgentName}…",
        AgentPanelState.Running => RunningText(Session?.Activity ?? AgentActivity.Unknown),
        AgentPanelState.Finished => "○ Finalizado",
        AgentPanelState.Failed => "✗ O terminal não abriu",
        AgentPanelState.NotInstalled => NotFoundText,
        _ => "Nenhuma sessão ativa.",
    };

    /// <summary>"● Trabalhando", "⚠ Aguardando você"… — o que os hooks disseram por último (ADR-037).</summary>
    public static string RunningText(AgentActivity activity) => activity switch
    {
        AgentActivity.Working => "● Trabalhando",
        AgentActivity.WaitingForUser => "⚠ Aguardando você",
        AgentActivity.WaitingReview => "✓ Terminou — aguardando sua revisão",
        AgentActivity.Failed => "✗ A última resposta terminou em erro",
        _ => "● Em execução",
    };

    /// <summary>Aberto e parado esperando o usuário: o status ganha a cor de atenção.</summary>
    public bool NeedsAttention =>
        State is AgentPanelState.Running
        && Session?.Activity is AgentActivity.WaitingForUser or AgentActivity.WaitingReview or AgentActivity.Failed;

    /// <summary>Verde só quando está aberto e ninguém precisa fazer nada.</summary>
    public bool IsRunningQuietly => IsRunning && !NeedsAttention;

    /// <summary>Âmbar: o terminal não abriu, ou o agente está esperando.</summary>
    public bool ShowsWarning => IsFailed || NeedsAttention;

    /// <summary>"Às 18:55 · Preciso saber se uso Redis ou MemoryCache."</summary>
    public string? ActivityText => State is AgentPanelState.Running && Session?.ActivityChangedAt is { } at
        ? AgentAlertViewModel.Excerpt(Session.ActivityMessage) is { } message
            ? $"Às {Clock(at)} · {message}"
            : $"Desde {Clock(at)}"
        : null;

    /// <summary>Com o agente aberto: se ele avisa o app, ou por que não.</summary>
    public string? MonitoringText => State is AgentPanelState.Running && Session is { } session
        ? session.IsMonitored
            ? "O MyTaskApp avisa quando o agente precisar de você ou terminar."
            : "Sem acompanhamento: esta sessão não avisa o MyTaskApp."
        : null;

    public bool IsChecking => State is AgentPanelState.Checking;

    public bool IsNotInstalled => State is AgentPanelState.NotInstalled;

    public bool IsIdle => State is AgentPanelState.Idle;

    public bool IsStarting => State is AgentPanelState.Starting;

    public bool IsRunning => State is AgentPanelState.Running;

    public bool IsFinished => State is AgentPanelState.Finished;

    public bool IsFailed => State is AgentPanelState.Failed;

    public bool HasSession => Session is not null && State is AgentPanelState.Running or AgentPanelState.Finished;

    /// <summary>"Iniciar" aparece sem sessão, e também depois dela: é o "iniciar novamente".</summary>
    public bool ShowStart => !IsReadOnly && State is AgentPanelState.Idle or AgentPanelState.Finished or AgentPanelState.Failed;

    public bool CanStart => !IsBusy && ShowStart;

    public bool CanFocus => !IsBusy && State is AgentPanelState.Running;

    /// <summary>
    /// O ambiente do card (ADR-031): cada repositório da tarefa tem o seu agente.
    /// Chamado quando o ambiente passa a existir. O texto só é reposto ao trocar
    /// de ambiente: no mesmo, o que o usuário está digitando fica.
    /// </summary>
    public void Load(Guid taskId, Guid developmentId, bool isReadOnly, string? prompt = null)
    {
        if (_taskId == taskId && _developmentId == developmentId)
        {
            IsReadOnly = isReadOnly;
            return;
        }

        _taskId = taskId;
        _developmentId = developmentId;
        Session = null;
        IsReadOnly = isReadOnly;
        Prompt = prompt;
        RunDirectly = false;
        State = AgentPanelState.Checking;
    }

    /// <summary>
    /// O que está gravado para a tarefa, já conferido contra o sistema — e, sem
    /// sessão em andamento, se o agente está instalado.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_developmentId == Guid.Empty || State is AgentPanelState.Starting)
        {
            return;
        }

        try
        {
            var session = await runner.RunAsync<GetTaskAgentSessionHandler, AgentSessionView?>(
                (handler, token) => handler.HandleAsync(new GetTaskAgentSession(_taskId, _developmentId), token),
                cancellationToken);

            Session = session;

            // Com o agente aberto, ele está instalado — não há o que procurar.
            if (session is not { IsActive: true })
            {
                await DetectAsync(cancellationToken);
            }

            State = StateFor(session);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "AgentSessionLoadFailed {TaskId}", _taskId);
            Message = "Não foi possível verificar o agente de IA desta tarefa.";
            State = Session is null ? AgentPanelState.Idle : StateFor(Session);
        }
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    public async Task StartAsync()
    {
        IsBusy = true;
        Message = null;
        var previous = State;
        State = AgentPanelState.Starting;

        try
        {
            Session = await runner.RunAsync<StartAgentSessionHandler, AgentSessionView>(
                (handler, token) => handler.HandleAsync(
                    new StartAgentSession(
                        _taskId,
                        _developmentId,
                        Cli?.ProviderId,
                        ArgumentsToSend(),
                        Prompt,
                        RunDirectly,
                        Cli is null ? null : Monitor),
                    token),
                CancellationToken.None);

            if (Cli is not null)
            {
                _loadedArguments = Arguments = Arguments.Trim();
                _loadedMonitor = Monitor;
            }

            State = StateFor(Session);

            Message = Session.Status is AgentSessionStatus.Exited
                ? $"O {AgentName} encerrou logo ao abrir."
                : Session.MonitoringNote;
        }
        catch (DomainException exception)
        {
            Message = exception.Message;
            State = previous;

            // "Não encontrado" pode ser desinstalação com o app aberto: a
            // próxima olhada mostra as instruções, e não um botão que falha.
            await DetectAsync(CancellationToken.None);

            if (Cli is { Detection.IsInstalled: false })
            {
                State = AgentPanelState.NotInstalled;
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "AgentSessionStartFailed {TaskId}", _taskId);
            Message = $"Não foi possível abrir o {AgentName}.";
            State = previous;
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// Leva ao terminal da sessão. Nunca abre um agente novo: se o processo
    /// acabou, o card passa a "Finalizado" e diz isso.
    /// </summary>
    [RelayCommand(CanExecute = nameof(CanFocus))]
    public async Task FocusAsync()
    {
        IsBusy = true;
        Message = null;

        try
        {
            var result = await runner.RunAsync<FocusAgentSessionHandler, AgentFocusResult>(
                (handler, token) => handler.HandleAsync(new FocusAgentSession(_taskId, _developmentId), token),
                CancellationToken.None);

            Session = result.Session;
            State = StateFor(result.Session);

            if (!result.Focused)
            {
                Message = result.Session.IsActive
                    ? $"Não foi possível localizar a janela do terminal ({ProcessText}). Procure-a na barra de tarefas."
                    : $"O {AgentName} deste ambiente já foi encerrado.";
            }
        }
        catch (DomainException exception)
        {
            Message = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "AgentSessionFocusFailed {TaskId}", _taskId);
            Message = "Não foi possível trazer o terminal para a frente.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>"Verificar novamente", depois de instalar.</summary>
    [RelayCommand]
    public Task RecheckAsync() => RefreshAsync(CancellationToken.None);

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
            logger.LogWarning(exception, "AgentCopyFailed");
            Message = "Não foi possível copiar. Tente de novo.";
        }
    }

    [RelayCommand]
    public async Task OpenInstallDocsAsync()
    {
        if (InstallGuide?.DocumentationUrl is { } url && !await shell.OpenUriAsync(url))
        {
            Message = $"Não foi possível abrir o navegador. O endereço é {url}";
        }
    }

    private async Task DetectAsync(CancellationToken cancellationToken)
    {
        try
        {
            var cli = await runner.RunAsync<DetectAgentCliHandler, AgentCliStatus>(
                (handler, token) => handler.HandleAsync(new DetectAgentCli(Cli?.ProviderId), token),
                cancellationToken);

            Cli = cli;

            // Não atropela o que o usuário está digitando.
            if (Arguments == _loadedArguments)
            {
                _loadedArguments = Arguments = cli.Arguments;
            }

            if (Monitor == _loadedMonitor)
            {
                _loadedMonitor = Monitor = cli.Monitor;
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "AgentDetectionFailed");
        }
    }

    /// <summary>
    /// Antes de a detecção trazer o padrão salvo, o campo vazio não é escolha
    /// do usuário — mandar <c>null</c> faz o caso de uso usar o salvo.
    /// </summary>
    private string? ArgumentsToSend() => Cli is null ? null : Arguments;

    private AgentPanelState StateFor(AgentSessionView? session) => session?.Status switch
    {
        AgentSessionStatus.Running or AgentSessionStatus.Starting => AgentPanelState.Running,
        AgentSessionStatus.Exited => Installed ? AgentPanelState.Finished : AgentPanelState.NotInstalled,
        AgentSessionStatus.Failed => Installed ? AgentPanelState.Failed : AgentPanelState.NotInstalled,
        _ => Installed ? AgentPanelState.Idle : AgentPanelState.NotInstalled,
    };

    /// <summary>Sem resposta da detecção, não se afirma que falta: o botão fica, e o erro vem ao clicar.</summary>
    private bool Installed => Cli is not { Detection.IsInstalled: false };

    private static string Clock(DateTimeOffset instant) =>
        instant.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
}
