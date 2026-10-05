using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Deadlines;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Deadlines;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// O aviso de prazo (ADR-050, §27): o que está vencendo, quando, e as três
/// saídas que importam — abrir a tarefa, dar mais um dia ao prazo, ou calar o
/// aviso por uma hora sem mexer no prazo (§21).
/// </summary>
public sealed partial class DeadlineAlertViewModel(
    IUseCaseRunner runner,
    ILogger<DeadlineAlertViewModel> logger) : ObservableObject
{
    public static readonly TimeSpan SnoozeFor = TimeSpan.FromHours(1);

    [ObservableProperty]
    private string _heading = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _message = string.Empty;

    [ObservableProperty]
    private bool _isUrgent;

    [ObservableProperty]
    private bool _isOverdue;

    /// <summary>O resumo de vários prazos não tem tarefa para abrir nem prazo para mudar.</summary>
    [ObservableProperty]
    private bool _isDigest;

    [ObservableProperty]
    private string? _errorMessage;

    public Guid OccurrenceId { get; private set; }

    /// <summary>O usuário reagiu: a janela sai e a lista recarrega.</summary>
    public event Action? Acted;

    /// <summary>"Abrir": quem sabe abrir a tarefa é o composition root.</summary>
    public event Action<Guid>? OpenRequested;

    public void Show(DeadlineAlert alert)
    {
        OccurrenceId = alert.OccurrenceId;
        Heading = alert.Heading;
        Title = alert.Title;
        Message = alert.Message;
        IsUrgent = alert.IsUrgent;
        IsOverdue = alert.Severity is DeadlineSeverity.Overdue;
        IsDigest = alert.OccurrenceId == Guid.Empty;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void Open()
    {
        if (!IsDigest)
        {
            OpenRequested?.Invoke(OccurrenceId);
        }

        Acted?.Invoke();
    }

    /// <summary>"+1 dia": muda o prazo, de propósito e à vista (§23).</summary>
    [RelayCommand]
    private Task ExtendAsync() =>
        RunAsync(
            () => runner.RunAsync<SetDeadlineHandler, TaskDeadlineView>(
                (handler, token) => handler.HandleAsync(
                    new SetDeadlineShortcut(OccurrenceId, DeadlineShortcut.Tomorrow), token),
                CancellationToken.None),
            "Não foi possível mudar o prazo.");

    /// <summary>"Adiar 1 h": só o aviso. O prazo continua onde estava (§21).</summary>
    [RelayCommand]
    private Task SnoozeAsync() =>
        RunAsync(
            () => runner.RunAsync<SnoozeDeadlineAlertHandler>(
                (handler, token) => handler.HandleAsync(new SnoozeDeadlineAlert(OccurrenceId, SnoozeFor), token),
                CancellationToken.None),
            "Não foi possível adiar o aviso.");

    /// <summary>"OK": o aviso já foi registrado ao sair; fechar é só fechar.</summary>
    [RelayCommand]
    private void Dismiss() => Acted?.Invoke();

    private async Task RunAsync(Func<Task> operation, string fallbackMessage)
    {
        ErrorMessage = null;

        try
        {
            await operation();
            Acted?.Invoke();
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "DeadlineAlertActionFailed {OccurrenceId}", OccurrenceId);
            ErrorMessage = fallbackMessage;
        }
    }
}
