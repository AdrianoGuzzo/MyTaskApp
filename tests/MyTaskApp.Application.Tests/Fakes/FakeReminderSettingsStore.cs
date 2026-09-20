using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>
/// Guarda a configuração em memória e conta as leituras — é assim que se prova
/// que a captura rápida lê o padrão uma vez só para o lote inteiro.
/// </summary>
internal sealed class FakeReminderSettingsStore(ReminderSettings? initial = null)
    : IReminderSettingsStore
{
    public ReminderSettings Settings { get; set; } = initial ?? ReminderSettings.Factory;

    public int Reads { get; private set; }

    public Task<ReminderSettings> GetAsync(CancellationToken cancellationToken = default)
    {
        Reads++;
        return Task.FromResult(Settings);
    }

    public Task SaveAsync(ReminderSettings settings, CancellationToken cancellationToken = default)
    {
        Settings = settings;
        return Task.CompletedTask;
    }
}

internal static class TestClock
{
    /// <summary>O relógio do usuário sobre um <c>TimeProvider</c> controlado.</summary>
    public static UserClock Over(
        TimeProvider timeProvider,
        string timeZoneId = "America/Sao_Paulo") =>
        new(
            timeProvider,
            Options.Create(new ApplicationOptions { TimeZoneId = timeZoneId }),
            NullLogger<UserClock>.Instance);
}
