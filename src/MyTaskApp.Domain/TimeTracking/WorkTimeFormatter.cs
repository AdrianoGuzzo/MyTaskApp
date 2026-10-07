using System.Globalization;

namespace MyTaskApp.Domain.TimeTracking;

/// <summary>
/// O texto do tempo trabalhado, num lugar só (ADR-052). A linha, o HUD, a aba
/// "Tempo", o card do plano e a auditoria leem daqui — o mesmo motivo do
/// <c>DeadlineFormatter</c>: "1h 30min" escrito em dois lugares acaba virando
/// "90 min" num deles.
/// </summary>
public static class WorkTimeFormatter
{
    /// <summary>
    /// Duração de relance, em minutos inteiros: "4h 32min", "1h 05min", "2h",
    /// "42min", "0min". Sem dias — "52h" é o que se lança numa planilha de horas.
    /// </summary>
    public static string Duration(TimeSpan span)
    {
        var minutes = span <= TimeSpan.Zero ? 0L : (long)span.TotalMinutes;

        if (minutes < 60)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{minutes}min");
        }

        var hours = minutes / 60;
        var rest = minutes % 60;

        return rest == 0
            ? string.Create(CultureInfo.InvariantCulture, $"{hours}h")
            : string.Create(CultureInfo.InvariantCulture, $"{hours}h {rest:00}min");
    }

    /// <summary>O cronômetro correndo: "00:37:42", "01:37:42", "124:05:00".</summary>
    public static string Clock(TimeSpan span)
    {
        if (span < TimeSpan.Zero)
        {
            span = TimeSpan.Zero;
        }

        var hours = (long)span.TotalHours;

        return string.Create(CultureInfo.InvariantCulture, $"{hours:00}:{span.Minutes:00}:{span.Seconds:00}");
    }

    /// <summary>
    /// O gasto contra a estimativa: "4h 30min / 6h · 75%" e, passou dela,
    /// "7h 20min / 6h · +1h 20min acima da estimativa". Informa, não bloqueia.
    /// </summary>
    public static string AgainstEstimate(TimeSpan spent, TimeSpan estimate)
    {
        if (estimate <= TimeSpan.Zero)
        {
            return Duration(spent);
        }

        var head = $"{Duration(spent)} / {Duration(estimate)}";

        if (spent > estimate && Duration(spent - estimate) != "0min")
        {
            return $"{head} · +{Duration(spent - estimate)} acima da estimativa";
        }

        var percent = (int)Math.Floor(Math.Max(0, spent.Ticks) * 100d / estimate.Ticks);

        return string.Create(CultureInfo.InvariantCulture, $"{head} · {Math.Min(percent, 100)}%");
    }

    /// <summary>
    /// O período em hora de parede: "14:00 → 15:30"; quando atravessa a
    /// meia-noite, o fim leva o dia ("23:00 → 07/10 01:30"); ainda correndo,
    /// "14:00 → agora".
    /// </summary>
    public static string Range(DateTime localStart, DateTime? localEnd)
    {
        var start = Time(localStart);

        if (localEnd is not { } end)
        {
            return $"{start} → agora";
        }

        return end.Date == localStart.Date
            ? $"{start} → {Time(end)}"
            : $"{start} → {end.ToString("dd/MM", CultureInfo.InvariantCulture)} {Time(end)}";
    }

    /// <summary>"Timer" ou "Manual", como na tela do histórico.</summary>
    public static string Source(TimeEntrySource source) => source switch
    {
        TimeEntrySource.Timer => "Timer",
        TimeEntrySource.Manual => "Manual",
        _ => source.ToString(),
    };

    private static string Time(DateTime local) => local.ToString("HH:mm", CultureInfo.InvariantCulture);
}
