using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace MyTaskApp.Infrastructure.Persistence;

/// <summary>
/// Usado só pelas ferramentas de migration (dotnet ef), para que elas não
/// precisem iniciar o aplicativo desktop.
/// </summary>
internal sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<MyTaskAppDbContext>
{
    public MyTaskAppDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<MyTaskAppDbContext>()
            .UseSqlite("Data Source=design-time.db")
            .Options);
}
