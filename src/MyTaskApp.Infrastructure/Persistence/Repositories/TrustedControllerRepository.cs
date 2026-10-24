using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Remote;
using MyTaskApp.Domain.Remote;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class TrustedControllerRepository(MyTaskAppDbContext context) : ITrustedControllerRepository
{
    public async Task<IReadOnlyList<TrustedController>> ListActiveAsync(Guid controllerDeviceId, CancellationToken cancellationToken = default) =>
        await context.TrustedControllers
            .Where(controller => controller.ControllerDeviceId == controllerDeviceId && controller.RevokedAt == null)
            .ToListAsync(cancellationToken);

    public async Task AddAsync(TrustedController controller, CancellationToken cancellationToken = default) =>
        await context.TrustedControllers.AddAsync(controller, cancellationToken);
}
