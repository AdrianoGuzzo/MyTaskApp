using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
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
/// </remarks>
public sealed partial class AgentAlertViewModel(
    IUseCaseRunner runner,
    ILogger<AgentAlertViewModel> logger) : ObservableObject
{
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
        Heading = HeadingFor(attention.AgentName, attention.Activity);
        Title = attention.TaskTitle;
        Context = attention.RepositoryName is { } repository
            ? attention.Branch is { } branch ? $"{repository} · {branch}" : repository
            : null;
        MessageText = Excerpt(attention.Message);
        TimeLabel = attention.At.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
        IsQuestion = attention.Activity is AgentActivity.WaitingForUser;
        IsFailure = attention.Activity is AgentActivity.Failed;
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

        try
        {
            var result = await runner.RunAsync<FocusAgentSessionHandler, AgentFocusResult>(
                (handler, token) => handler.HandleAsync(new FocusAgentSession(TaskId, DevelopmentId), token),
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
}
