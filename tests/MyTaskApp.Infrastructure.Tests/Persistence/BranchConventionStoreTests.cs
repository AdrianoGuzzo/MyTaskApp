using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.External;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

public class BranchConventionStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static BranchConventionStore Store(MyTaskAppDbContext context) =>
        new(context, NullLogger<BranchConventionStore>.Instance);

    [Fact]
    public async Task NothingSaved_GivesTheFactoryConventions()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await using var context = db.CreateContext();

        (await Store(context).GetAsync(Ct)).Should().Be(BranchConventions.Default);
    }

    [Fact]
    public async Task TheUsersConventions_SurviveTheRoundTrip()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var write = db.CreateContext())
        {
            await Store(write).SaveAsync(BranchConventions.Parse("Bug = fix/{id}\nStory = feat/{id}-{slug}"), Ct);
            await write.SaveChangesAsync(Ct);
        }

        await using (var write = db.CreateContext())
        {
            // Gravar de novo atualiza a mesma linha: a tabela só tem uma.
            await Store(write).SaveAsync(BranchConventions.Parse("Bug = fix/{id}"), Ct);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var conventions = await Store(read).GetAsync(Ct);

        conventions.PatternFor("Bug").Should().Be("fix/{id}");
        conventions.Patterns.Should().ContainSingle();
    }

    [Fact]
    public async Task ACorruptedRow_DegradesToTheFactoryConventions()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var write = db.CreateContext())
        {
            await write.Database.ExecuteSqlRawAsync(
                "INSERT INTO BranchSettings (Id, Conventions) VALUES (1, 'isto não é convenção')", Ct);
        }

        await using var read = db.CreateContext();
        (await Store(read).GetAsync(Ct)).Should().Be(BranchConventions.Default);
    }
}
