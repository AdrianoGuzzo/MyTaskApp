using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain.Deadlines;

namespace MyTaskApp.Application.Deadlines;

/// <summary>
/// A parte do tique que cuida dos prazos: avisa quem chegou num degrau novo e
/// registra que avisou (ADR-050).
/// </summary>
public sealed record DispatchDeadlineAlerts;

public sealed record DispatchDeadlineAlertsResult(int Dispatched, bool Paused)
{
    public static DispatchDeadlineAlertsResult Nothing { get; } = new(0, Paused: false);

    public static DispatchDeadlineAlertsResult WhilePaused { get; } = new(0, Paused: true);
}

public sealed class DispatchDeadlineAlertsHandler(
    IDeadlineAlertQuery query,
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    IDeadlineSettingsStore deadlineSettings,
    IReminderSettingsStore reminderSettings,
    IDeadlineAlertPresenter presenter,
    ISoundPlayer sound,
    IUserClock clock,
    TimeProvider timeProvider,
    ILogger<DispatchDeadlineAlertsHandler> logger)
{
    /// <summary>Acima disso, os avisos de prazo viram um só.</summary>
    public const int MaxVisible = 3;

    public async Task<DispatchDeadlineAlertsResult> HandleAsync(
        DispatchDeadlineAlerts command,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();

        // "Pausar lembretes por 1 hora" vale para o prazo também: quem pediu
        // silêncio pediu silêncio. Nada se perde — o degrau continua cruzado e
        // é avisado, uma vez, quando a pausa acabar.
        if ((await reminderSettings.GetAsync(cancellationToken)).IsPausedAt(now))
        {
            return DispatchDeadlineAlertsResult.WhilePaused;
        }

        var candidates = await query.GetCandidatesAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return DispatchDeadlineAlertsResult.Nothing;
        }

        var policy = (await deadlineSettings.GetAsync(cancellationToken)).Alerts;
        var alerts = await MarkAlertedAsync(candidates, policy, now, cancellationToken);

        if (alerts.Count == 0)
        {
            logger.LogDebug("DeadlineTickFoundNothing {Now}", now);
            return DispatchDeadlineAlertsResult.Nothing;
        }

        // Marcar, gravar e só então apresentar (ADR-004): uma queda aqui perde
        // um aviso, em vez de repetir um que o usuário não tem como desfazer.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await PresentAsync(alerts, cancellationToken);

        logger.LogInformation(
            "DeadlineAlertsDispatched {Count} {MostSevere}",
            alerts.Count,
            alerts.Max(alert => alert.Stage));

        return new DispatchDeadlineAlertsResult(alerts.Count, Paused: false);
    }

    private async Task<List<DeadlineAlert>> MarkAlertedAsync(
        IReadOnlyList<DeadlineCandidateRow> candidates,
        DeadlineAlertPolicy policy,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var today = clock.ToLocalDate(now);
        var alerts = new List<DeadlineAlert>();

        foreach (var row in candidates)
        {
            var deadlineAt = clock.ToInstant(row.Deadline.Date, row.Deadline.Time);
            var stage = DeadlineAlerting.Decide(
                policy.StagesFor(row.TaskOverride),
                policy.OverdueRepeatEvery,
                deadlineAt,
                row.Alert,
                now);

            if (stage is not { } due)
            {
                continue;
            }

            var task = await tasks.FindByOccurrenceIdAsync(row.OccurrenceId, cancellationToken);

            // Apagada entre a consulta e agora: corrida normal, não falha.
            if (task is null)
            {
                continue;
            }

            task.MarkDeadlineAlerted(row.OccurrenceId, due, now);

            var snapshot = DeadlineAssessment.Assess(row.Deadline, deadlineAt, now, today);

            alerts.Add(new DeadlineAlert(
                row.OccurrenceId,
                row.TaskId,
                row.Title,
                DeadlineFormatter.AlertHeading(snapshot, row.Deadline, today),
                DeadlineFormatter.AlertMessage(snapshot, row.Deadline, today),
                due,
                snapshot.Severity,
                IsUrgent: due >= DeadlineAlertStage.TwoHours));
        }

        return alerts;
    }

    private async Task PresentAsync(List<DeadlineAlert> alerts, CancellationToken cancellationToken)
    {
        // Um som por tique, e só para o que é urgente: a véspera avisa sem bipe.
        if (alerts.Exists(alert => alert.IsUrgent))
        {
            try
            {
                sound.PlayAlert();
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "DeadlineAlertSoundFailed");
            }
        }

        try
        {
            if (alerts.Count > MaxVisible)
            {
                await presenter.PresentDigestAsync(
                    new DeadlineDigest(alerts.Count, alerts.Exists(alert => alert.IsUrgent)),
                    cancellationToken);
                return;
            }

            foreach (var alert in alerts)
            {
                await presenter.PresentAsync(alert, cancellationToken);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Não desmarca, pelo mesmo motivo dos lembretes: a linha continua
            // dizendo o prazo, e um apresentador quebrado não pode ser martelado.
            logger.LogError(exception, "DeadlineAlertPresentationFailed {Count}", alerts.Count);
        }
    }
}
