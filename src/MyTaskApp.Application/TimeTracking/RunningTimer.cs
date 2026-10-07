using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Application.TimeTracking;

/// <summary>
/// Encerrar o cronômetro ativo sem salvar: quem chama decide o <c>SaveChanges</c>,
/// para o fim do período entrar junto com o que o causou — concluir, arquivar,
/// mandar para a lixeira ou iniciar outra tarefa (ADR-052).
/// </summary>
internal static class RunningTimer
{
    /// <summary>Encerra o ativo se ele for de uma destas ocorrências.</summary>
    /// <returns>O período encerrado, ou <c>null</c> se não havia nada a encerrar.</returns>
    public static async Task<TimeEntry?> StopIfOnAsync(
        ITimeEntryRepository timeEntries,
        IReadOnlyCollection<Guid> occurrenceIds,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var active = await timeEntries.FindActiveAsync(cancellationToken);

        if (active is null || !occurrenceIds.Contains(active.TaskOccurrenceId))
        {
            return null;
        }

        Close(timeEntries, active, now);

        return active;
    }

    /// <summary>
    /// ⏹ no período ativo. Se o relógio do sistema voltou para antes do início,
    /// o período é descartado em vez de recusado: um cronômetro que não se deixa
    /// parar prenderia o usuário, e um período de duração zero não é trabalho.
    /// </summary>
    public static void Close(ITimeEntryRepository timeEntries, TimeEntry active, DateTimeOffset now)
    {
        if (now > active.StartedAt)
        {
            active.Stop(now);
        }
        else
        {
            timeEntries.Remove(active);
        }
    }
}
