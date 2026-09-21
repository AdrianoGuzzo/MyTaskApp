using MyTaskApp.Domain.Lifecycle;

namespace MyTaskApp.Application.Lifecycle;

/// <summary>
/// A configuração de gerenciamento de dados do usuário (§11), em linha única no
/// banco — mesmo desenho do <c>IReminderSettingsStore</c> e pela mesma razão
/// (ADR-014): isto é dado do usuário, e o <c>appsettings.json</c> não é gravável.
/// </summary>
public interface IDataRetentionSettingsStore
{
    Task<DataRetentionPolicy> GetAsync(CancellationToken cancellationToken = default);

    Task SaveAsync(DataRetentionPolicy policy, CancellationToken cancellationToken = default);
}
