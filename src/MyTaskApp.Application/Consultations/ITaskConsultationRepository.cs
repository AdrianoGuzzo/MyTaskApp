using MyTaskApp.Domain.Consultations;

namespace MyTaskApp.Application.Consultations;

/// <summary>A Consulta Inteligente de cada tarefa (ADR-081). Estreita por necessidade (ADR-005).</summary>
public interface ITaskConsultationRepository
{
    /// <summary>A da tarefa, com as pastas; <c>null</c> se ainda não foi configurada.</summary>
    Task<TaskConsultation?> FindAsync(Guid taskId, CancellationToken cancellationToken = default);

    Task AddAsync(TaskConsultation consultation, CancellationToken cancellationToken = default);

    /// <summary>Tira só a configuração: pastas, bancos e conexões continuam onde estão.</summary>
    void Remove(TaskConsultation consultation);
}
