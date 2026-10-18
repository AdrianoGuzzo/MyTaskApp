using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Um aviso na tela. Todo botão aqui é uma forma de "dar atenção" — é o que
/// encerra (ou adia) o lembrete. Fechar a janela não é: o aviso volta.
/// </summary>
public sealed partial class ReminderAlertViewModel(
    IUseCaseRunner runner,
    ILogger<ReminderAlertViewModel> logger) : ObservableObject
{
    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string? _timeLabel;

    [ObservableProperty]
    private string _waitingLabel = string.Empty;

    [ObservableProperty]
    private int _step;

    [ObservableProperty]
    private bool _isProminent;

    [ObservableProperty]
    private string? _errorMessage;

    /// <summary>
    /// As ocorrências que os botões atendem: uma no aviso comum, todas as
    /// agregadas no resumo.
    /// </summary>
    public IReadOnlyList<Guid> OccurrenceIds { get; private set; } = [];

    public bool IsDigest { get; private set; }

    /// <summary>Avisa a janela de que o usuário reagiu e ela pode sair da tela.</summary>
    public event Action<ReminderAlertViewModel>? Acted;

    public void Show(ReminderAlert alert)
    {
        OccurrenceIds = [alert.OccurrenceId];
        IsDigest = false;
        Title = alert.Title;
        TimeLabel = alert.TimeLabel;
        Fill(alert.Level, alert.Waiting);
    }

    public void ShowDigest(ReminderDigest digest)
    {
        OccurrenceIds = digest.OccurrenceIds;
        IsDigest = true;
        Title = digest.Count == 1
            ? "1 checklist está esperando sua atenção"
            : $"{digest.Count} checklists estão esperando sua atenção";
        TimeLabel = null;
        Fill(digest.Level, digest.LongestWait);
    }

    private void Fill(AlertLevel level, TimeSpan waiting)
    {
        Step = level.Step;
        IsProminent = level.Prominent;
        WaitingLabel = DescribeWait(waiting);
        ErrorMessage = null;
    }

    [RelayCommand]
    public Task OpenAsync(CancellationToken cancellationToken) =>
        AcknowledgeAsync(ReminderAcknowledgement.Opened, cancellationToken);

    [RelayCommand]
    public Task MarkSeenAsync(CancellationToken cancellationToken) =>
        AcknowledgeAsync(ReminderAcknowledgement.MarkedSeen, cancellationToken);

    [RelayCommand]
    public async Task SnoozeAsync(string minutes, CancellationToken cancellationToken)
    {
        // Vem como texto porque o XAML passa um literal por botão; converter aqui
        // evita três comandos quase iguais.
        if (!int.TryParse(minutes, CultureInfo.InvariantCulture, out var parsed))
        {
            return;
        }

        await RunForEachAsync(
            occurrenceId => runner.RunAsync<SnoozeReminderHandler>(
                (handler, token) => handler.HandleAsync(
                    new SnoozeReminder(occurrenceId, TimeSpan.FromMinutes(parsed)), token),
                cancellationToken),
            "Não foi possível adiar este lembrete.");
    }

    /// <summary>
    /// "Aguardando sua atenção há 35 minutos." Acima de 90 minutos passa a horas,
    /// porque "há 190 minutos" ninguém lê como tempo.
    /// </summary>
    public static string DescribeWait(TimeSpan waiting)
    {
        var minutes = (int)Math.Round(waiting.TotalMinutes, MidpointRounding.AwayFromZero);

        if (minutes < 1)
        {
            return "Esperando a sua atenção agora.";
        }

        if (minutes == 1)
        {
            return "Aguardando sua atenção há 1 minuto.";
        }

        if (minutes <= 90)
        {
            return $"Aguardando sua atenção há {minutes} minutos.";
        }

        var hours = (int)Math.Round(waiting.TotalHours, MidpointRounding.AwayFromZero);

        return hours == 1
            ? "Aguardando sua atenção há 1 hora."
            : $"Aguardando sua atenção há {hours} horas.";
    }

    private Task AcknowledgeAsync(
        ReminderAcknowledgement by,
        CancellationToken cancellationToken) =>
        RunForEachAsync(
            occurrenceId => runner.RunAsync<AcknowledgeReminderHandler>(
                (handler, token) => handler.HandleAsync(
                    new AcknowledgeReminder(occurrenceId, by), token),
                cancellationToken),
            "Não foi possível registrar que você viu este lembrete.");

    /// <summary>
    /// Uma ocorrência por vez, cada uma no seu caso de uso. Se uma falhar no meio,
    /// o aviso fica e repetir é seguro: as já atendidas só saem da tela de novo.
    /// Mesmo caminho de erro do resto do app (ADR-008).
    /// </summary>
    private async Task RunForEachAsync(Func<Guid, Task> operation, string fallbackMessage)
    {
        ErrorMessage = null;
        var occurrenceIds = OccurrenceIds;
        var current = Guid.Empty;

        try
        {
            foreach (var occurrenceId in occurrenceIds)
            {
                current = occurrenceId;
                await operation(occurrenceId);
            }

            Acted?.Invoke(this);
        }
        catch (DomainException exception)
        {
            ErrorMessage = exception.Message;
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "ReminderAlertActionFailed {OccurrenceId}", current);
            ErrorMessage = fallbackMessage;
        }
    }
}
