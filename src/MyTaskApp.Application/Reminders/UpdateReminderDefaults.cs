using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Reminders;

namespace MyTaskApp.Application.Reminders;

/// <summary>
/// Os campos chegam soltos porque é assim que a tela os coleta; quem recusa uma
/// combinação impossível é o <see cref="ReminderPolicy"/>.
/// </summary>
public sealed record UpdateReminderDefaults(
    bool IsEnabled,
    ReminderAnchor Anchor,
    TimeSpan Offset,
    bool RepeatUntilAcknowledged,
    TimeSpan RepeatEvery,
    AlertChannels Channels);

public sealed class UpdateReminderDefaultsHandler(
    IReminderSettingsStore settings,
    IUnitOfWork unitOfWork,
    ILogger<UpdateReminderDefaultsHandler> logger)
{
    public async Task HandleAsync(
        UpdateReminderDefaults command,
        CancellationToken cancellationToken = default)
    {
        var policy = new ReminderPolicy(
            command.IsEnabled,
            command.Anchor,
            command.Offset,
            command.RepeatUntilAcknowledged,
            command.RepeatEvery,
            command.Channels);

        // A pausa é preservada: mexer no padrão não é o mesmo que voltar a ser
        // incomodado agora.
        var current = await settings.GetAsync(cancellationToken);

        // Deliberadamente não reaplica ao que já existe: o padrão vale na
        // criação. Rearmar o banco inteiro porque o usuário mexeu num combo
        // seria genuinamente assustador.
        await settings.SaveAsync(
            current with { DefaultPolicy = policy },
            cancellationToken);

        await unitOfWork.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "ReminderDefaultsUpdated {IsEnabled} {Offset} {RepeatEvery}",
            policy.IsEnabled,
            policy.Offset,
            policy.RepeatEvery);
    }
}
