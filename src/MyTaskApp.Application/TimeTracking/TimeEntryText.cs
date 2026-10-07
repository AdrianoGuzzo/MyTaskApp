using System.Globalization;
using MyTaskApp.Application.Planning;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Application.TimeTracking;

/// <summary>
/// O período escrito na hora do usuário (ADR-002): o domínio guarda instantes e
/// não sabe o fuso; quem sabe é o <see cref="IUserClock"/>.
/// </summary>
internal static class TimeEntryText
{
    public static DateTime Local(DateTimeOffset instant, IUserClock clock) =>
        TimeZoneInfo.ConvertTime(instant, clock.TimeZone).DateTime;

    /// <summary>"14:00 → 15:30", ou "14:00 → agora" se ainda corre.</summary>
    public static string Range(TimeEntry entry, IUserClock clock) =>
        WorkTimeFormatter.Range(
            Local(entry.StartedAt, clock),
            entry.EndedAt is { } ended ? Local(ended, clock) : null);

    /// <summary>"06/10 14:00 → 15:30": a mensagem de sobreposição pode falar de outro dia.</summary>
    public static string DatedRange(TimeEntry entry, IUserClock clock) =>
        $"{Local(entry.StartedAt, clock).ToString("dd/MM", CultureInfo.InvariantCulture)} {Range(entry, clock)}";

    /// <summary>"06/10 14:00 → 15:30 (1h 30min) · Manual", o detalhe da auditoria.</summary>
    public static string Audit(TimeEntry entry, IUserClock clock, DateTimeOffset now) =>
        $"{DatedRange(entry, clock)} ({WorkTimeFormatter.Duration(entry.Duration(now))}) · {WorkTimeFormatter.Source(entry.Source)}";
}
