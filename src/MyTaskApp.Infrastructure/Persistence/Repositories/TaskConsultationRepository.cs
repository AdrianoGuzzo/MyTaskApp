using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Consultations;
using MyTaskApp.Domain.Consultations;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class TaskConsultationRepository(MyTaskAppDbContext context) : ITaskConsultationRepository
{
    public Task<TaskConsultation?> FindAsync(Guid taskId, CancellationToken cancellationToken = default) =>
        context.TaskConsultations
            .Include(consultation => consultation.Sources.OrderBy(source => source.Position))
            .SingleOrDefaultAsync(consultation => consultation.TaskItemId == taskId, cancellationToken);

    public async Task AddAsync(TaskConsultation consultation, CancellationToken cancellationToken = default) =>
        await context.TaskConsultations.AddAsync(consultation, cancellationToken);

    public void Remove(TaskConsultation consultation) => context.TaskConsultations.Remove(consultation);
}
