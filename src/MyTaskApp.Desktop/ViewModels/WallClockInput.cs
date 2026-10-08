using System.Globalization;
using System.Text.RegularExpressions;

namespace MyTaskApp.Desktop.ViewModels;

/// <summary>
/// Dia e hora digitados à mão, do jeito que se digita com pressa: <c>0831</c> é
/// 08:31, <c>8</c> é 08:00, <c>0610</c> é 06/10. Lê e escreve o formato
/// brasileiro; quem decide se o valor faz sentido (nada no futuro, fim depois do
/// início) continua sendo o caso de uso.
/// </summary>
public static partial class WallClockInput
{
    /// <summary>
    /// Lê um horário. Só dígitos: 1–2 são a hora (<c>8</c>, <c>14</c>), 3 são
    /// H+MM (<c>831</c>) e 4 são HH+MM (<c>0831</c>). Com separador — <c>:</c>,
    /// <c>h</c> ou <c>.</c> —, os minutos têm dois dígitos ou nenhum (<c>8h</c>).
    /// </summary>
    public static bool TryParseTime(string? text, out TimeOnly time)
    {
        time = default;
        var value = (text ?? string.Empty).Trim();

        int hour;
        var minute = 0;

        if (DigitsOnly().IsMatch(value))
        {
            switch (value.Length)
            {
                case <= 2:
                    hour = Number(value);
                    break;
                case <= 4:
                    hour = Number(value[..^2]);
                    minute = Number(value[^2..]);
                    break;
                default:
                    return false;
            }
        }
        else if (SeparatedTime().Match(value) is { Success: true } match)
        {
            hour = Number(match.Groups["hour"].Value);
            minute = match.Groups["minute"].Success ? Number(match.Groups["minute"].Value) : 0;
        }
        else
        {
            return false;
        }

        if (hour > 23 || minute > 59)
        {
            return false;
        }

        time = new TimeOnly(hour, minute);
        return true;
    }

    /// <summary>
    /// Lê um dia. Com separador — <c>/</c>, <c>-</c> ou <c>.</c> —: dia/mês e o
    /// ano opcional (<c>6/10</c>, <c>06/10/26</c>, <c>06/10/2026</c>). Só dígitos:
    /// 1–2 são o dia, 4 são DDMM, 6 são DDMMAA e 8 são DDMMAAAA. "hoje" e "ontem"
    /// também valem.
    /// </summary>
    /// <remarks>
    /// Sem ano (ou sem mês), vale a ocorrência mais recente que não passa de
    /// <paramref name="today"/>: em 03/01, <c>28/12</c> é o do ano que acabou —
    /// período de trabalho no futuro não existe.
    /// </remarks>
    public static bool TryParseDate(string? text, DateOnly today, out DateOnly date)
    {
        date = default;
        var value = (text ?? string.Empty).Trim();

        if (value.Equals("hoje", StringComparison.OrdinalIgnoreCase))
        {
            date = today;
            return true;
        }

        if (value.Equals("ontem", StringComparison.OrdinalIgnoreCase))
        {
            date = today.AddDays(-1);
            return true;
        }

        string day;
        string? month;
        string? year;

        if (DigitsOnly().IsMatch(value))
        {
            switch (value.Length)
            {
                case <= 2:
                    (day, month, year) = (value, null, null);
                    break;
                case 4:
                    (day, month, year) = (value[..2], value[2..], null);
                    break;
                case 6:
                case 8:
                    (day, month, year) = (value[..2], value[2..4], value[4..]);
                    break;
                default:
                    return false;
            }
        }
        else if (SeparatedDate().Match(value) is { Success: true } match)
        {
            day = match.Groups["day"].Value;
            month = match.Groups["month"].Value;
            year = match.Groups["year"].Success ? match.Groups["year"].Value : null;
        }
        else
        {
            return false;
        }

        return month is null
            ? TryLatestDay(Number(day), today, out date)
            : year is null
                ? TryLatestDayOfMonth(Number(day), Number(month), today, out date)
                : TryDate(Number(day), Number(month), Year(year), out date);
    }

    /// <summary>"08:31" — como o campo fica depois de sair dele.</summary>
    public static string Format(TimeOnly time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"06/10/2026" — como o campo fica depois de sair dele.</summary>
    public static string Format(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    /// <summary>O dia <paramref name="day"/> mais recente até hoje: este mês ou um dos anteriores.</summary>
    private static bool TryLatestDay(int day, DateOnly today, out DateOnly date)
    {
        var month = new DateOnly(today.Year, today.Month, 1);

        // Um dia 31 aparece no máximo dois meses para trás; 12 é folga.
        for (var back = 0; back < 12; back++, month = month.AddMonths(-1))
        {
            if (TryDate(day, month.Month, month.Year, out date) && date <= today)
            {
                return true;
            }
        }

        date = default;
        return false;
    }

    /// <summary>O dia/mês mais recente até hoje: este ano ou um dos anteriores (o 29/02 pode voltar até 8 anos).</summary>
    private static bool TryLatestDayOfMonth(int day, int month, DateOnly today, out DateOnly date)
    {
        for (var year = today.Year; year > today.Year - 9; year--)
        {
            if (TryDate(day, month, year, out date) && date <= today)
            {
                return true;
            }
        }

        date = default;
        return false;
    }

    private static bool TryDate(int day, int month, int year, out DateOnly date)
    {
        date = default;

        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month))
        {
            return false;
        }

        date = new DateOnly(year, month, day);
        return true;
    }

    /// <summary>Dois dígitos são deste século: <c>26</c> é 2026.</summary>
    private static int Year(string digits) => digits.Length == 2 ? 2000 + Number(digits) : Number(digits);

    private static int Number(string digits) => int.Parse(digits, NumberStyles.None, CultureInfo.InvariantCulture);

    [GeneratedRegex("^[0-9]+$")]
    private static partial Regex DigitsOnly();

    [GeneratedRegex(@"^(?<hour>[0-9]{1,2})\s*[:hH.]\s*(?<minute>[0-9]{2})?$")]
    private static partial Regex SeparatedTime();

    [GeneratedRegex(@"^(?<day>[0-9]{1,2})\s*[/.-]\s*(?<month>[0-9]{1,2})(?:\s*[/.-]\s*(?<year>[0-9]{2}|[0-9]{4}))?$")]
    private static partial Regex SeparatedDate();
}
