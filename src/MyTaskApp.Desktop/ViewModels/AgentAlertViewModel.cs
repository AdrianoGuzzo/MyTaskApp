using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Remote;
using MyTaskApp.Desktop.Terminals;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// O aviso de que um agente parou esperando o usuário (ADR-037): uma pergunta,
/// uma permissão, um plano para aprovar, uma resposta pronta para revisar.
/// </summary>
/// <remarks>
/// Diferente do lembrete, fechar não "atende" nada: o agente continua
/// esperando no terminal, e o aviso some sozinho quando ele volta a trabalhar.
/// <para>
/// Também avisa que alguém abriu de longe um Claude nesta máquina (ADR-092):
/// a conversa roda sem terminal, e o botão leva a ela.
/// </para>
/// </remarks>
/// <param name="conversations">Abre a conversa sem terminal; nulo nos testes que não falam dela.</param>
/// <param name="remoteTerminals">Segue o terminal de outra máquina (ADR-103); nulo nos testes que não falam dele.</param>
public sealed partial class AgentAlertViewModel(
    IUseCaseRunner runner,
    ILogger<AgentAlertViewModel> logger,
    ManagedConversationOpener? conversations = null,
    RemoteTerminalWindows? remoteTerminals = null) : ObservableObject
{
    /// <summary>A conversa sem terminal aberta de longe: o botão abre a conversa.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenLabel))]
    private bool _isRemoteConversation;

    /// <summary>O Claude roda em outra máquina (ADR-103): o botão segue de longe, ou some.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(OpenLabel), nameof(CanOpen))]
    private RemoteAgentOrigin? _remote;

    public string OpenLabel => Remote switch
    {
        { IsConversation: true } => "Abrir conversa",
        not null => "Seguir",
        _ => IsRemoteConversation ? "Abrir conversa" : "Abrir terminal",
    };

    /// <summary>Sem o terminal remoto aceito lá, não há para onde ir: só o aviso.</summary>
    public bool CanOpen => Remote is not { IsConversation: false, CanFollow: false };

    [ObservableProperty]
    private string _heading = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string? _context;

    [ObservableProperty]
    private string? _messageText;

    [ObservableProperty]
    private string _timeLabel = string.Empty;

    [ObservableProperty]
    private bool _isQuestion;

    [ObservableProperty]
    private bool _isFailure;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>"Prazo da tarefa: ATENÇÃO · vence amanhã às 18:00" — só quando aperta (§26).</summary>
    [ObservableProperty]
    private string? _deadlineLabel;

    public Guid SessionId { get; private set; }

    public Guid TaskId { get; private set; }

    public Guid? DevelopmentId { get; private set; }

    /// <summary>O usuário foi ao terminal ou dispensou: a janela pode sair.</summary>
    public event Action<AgentAlertViewModel>? Closed;

    public void Show(AgentAttention attention)
    {
        SessionId = attention.SessionId;
        TaskId = attention.TaskId;
        DevelopmentId = attention.DevelopmentId;
        IsRemoteConversation = attention.OpenedBy is not null;
        Remote = attention.Remote;
        Heading = attention switch
        {
            { OpenedBy: { } origin } => $"{attention.AgentName} · aberto de {origin}",
            { Remote: { } remote } => HeadingFor($"{attention.AgentName} em {remote.MachineName}", attention.Activity),
            _ => HeadingFor(attention.AgentName, attention.Activity),
        };
        Title = attention.TaskTitle;
        Context = attention.RepositoryName is { } repository
            ? attention.Branch is { } branch ? $"{repository} · {branch}" : repository
            : null;
        MessageText = attention switch
        {
            { OpenedBy: not null } => "Roda sem terminal, pelo agente desta máquina. Acompanhe e responda pela conversa, daqui ou de lá.",
            { Remote: { } remote } => Excerpt(attention.Message) ?? RemoteHint(remote),
            _ => Excerpt(attention.Message),
        };
        TimeLabel = attention.At.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        IsQuestion = attention.Activity is AgentActivity.WaitingForUser;
        IsFailure = attention.Activity is AgentActivity.Failed;
        DeadlineLabel = attention.DeadlineLabel;
        ErrorMessage = null;
    }

    /// <summary>"Claude Code · aguardando você", "Claude Code · pronto para revisão".</summary>
    public static string HeadingFor(string agentName, AgentActivity activity) => activity switch
    {
        AgentActivity.WaitingForUser => $"{agentName} · aguardando você",
        AgentActivity.WaitingReview => $"{agentName} · pronto para revisão",
        AgentActivity.Failed => $"{agentName} · a resposta falhou",
        _ => agentName,
    };

    /// <summary>Sem o texto do Claude (a máquina não aceita o terminal remoto), onde responder.</summary>
    public static string RemoteHint(RemoteAgentOrigin remote) => remote switch
    {
        { IsConversation: true } => $"Na máquina {remote.MachineName}. Responda pela conversa.",
        { CanFollow: true } => $"Na máquina {remote.MachineName}. Siga o terminal de lá para responder.",
        _ => $"Na máquina {remote.MachineName}, que não aceita o terminal remoto: responda por lá.",
    };

    /// <summary>
    /// A resposta inteira não cabe num aviso: as primeiras linhas, sem as
    /// vazias, e reticências se sobrar.
    /// </summary>
    public static string? Excerpt(string? message, int maxLength = 220)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return null;
        }

        var compact = string.Join(
            ' ',
            message.Split(["\r\n", "\n", "\r"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        return compact.Length <= maxLength ? compact : compact[..maxLength].TrimEnd() + "…";
    }

    /// <summary>Leva ao terminal do agente — o mesmo caso de uso do card e do selo.</summary>
    [RelayCommand]
    public async Task OpenTerminalAsync()
    {
        ErrorMessage = null;

        if (Remote is { } remote)
        {
            await FollowAsync(remote);
            return;
        }

        if (IsRemoteConversation)
        {
            await OpenConversationAsync();
            return;
        }

        try
        {
            var result = await runner.RunAsync<FocusAgentSessionHandler, AgentFocusResult>(
                (handler, token) => handler.HandleAsync(new FocusAgentSession(TaskId, DevelopmentId, SessionId), token),
                CancellationToken.None);

            if (!result.Focused)
            {
                ErrorMessage = result.Session.IsActive
                    ? "Não foi possível localizar a janela do terminal. Procure-a na barra de tarefas."
                    : "Este agente já foi encerrado.";

                if (result.Session.IsActive)
                {
                    return;
                }
            }

            Closed?.Invoke(this);
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "AgentAlertFocusFailed {TaskId} {SessionId}", TaskId, SessionId);
            ErrorMessage = "Não foi possível trazer o terminal para a frente.";
        }
    }

    [RelayCommand]
    public void Dismiss() => Closed?.Invoke(this);

    /// <summary>O Claude de outra máquina: segue o terminal de lá, ou abre a conversa (ADR-103).</summary>
    private async Task FollowAsync(RemoteAgentOrigin remote)
    {
        try
        {
            if (remote.IsConversation)
            {
                ErrorMessage = conversations is null
                    ? "Abra a conversa pela janela Máquinas."
                    : await conversations.OpenRemoteAsync(remote.DeviceId, DevelopmentId, SessionId, CancellationToken.None);
            }
            else if (!remote.CanFollow || DevelopmentId is not { } developmentId)
            {
                ErrorMessage = $"{remote.MachineName} mostra o estado, mas não aceita o terminal remoto.";
            }
            else if (remoteTerminals?.Show(new RemoteTerminalTarget(remote.DeviceId, SessionId, TaskId, developmentId), $"{Title} — terminal em {remote.MachineName}") != true)
            {
                ErrorMessage = "Não foi possível abrir o terminal de longe agora.";
            }

            if (ErrorMessage is null)
            {
                Closed?.Invoke(this);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "AgentAlertFollowFailed {TaskId} {SessionId}", TaskId, SessionId);
            ErrorMessage = "Não foi possível seguir o Claude de longe.";
        }
    }

    private async Task OpenConversationAsync()
    {
        if (conversations is null || DevelopmentId is not { } developmentId)
        {
            ErrorMessage = "Abra a conversa pela aba Desenvolvimento da tarefa.";
            return;
        }

        try
        {
            ErrorMessage = await conversations.OpenAsync(developmentId, SessionId, CancellationToken.None);

            if (ErrorMessage is null)
            {
                Closed?.Invoke(this);
            }
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "AgentAlertConversationFailed {TaskId} {SessionId}", TaskId, SessionId);
            ErrorMessage = "Não foi possível abrir a conversa.";
        }
    }
}
