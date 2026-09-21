using MyTaskApp.Domain.Lifecycle;

namespace MyTaskApp.Application.Lifecycle;

/// <summary>A seção "Gerenciamento de dados" das configurações (§11).</summary>
public sealed record GetDataRetentionSettings;

public sealed class GetDataRetentionSettingsHandler(IDataRetentionSettingsStore settings)
{
    public Task<DataRetentionPolicy> HandleAsync(
        GetDataRetentionSettings command,
        CancellationToken cancellationToken = default) =>
        settings.GetAsync(cancellationToken);
}
