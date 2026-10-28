using System.Globalization;
using System.Text;
using MyTaskApp.Domain.TimeTracking;

namespace MyTaskApp.Application.TimeTracking;

/// <summary>
/// O relatório de horas como planilha (ADR-097): um bloco por linha, no
/// formato que o Excel em pt-BR abre direto — ponto e vírgula entre colunas e
/// vírgula decimal. Os blocos sem horas entram; os futuros, não.
/// </summary>
public static class HoursHistoryCsv
{
    private static readonly CultureInfo Brazil = CultureInfo.GetCultureInfo("pt-BR");

    public static string Build(HoursHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        var text = new StringBuilder();
        text.Append("Período;Início;Fim;Horas;Duração;Lançamentos;% do total\r\n");

        foreach (var bucket in history.Buckets.Where(bucket => !bucket.IsFuture))
        {
            var share = history.Total > TimeSpan.Zero ? bucket.Total / history.Total * 100 : 0;

            text.Append(Field(bucket.LongLabel)).Append(';')
                .Append(bucket.From.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(';')
                .Append(bucket.To.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).Append(';')
                .Append(bucket.Total.TotalHours.ToString("0.00", Brazil)).Append(';')
                .Append(WorkTimeFormatter.Duration(bucket.Total)).Append(';')
                .Append(bucket.EntryCount.ToString(CultureInfo.InvariantCulture)).Append(';')
                .Append(share.ToString("0.0", Brazil))
                .Append("\r\n");
        }

        return text.ToString();
    }

    /// <summary>"horas-2026-10-por-dia.csv", "horas-2026-por-mes.csv", "horas-por-ano.csv".</summary>
    public static string SuggestedFileName(HoursHistory history)
    {
        ArgumentNullException.ThrowIfNull(history);

        return history.Scale switch
        {
            HoursScale.Day => string.Create(CultureInfo.InvariantCulture, $"horas-{history.From:yyyy-MM}-por-dia.csv"),
            HoursScale.Month => string.Create(CultureInfo.InvariantCulture, $"horas-{history.From.Year}-por-mes.csv"),
            _ => "horas-por-ano.csv",
        };
    }

    /// <summary>Entre aspas só quando precisa, dobrando as de dentro (RFC 4180).</summary>
    private static string Field(string value) =>
        value.AsSpan().IndexOfAny(";\"\r\n") < 0 ? value : $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
}
