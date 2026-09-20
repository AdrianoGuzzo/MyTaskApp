namespace MyTaskApp.Domain.Reminders;

/// <summary>
/// Formas de chamar atenção que o usuário liga ou desliga. A ordem em que são
/// usadas é da escada (<see cref="ReminderEscalation"/>), não desta escolha:
/// desligar um canal apenas faz o degrau correspondente ser pulado.
/// </summary>
[Flags]
public enum AlertChannels
{
    None = 0,
    Notification = 1,
    Sound = 2,
    BringToFront = 4,

    All = Notification | Sound | BringToFront,
}
