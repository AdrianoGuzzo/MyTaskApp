using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Deadlines;

namespace MyTaskApp.Application.Deadlines;

/// <summary>A configuração de prazos, para a tela de ajustes.</summary>
public sealed record GetDeadlineSettings;

public sealed class GetDeadlineSettingsHandler(IDeadlineSettingsStore settings)
{
    public Task<DeadlineSettings> HandleAsync(
        GetDeadlineSettings command,
        CancellationToken cancellationToken = default) =>
        settings.GetAsync(cancellationToken);
}

/// <summary>
/// Grava a configuração de prazos (§19). Vale na hora para toda tarefa em
/// "Padrão": a política é lida viva a cada tique, e nenhuma tarefa precisa ser
/// rearmada (ADR-050).
/// </summary>
public sealed record UpdateDeadlineSettings(
    bool IsEnabled,
    DeadlineAlertStage Stages,
    TimeSpan? OverdueRepeatEvery,
    TimeOnly DefaultTime);

public sealed class UpdateDeadlineSettingsHandler(
    IDeadlineSettingsStore settings,
    IUnitOfWork unitOfWork)
{
    public async Task<DeadlineSettings> HandleAsync(
        UpdateDeadlineSettings command,
        CancellationToken cancellationToken = default)
    {
        // A política valida antes de qualquer gravação.
        var updated = new DeadlineSettings(
            new DeadlineAlertPolicy(command.IsEnabled, command.Stages, command.OverdueRepeatEvery),
            command.DefaultTime);

        await settings.SaveAsync(updated, cancellationToken);
        await unitOfWork.SaveChangesAsync(cancellationToken);

        return updated;
    }
}
