namespace MyTaskApp.Desktop.Reports;

/// <summary>
/// O eixo de horas do gráfico (ADR-097): o topo e o passo em números
/// "redondos" — 2h, 4h, 6h, 8h, e não 1,7h, 3,4h —, com no máximo
/// <see cref="MaxLines"/> linhas de grade.
/// </summary>
internal static class ChartAxis
{
    public const int MaxLines = 5;

    /// <summary>Os passos possíveis, em horas: de 15 minutos aos anos de milhares de horas.</summary>
    private static readonly double[] Steps =
        [0.25, 0.5, 1, 2, 4, 5, 10, 20, 25, 50, 100, 200, 250, 500, 1000, 2000, 2500, 5000, 10000];

    /// <summary>O gráfico sem horas mostra uma jornada: 0 a 8h, de 2 em 2.</summary>
    public static (double Top, double Step) For(double maxHours)
    {
        if (maxHours <= 0)
        {
            return (8, 2);
        }

        foreach (var step in Steps)
        {
            var lines = Math.Ceiling(maxHours / step);

            if (lines <= MaxLines)
            {
                return (lines * step, step);
            }
        }

        var last = Steps[^1];
        return (Math.Ceiling(maxHours / last) * last, Math.Ceiling(maxHours / last / MaxLines) * last);
    }
}
