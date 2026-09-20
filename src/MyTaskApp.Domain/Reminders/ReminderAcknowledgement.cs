namespace MyTaskApp.Domain.Reminders;

/// <summary>
/// O que o usuário fez para o lembrete parar. Mostrar uma notificação não entra
/// nesta lista de propósito: notificado não é atendido.
/// </summary>
public enum ReminderAcknowledgement
{
    Opened = 0,
    MarkedSeen = 1,
    Completed = 2,
}
