using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain.Auditing;

namespace MyTaskApp.Application.Tests.Fakes;

/// <summary>
/// Trilha de auditoria em memória. Guarda as linhas como o adaptador de verdade
/// faria — sem gravar — para que os testes possam afirmar o que foi registrado
/// <b>e</b> que nada foi registrado quando a operação falhou.
/// </summary>
internal sealed class FakeTaskAuditLog : ITaskAuditLog
{
    public List<TaskAuditEntry> Entries { get; } = [];

    public TaskAuditEntry? Last => Entries.Count == 0 ? null : Entries[^1];

    public IReadOnlyList<TaskAuditOperation> Operations =>
        [.. Entries.Select(entry => entry.Operation)];

    public Task RecordAsync(TaskAuditEntry entry, CancellationToken cancellationToken = default)
    {
        Entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<TaskAuditEntry>> GetForTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<TaskAuditEntry>>(
        [
            .. Entries
                .Where(entry => entry.TaskId == taskId)
                .OrderByDescending(entry => entry.OccurredAt)
                .ThenByDescending(entry => entry.Id),
        ]);
}
