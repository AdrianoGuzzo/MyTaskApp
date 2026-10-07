namespace MyTaskApp.Domain.TimeTracking;

/// <summary>
/// As regras sobre o conjunto de períodos de uma tarefa (ADR-052). Funções puras,
/// como o <c>TodayClassifier</c>: recebem os períodos e o "agora" por parâmetro,
/// e cada borda vira um teste direto.
/// </summary>
public static class TimeLog
{
    /// <summary>
    /// O período que <c>[start, end)</c> invadiria, ou <c>null</c>. Os intervalos
    /// são semiabertos: 14:00 → 15:00 e 15:00 → 16:00 só se encostam. Um período
    /// ativo vale até <paramref name="now"/>; um candidato sem fim (o ▶) vale
    /// dali para sempre.
    /// </summary>
    /// <param name="ignoring">O próprio período, numa edição.</param>
    public static TimeEntry? FindOverlap(
        DateTimeOffset start,
        DateTimeOffset? end,
        IEnumerable<TimeEntry> siblings,
        DateTimeOffset now,
        Guid? ignoring = null)
    {
        ArgumentNullException.ThrowIfNull(siblings);

        var candidateEnd = end ?? DateTimeOffset.MaxValue;

        return siblings
            .Where(entry => entry.Id != ignoring)
            .OrderBy(entry => entry.StartedAt)
            .FirstOrDefault(entry =>
            {
                var siblingEnd = entry.EndedAt ?? (now > entry.StartedAt ? now : entry.StartedAt.AddTicks(1));

                return start < siblingEnd && entry.StartedAt < candidateEnd;
            });
    }

    /// <summary>
    /// Recusa o período que invadiria outro da mesma tarefa. A mensagem diz qual,
    /// escrito por quem chama — o domínio não sabe o fuso do usuário.
    /// </summary>
    public static void EnsureNoOverlap(
        DateTimeOffset start,
        DateTimeOffset? end,
        IEnumerable<TimeEntry> siblings,
        DateTimeOffset now,
        Func<TimeEntry, string> describe,
        Guid? ignoring = null)
    {
        ArgumentNullException.ThrowIfNull(describe);

        if (FindOverlap(start, end, siblings, now, ignoring) is { } conflict)
        {
            throw new DomainException(
                $"Este período se sobrepõe a outro já registrado nesta tarefa ({describe(conflict)}).");
        }
    }

    /// <summary>
    /// O tempo registrado: a soma dos períodos encerrados. O que ainda corre fica
    /// de fora, de propósito — é mostrado à parte, e o total não muda a cada
    /// segundo.
    /// </summary>
    public static TimeSpan Logged(IEnumerable<TimeEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        return entries
            .Where(entry => entry.EndedAt is not null)
            .Aggregate(TimeSpan.Zero, (total, entry) => total + (entry.EndedAt!.Value - entry.StartedAt));
    }
}
