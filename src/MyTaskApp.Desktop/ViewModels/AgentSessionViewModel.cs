using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Desktop.Composition;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;

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
/// O card do agente de IA na aba Desenvolvimento (ADR-029): se ele está
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

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(IsChecking), nameof(IsNotInstalled), nameof(IsIdle), nameof(IsStarting), nameof(IsRunning),
        nameof(IsFinished), nameof(IsFailed), nameof(HasSession), nameof(ShowStart), nameof(StatusText))]
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
        nameof(ExecutablePath), nameof(InstallGuide), nameof(HasInstallGuide), nameof(StatusText))]
    private AgentCliStatus? _cli;

    [ObservableProperty]
    [NotifyPropertyChangedFor(
        nameof(AgentName), nameof(StartLabel), nameof(ProcessText), nameof(StartedText), nameof(RangeText),
        nameof(WorkingDirectory), nameof(FailureReason), nameof(HasSession), nameof(StatusText))]
    private AgentSessionView? _session;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(FocusCommand))]
    private bool _isBusy;

    /// <summary>Um recado curto: "Não foi possível localizar a janela…".</summary>
    [ObservableProperty]
    private string? _message;

    public string AgentName => Cli?.Name ?? Session?.ProviderName ?? "Agente de IA";

    public string StartLabel => $"Iniciar {AgentName}";

    public string NotFoundText => $"{AgentName} não encontrado.";

    public string? VersionText => Cli?.Detection.Version is { } version ? $"Versão {version}" : null;

    public string? ExecutablePath => Cli?.Detection.ExecutablePath;

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
        AgentPanelState.Running => "● Em execução",
        AgentPanelState.Finished => "○ Finalizado",
        AgentPanelState.Failed => "✗ O terminal não abriu",
        AgentPanelState.NotInstalled => NotFoundText,
        _ => "Nenhuma sessão ativa.",
    };

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

    /// <summary>A tarefa do card. Chamado uma vez, na abertura da janela.</summary>
    public void Load(Guid taskId, bool isReadOnly)
    {
        _taskId = taskId;
        IsReadOnly = isReadOnly;
        State = AgentPanelState.Checking;
    }

    /// <summary>
    /// O que está gravado para a tarefa, já conferido contra o sistema — e, sem
    /// sessão em andamento, se o agente está instalado.
    /// </summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (_taskId == Guid.Empty || State is AgentPanelState.Starting)
        {
            return;
        }

        try
        {
            var session = await runner.RunAsync<GetTaskAgentSessionHandler, AgentSessionView?>(
                (handler, token) => handler.HandleAsync(new GetTaskAgentSession(_taskId), token),
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
                (handler, token) => handler.HandleAsync(new StartAgentSession(_taskId, Cli?.ProviderId), token),
                CancellationToken.None);

            State = StateFor(Session);

            Message = Session.Status is AgentSessionStatus.Exited
                ? $"O {AgentName} encerrou logo ao abrir."
                : null;
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
                (handler, token) => handler.HandleAsync(new FocusAgentSession(_taskId), token),
                CancellationToken.None);

            Session = result.Session;
            State = StateFor(result.Session);

            if (!result.Focused)
            {
                Message = result.Session.IsActive
                    ? $"Não foi possível localizar a janela do terminal ({ProcessText}). Procure-a na barra de tarefas."
                    : $"O {AgentName} desta tarefa já foi encerrado.";
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
            Cli = await runner.RunAsync<DetectAgentCliHandler, AgentCliStatus>(
                (handler, token) => handler.HandleAsync(new DetectAgentCli(Cli?.ProviderId), token),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "AgentDetectionFailed");
        }
    }

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
