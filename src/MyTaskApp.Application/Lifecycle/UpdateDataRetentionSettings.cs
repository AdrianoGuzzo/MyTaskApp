using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Lifecycle;

namespace MyTaskApp.Application.Lifecycle;

/// <summary>
/// Os campos chegam soltos porque é assim que a tela os coleta; quem recusa um
/// prazo impossível é o <see cref="DataRetentionPolicy"/> — mesmo desenho do
/// <c>UpdateReminderDefaults</c>.
/// </summary>
public sealed record UpdateDataRetentionSettings(
    bool AutoArchiveEnabled,
    int AutoArchiveAfterDays,
    int TrashRetentionDays);

public sealed class UpdateDataRetentionSettingsHandler(
    IDataRetentionSettingsStore settings,
    IUnitOfWork unitOfWork,
    ILogger<UpdateDataRetentionSettingsHandler> logger)
{
    public async Task HandleAsync(
        UpdateDataRetentionSettings command,
        CancellationToken cancellationToken = default)
    {
        var policy = new DataRetentionPolicy(
            command.AutoArchiveEnabled,
            command.AutoArchiveAfterDays,
            command.TrashRetentionDays);

        await settings.SaveAsync(policy, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        // Encurtar um prazo não dispara varredura aqui: quem arquiva e quem
        // apaga é a rotina, no próximo tique. Salvar uma configuração não pode
        // ser, ele mesmo, o gesto que faz dados sumirem da tela.
        logger.LogInformation(
            "DataRetentionUpdated {AutoArchiveEnabled} {AutoArchiveAfterDays} {TrashRetentionDays}",
            policy.AutoArchiveEnabled,
            policy.AutoArchiveAfterDays,
            policy.TrashRetentionDays);
    }
}
