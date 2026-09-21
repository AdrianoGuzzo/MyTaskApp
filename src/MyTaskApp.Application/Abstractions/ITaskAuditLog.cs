using MyTaskApp.Domain.Auditing;

namespace MyTaskApp.Application.Abstractions;

/// <summary>
/// A trilha de auditoria do ciclo de vida (§8). Interface estreita, como as
/// demais (ADR-005): registrar e ler o histórico de um checklist é tudo que o
/// app precisa.
/// </summary>
/// <remarks>
/// <b>Registrar não salva.</b> A linha entra na mesma unidade de trabalho da
/// operação auditada, e é o caso de uso que fecha a transação — é isso que
/// impede a existência de um arquivamento sem registro, ou de um registro de
/// algo que acabou não acontecendo.
/// </remarks>
public interface ITaskAuditLog
{
    Task RecordAsync(TaskAuditEntry entry, CancellationToken cancellationToken = default);

    /// <summary>
    /// O histórico de um checklist, do mais recente para o mais antigo.
    /// Continua respondendo depois da exclusão definitiva: as linhas não
    /// dependem do registro do checklist.
    /// </summary>
    Task<IReadOnlyList<TaskAuditEntry>> GetForTaskAsync(
        Guid taskId,
        CancellationToken cancellationToken = default);
}
