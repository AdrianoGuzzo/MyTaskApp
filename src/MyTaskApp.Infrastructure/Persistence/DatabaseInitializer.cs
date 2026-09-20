using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace MyTaskApp.Infrastructure.Persistence;

internal sealed class DatabaseInitializer(
    MyTaskAppDbContext context,
    IOptions<DatabaseOptions> options,
    ILogger<DatabaseInitializer> logger) : IDatabaseInitializer
{
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var path = options.Value.ResolveFullPath();
        var directory = Path.GetDirectoryName(path);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await context.Database.MigrateAsync(cancellationToken);

        logger.LogInformation("DatabaseReady {DatabasePath}", path);
    }
}
