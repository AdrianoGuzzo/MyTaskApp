using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace MyTaskApp.Infrastructure.Persistence.Configurations;

/// <summary>
/// O SQLite não tem tipo de data nativo: um <see cref="DateTimeOffset"/> viraria
/// TEXT com offset embutido, e o EF se recusa a traduzir comparações sobre ele.
/// Guardar ticks em UTC torna a coluna ordenável, indexável e sem ambiguidade de
/// fuso. O preço é uma coluna menos legível ao abrir o banco na mão.
/// </summary>
internal static class UtcInstantConverter
{
    public static readonly ValueConverter<DateTimeOffset, long> Instance = new(
        instant => instant.ToUniversalTime().Ticks,
        ticks => new DateTimeOffset(ticks, TimeSpan.Zero));
}
