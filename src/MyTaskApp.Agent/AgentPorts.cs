using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Reminders;

namespace MyTaskApp.Agent;

/// <summary>
/// A conta do sistema, como o desktop identifica o autor (§5). A origem
/// "Remoto: …" entra por cima, pelo <see cref="OperationOrigin"/>, em cada operação.
/// </summary>
internal sealed class AgentUser : ICurrentUser
{
    private readonly string? _name = ReadName();

    public string? Name => OperationOrigin.Describe(_name);

    private static string? ReadName()
    {
        try
        {
            var name = Environment.UserName;
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>
/// Lembretes são só do desktop (ADR-085): sem tela, não há onde mostrar. O
/// agente não liga o agendador deles; isto só deixa a composição completa.
/// </summary>
internal sealed class NoAlertPresenter(ILogger<NoAlertPresenter> logger) : IAlertPresenter
{
    public Task PresentAsync(ReminderAlert alert, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("AgentReminderIgnored");
        return Task.CompletedTask;
    }

    public Task PresentDigestAsync(ReminderDigest digest, CancellationToken cancellationToken = default)
    {
        logger.LogWarning("AgentReminderIgnored");
        return Task.CompletedTask;
    }

    public Task DismissAsync(Guid occurrenceId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>Sem tela, sem som: quem toca o aviso é o desktop.</summary>
internal sealed class NoSoundPlayer : ISoundPlayer
{
    public void PlayAlert()
    {
    }
}
