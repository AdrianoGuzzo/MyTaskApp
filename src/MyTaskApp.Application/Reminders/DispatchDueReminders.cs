using System.Globalization;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Application.Reminders;

/// <summary>Um tique do agendador: avisa o que venceu e reagenda o próximo aviso.</summary>
public sealed record DispatchDueReminders;

public sealed record DispatchDueRemindersResult(int Dispatched, bool Paused)
{
    public static DispatchDueRemindersResult Nothing { get; } = new(0, Paused: false);

    public static DispatchDueRemindersResult WhilePaused { get; } = new(0, Paused: true);
}

public sealed class DispatchDueRemindersHandler(
    IDueReminderQuery due,
    ITaskItemRepository tasks,
    IUnitOfWork unitOfWork,
    IReminderSettingsStore settings,
    IAlertPresenter presenter,
    ISoundPlayer sound,
    TimeProvider timeProvider,
    ILogger<DispatchDueRemindersHandler> logger)
{
    /// <summary>Acima disso, os avisos viram um só — ver <see cref="ReminderDigest"/>.</summary>
    public const int MaxVisible = 5;

    /// <summary>Teto do tique. O que sobrar vem no próximo, já coalescido.</summary>
    public const int BatchSize = 50;

    public async Task<DispatchDueRemindersResult> HandleAsync(
        DispatchDueReminders command,
        CancellationToken cancellationToken = default)
    {
        var now = timeProvider.GetUtcNow();
        var stored = await settings.GetAsync(cancellationToken);

        if (stored.IsPausedAt(now))
        {
            // Nada se perde: os disparos continuam vencidos e coalescem na
            // retomada. Isto só é seguro porque a coalescência é estrutural.
            return DispatchDueRemindersResult.WhilePaused;
        }

        var rows = await due.GetDueAsync(now, BatchSize, cancellationToken);

        if (rows.Count == 0)
        {
            // Debug, e não Information: um poll de 30 s encheria os 7 dias de
            // retenção do log com tique vazio.
            logger.LogDebug("ReminderTickFoundNothing {Now}", now);
            return DispatchDueRemindersResult.Nothing;
        }

        var alerts = await MarkFiredAsync(rows, now, cancellationToken);

        if (alerts.Count == 0)
        {
            return DispatchDueRemindersResult.Nothing;
        }

        // Um único SaveChanges para o tique inteiro, antes de qualquer pixel.
        // Uma queda aqui perde UM aviso, que volta um intervalo depois porque o
        // lembrete repete; a ordem inversa duplicaria um aviso que o usuário
        // não tem como desfazer.
        await unitOfWork.SaveChangesAsync(cancellationToken);

        await PresentAsync(alerts, cancellationToken);

        logger.LogInformation(
            "RemindersDispatched {Count} {TopStep}",
            alerts.Count,
            alerts.Max(alert => alert.Level.Step));

        return new DispatchDueRemindersResult(alerts.Count, Paused: false);
    }

    private async Task<List<ReminderAlert>> MarkFiredAsync(
        IReadOnlyList<DueReminderRow> rows,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var alerts = new List<ReminderAlert>(rows.Count);

        foreach (var row in rows)
        {
            var task = await tasks.FindByOccurrenceIdAsync(row.OccurrenceId, cancellationToken);

            // Apagada entre a consulta e agora: não é falha, é corrida normal.
            if (task is null)
            {
                continue;
            }

            var occurrence = task.GetOccurrence(row.OccurrenceId);

            // A grade original é preservada: a base é o disparo previsto, não
            // "agora". Um lembrete das 15:00 continua caindo em 15:15 e 15:30
            // mesmo que o tique tenha chegado atrasado.
            var next = ReminderScheduling.NextFireAfter(task.Reminder, row.NextFireAtUtc, now);

            occurrence.MarkReminderFired(now, next);

            alerts.Add(new ReminderAlert(
                row.OccurrenceId,
                row.TaskId,
                row.Title,
                FormatTime(row.ScheduledTime),
                ReminderEscalation.LevelFor(occurrence.Reminder.Attempt, row.Channels),
                now - (row.WaitingSinceUtc ?? now)));
        }

        return alerts;
    }

    private async Task PresentAsync(
        List<ReminderAlert> alerts,
        CancellationToken cancellationToken)
    {
        // Um som por tique, não um por aviso: cinco bipes juntos não são cinco
        // vezes mais úteis do que um.
        if (alerts.Exists(alert => alert.Level.PlaySound))
        {
            try
            {
                sound.PlayAlert();
            }
            catch (Exception exception)
            {
                // A porta promete não lançar, mas se uma implementação quebrar
                // essa promessa não pode levar junto os avisos do tique.
                logger.LogWarning(exception, "AlertSoundFailed");
            }
        }

        if (alerts.Count > MaxVisible)
        {
            await PresentDigestAsync(alerts, cancellationToken);
            return;
        }

        foreach (var alert in alerts)
        {
            try
            {
                await presenter.PresentAsync(alert, cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // Não desmarca: desmarcar faria o agendador martelar um
                // apresentador quebrado a cada 30 s. O próximo disparo já está
                // agendado, e o ⚠ na tela "Hoje" continua acusando.
                logger.LogError(
                    exception,
                    "ReminderPresentationFailed {OccurrenceId}",
                    alert.OccurrenceId);
            }
        }
    }

    private async Task PresentDigestAsync(
        List<ReminderAlert> alerts,
        CancellationToken cancellationToken)
    {
        var digest = new ReminderDigest(
            alerts.Count,
            alerts.MaxBy(alert => alert.Level.Step)!.Level,
            alerts.Max(alert => alert.Waiting));

        try
        {
            await presenter.PresentDigestAsync(digest, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogError(exception, "ReminderDigestPresentationFailed {Count}", digest.Count);
        }
    }

    private static string? FormatTime(TimeOnly? time) =>
        time?.ToString("HH:mm", CultureInfo.InvariantCulture);
}
