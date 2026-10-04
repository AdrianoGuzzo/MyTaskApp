using MyTaskApp.Domain.Deadlines;

namespace MyTaskApp.Application.Deadlines;

/// <summary>
/// A configuração de prazos do usuário (§19): os avisos globais e o horário que
/// um prazo recebe quando só o dia é escolhido.
/// </summary>
public sealed record DeadlineSettings(DeadlineAlertPolicy Alerts, TimeOnly DefaultTime)
{
    /// <summary>O fim do expediente: "entregar sexta" quase sempre quer dizer isto.</summary>
    public static readonly TimeOnly FactoryDefaultTime = new(18, 0);

    /// <summary>O que uma instalação nova recebe, sem semear linha nenhuma (ADR-014).</summary>
    public static DeadlineSettings Factory { get; } = new(DeadlineAlertPolicy.Default, FactoryDefaultTime);
}

public interface IDeadlineSettingsStore
{
    Task<DeadlineSettings> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DeadlineSettings settings, CancellationToken cancellationToken = default);
}
