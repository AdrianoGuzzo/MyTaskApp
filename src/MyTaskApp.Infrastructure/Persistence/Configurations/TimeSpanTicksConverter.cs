using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

/// <summary>
/// O padrão do EF no SQLite guarda <see cref="TimeSpan"/> como TEXT "hh:mm:ss":
/// desordenado e intraduzível em comparação — exatamente o erro que o ADR-011 já
/// documenta para <see cref="DateTimeOffset"/>. Ticks resolvem os dois.
/// </summary>
internal static class TimeSpanTicksConverter
{
    public static readonly ValueConverter<TimeSpan, long> Instance = new(
        duration => duration.Ticks,
        ticks => TimeSpan.FromTicks(ticks));
}
