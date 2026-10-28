using System.Globalization;
using MyTaskApp.Domain.Deadlines;

namespace MyTaskApp.Application.TimeTracking;

/// <summary>
/// Os rótulos de data do relatório de horas (ADR-097). Os nomes dos meses são
/// fixos, como os dias da semana do <see cref="DeadlineFormatter"/>: a interface
/// é pt-BR mesmo num Windows em inglês.
/// </summary>
public static class ReportCalendar
{
    private static readonly string[] MonthNames =
    [
        "janeiro", "fevereiro", "março", "abril", "maio", "junho",
        "julho", "agosto", "setembro", "outubro", "novembro", "dezembro",
    ];

    /// <summary>"outubro".</summary>
    public static string MonthName(int month) => MonthNames[month - 1];

    /// <summary>"out", como no eixo do gráfico.</summary>
    public static string MonthAbbreviation(int month) => MonthNames[month - 1][..3];

    /// <summary>"outubro de 2026".</summary>
    public static string Month(DateOnly date) =>
        string.Create(CultureInfo.InvariantCulture, $"{MonthName(date.Month)} de {date.Year}");

    /// <summary>"ter, 14/10".</summary>
    public static string ShortDay(DateOnly date) =>
        $"{DeadlineFormatter.WeekdayAbbreviation(date)}, {date.ToString("dd/MM", CultureInfo.InvariantCulture)}";

    /// <summary>"ter, 14/10/2026".</summary>
    public static string LongDay(DateOnly date) =>
        $"{DeadlineFormatter.WeekdayAbbreviation(date)}, {date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)}";

    /// <summary>"1 lançamento", "4 lançamentos".</summary>
    public static string Entries(int count) =>
        count == 1 ? "1 lançamento" : string.Create(CultureInfo.InvariantCulture, $"{count} lançamentos");

    /// <summary>"1 dia", "18 dias".</summary>
    public static string Days(int count) =>
        count == 1 ? "1 dia" : string.Create(CultureInfo.InvariantCulture, $"{count} dias");
}
