using MyTaskApp.Domain.Deadlines;

namespace MyTaskApp.Application.Deadlines;

/// <summary>
/// Um aviso de prazo pronto para a tela. O texto já vem montado pelo
/// <see cref="DeadlineFormatter"/>: diz o que aconteceu, com a tarefa e o
/// momento, e nunca "você tem pendências" (§27).
/// </summary>
/// <param name="IsUrgent">2 horas ou atrasada: com som, e em destaque.</param>
public sealed record DeadlineAlert(
    Guid OccurrenceId,
    Guid TaskId,
    string Title,
    string Heading,
    string Message,
    DeadlineAlertStage Stage,
    DeadlineSeverity Severity,
    bool IsUrgent);

/// <summary>Vários prazos de uma vez viram um aviso só, como os lembretes.</summary>
public sealed record DeadlineDigest(int Count, bool IsUrgent);

/// <summary>Põe o aviso de prazo na tela. A implementação é da borda (Desktop).</summary>
public interface IDeadlineAlertPresenter
{
    /// <summary>Mostra, ou atualiza no lugar, o aviso do prazo desta ocorrência.</summary>
    Task PresentAsync(DeadlineAlert alert, CancellationToken cancellationToken = default);

    Task PresentDigestAsync(DeadlineDigest digest, CancellationToken cancellationToken = default);

    /// <summary>Tira da tela o aviso do prazo desta ocorrência, se houver.</summary>
    Task DismissAsync(Guid occurrenceId, CancellationToken cancellationToken = default);
}

/// <summary>Sem tela (testes, host sem UI): ninguém para avisar.</summary>
public sealed class NoDeadlineAlertPresenter : IDeadlineAlertPresenter
{
    public Task PresentAsync(DeadlineAlert alert, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task PresentDigestAsync(DeadlineDigest digest, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;

    public Task DismissAsync(Guid occurrenceId, CancellationToken cancellationToken = default) =>
        Task.CompletedTask;
}
