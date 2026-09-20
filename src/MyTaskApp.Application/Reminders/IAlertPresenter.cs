using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Application.Reminders;

/// <summary>Um aviso pronto para a tela: o domínio já decidiu o quanto insistir.</summary>
public sealed record ReminderAlert(
    Guid OccurrenceId,
    Guid TaskId,
    string Title,
    string? TimeLabel,
    AlertLevel Level,
    TimeSpan Waiting);

/// <summary>
/// Vários lembretes vencidos de uma vez viram um aviso só. Sem isto, abrir o app
/// depois de um mês jogaria dezenas de janelas na tela.
/// </summary>
public sealed record ReminderDigest(int Count, AlertLevel Level, TimeSpan LongestWait);

public interface IAlertPresenter
{
    /// <summary>
    /// Retorna quando o aviso está <b>na tela</b> — não quando o usuário reage.
    /// O fim do lembrete é o atendimento, não esta chamada.
    /// </summary>
    Task PresentAsync(ReminderAlert alert, CancellationToken cancellationToken = default);

    Task PresentDigestAsync(ReminderDigest digest, CancellationToken cancellationToken = default);

    /// <summary>Tira da tela o aviso desta ocorrência, se houver um aberto.</summary>
    Task DismissAsync(Guid occurrenceId, CancellationToken cancellationToken = default);
}
