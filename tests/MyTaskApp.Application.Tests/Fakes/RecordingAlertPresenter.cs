using MyTaskApp.Application.Reminders;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>
/// Apresentador que só anota o que foi pedido. É o que permite provar a
/// funcionalidade inteira sem levantar Avalonia: o degrau da escada, a ordem
/// "marcar → salvar → apresentar" e o que acontece quando apresentar falha.
/// </summary>
internal sealed class RecordingAlertPresenter : IAlertPresenter
{
    public List<ReminderAlert> Presented { get; } = [];

    public List<ReminderDigest> Digests { get; } = [];

    public List<Guid> Dismissed { get; } = [];

    /// <summary>Roda dentro do <c>PresentAsync</c>, para inspecionar o estado no momento do aviso.</summary>
    public Action<ReminderAlert>? OnPresent { get; set; }

    /// <summary>Faz o próximo aviso desta ocorrência falhar, como uma janela que não abre.</summary>
    public Guid? FailFor { get; set; }

    public Task PresentAsync(ReminderAlert alert, CancellationToken cancellationToken = default)
    {
        OnPresent?.Invoke(alert);

        if (FailFor == alert.OccurrenceId)
        {
            return Task.FromException(new InvalidOperationException("A janela não abriu."));
        }

        Presented.Add(alert);
        return Task.CompletedTask;
    }

    public Task PresentDigestAsync(
        ReminderDigest digest,
        CancellationToken cancellationToken = default)
    {
        Digests.Add(digest);
        return Task.CompletedTask;
    }

    public Task DismissAsync(Guid occurrenceId, CancellationToken cancellationToken = default)
    {
        Dismissed.Add(occurrenceId);
        return Task.CompletedTask;
    }
}

internal sealed class RecordingSoundPlayer : ISoundPlayer
{
    public int Played { get; private set; }

    public void PlayAlert() => Played++;
}
