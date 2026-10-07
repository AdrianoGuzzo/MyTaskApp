using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain.Auditing;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;

namespace MyTaskApp.Application.Tasks;

/// <summary>
/// O caminho de toda tarefa criada uma a uma: política de lembrete padrão,
/// lembrete armado, registro no repositório e auditoria <c>Created</c>. Um lugar
/// só para "criar tarefa" e "post-it vira tarefa" (ADR-054) não divergirem — o
/// segundo nasceria sem lembrete no dia em que o primeiro ganhasse uma regra.
/// </summary>
/// <remarks>Não grava: o <c>SaveChanges</c> é de quem chama, na mesma transação do resto.</remarks>
internal static class TaskCreation
{
    public static async Task<TaskItem> AddAsync(
        ITaskItemRepository tasks,
        IReminderSettingsStore settings,
        ITaskAuditLog audit,
        ICurrentUser currentUser,
        IUserClock clock,
        DateTimeOffset createdAt,
        ReminderPolicy? reminder,
        Func<ReminderPolicy, TaskItem> build,
        CancellationToken cancellationToken)
    {
        // Sem politica explicita vale o padrao do usuario: e o que faz
        // "crio e nao preciso mais olhar" funcionar sem configurar nada.
        var policy = reminder ?? (await settings.GetAsync(cancellationToken)).DefaultPolicy;

        var task = build(policy);

        // Armar antes de salvar: o lembrete entra na mesma transacao da tarefa.
        ReminderArming.Arm(task, task.Occurrences.Single(), clock, createdAt);

        await tasks.AddAsync(task, cancellationToken);

        await audit.RecordAsync(
            TaskAuditEntry.ByUser(
                task.Id,
                task.Title,
                TaskAuditOperation.Created,
                createdAt,
                currentUser.Name),
            cancellationToken);

        return task;
    }
}
