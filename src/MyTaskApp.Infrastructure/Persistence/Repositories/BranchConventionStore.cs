using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.External;
using MyTaskApp.Domain;

namespace MyTaskApp.Infrastructure.Persistence.Repositories;

internal sealed class BranchConventionStore(
    MyTaskAppDbContext context,
    ILogger<BranchConventionStore> logger) : IBranchConventionStore
{
    public async Task<BranchConventions> GetAsync(CancellationToken cancellationToken = default)
    {
        var row = await context.BranchSettings
            .AsNoTracking()
            .FirstOrDefaultAsync(settings => settings.Id == BranchSettingsRow.SingletonId, cancellationToken);

        if (row is null)
        {
            // Sem linha, as de fábrica — sem semear nada (ADR-014).
            return BranchConventions.Default;
        }

        try
        {
            return BranchConventions.Parse(row.Conventions);
        }
        catch (DomainException exception)
        {
            // Degrada, não derruba: uma linha editada à mão no banco não pode
            // impedir a aba Desenvolvimento de abrir.
            logger.LogWarning(exception, "InvalidBranchConventionsStored");
            return BranchConventions.Default;
        }
    }

    public async Task SaveAsync(BranchConventions conventions, CancellationToken cancellationToken = default)
    {
        var row = await context.BranchSettings.FirstOrDefaultAsync(
            stored => stored.Id == BranchSettingsRow.SingletonId,
            cancellationToken);

        if (row is null)
        {
            row = new BranchSettingsRow();
            await context.BranchSettings.AddAsync(row, cancellationToken);
        }

        row.Conventions = conventions.ToText();
    }
}
