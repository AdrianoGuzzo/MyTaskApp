using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Configuration;

namespace MyTaskApp.Application.Planning;

public sealed class UserClock : IUserClock
{
    private readonly TimeProvider _timeProvider;

    public UserClock(
        TimeProvider timeProvider,
        IOptions<ApplicationOptions> options,
        ILogger<UserClock> logger)
    {
        _timeProvider = timeProvider;
        TimeZone = Resolve(options.Value.TimeZoneId, logger);
    }

    public TimeZoneInfo TimeZone { get; }

    public DateOnly Today => DateOnly.FromDateTime(LocalNow.DateTime);

    public TimeOnly CurrentTime => TimeOnly.FromDateTime(LocalNow.DateTime);

    private DateTimeOffset LocalNow => TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), TimeZone);

    public DateOnly ToLocalDate(DateTimeOffset instant) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, TimeZone).DateTime);

    public DateTimeOffset ToInstant(DateOnly date, TimeOnly time)
    {
        var local = date.ToDateTime(time);

        if (TimeZone.IsInvalidTime(local))
        {
            // Hora que não existe (o relógio adiantou): avança para o primeiro
            // instante válido — regra explícita do ADR-002.
            var rule = Array.Find(
                TimeZone.GetAdjustmentRules(),
                candidate => candidate.DateStart <= local && local <= candidate.DateEnd);

            local = local.Add(rule?.DaylightDelta ?? TimeSpan.FromHours(1));
        }

        // Hora ambígua (o relógio atrasou): a primeira ocorrência, que é a de
        // maior offset — o horário de verão. Vale para todo fuso de DST
        // positivo; Irlanda e Lord Howe, de DST negativo, ficam de fora.
        var offset = TimeZone.IsAmbiguousTime(local)
            ? TimeZone.GetAmbiguousTimeOffsets(local).Max()
            : TimeZone.GetUtcOffset(local);

        return new DateTimeOffset(local, offset);
    }

    /// <summary>
    /// Um fuso inválido na configuração degrada para o fuso da máquina: o app
    /// abrir com o fuso errado é melhor do que não abrir.
    /// </summary>
    private static TimeZoneInfo Resolve(string? timeZoneId, ILogger<UserClock> logger)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Local;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception exception) when (
            exception is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            logger.LogWarning(
                exception,
                "UnknownTimeZoneConfigured {TimeZoneId}; usando o fuso da máquina",
                timeZoneId);

            return TimeZoneInfo.Local;
        }
    }
}
