using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// O que acontece quando o usuário instala a versão nova por cima da antiga.
/// O instalador troca binários e vai embora; quem encontra o banco de ontem é a
/// aplicação, no primeiro start, por <c>MigrateAsync</c> (ADR-005).
/// <para>
/// Estes testes exercitam exatamente esse momento — banco populado, processo
/// novo subindo — contra SQLite de verdade, porque perder dados num upgrade é a
/// única falha desta entrega que o usuário não consegue desfazer.
/// </para>
/// </summary>
public class UpgradePreservationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 17, 17, 30, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static TaskItem ATaskWithHistory() =>
        TaskItem.Create(
            "Renovar o contrato",
            Now,
            "Não pode passar do fim do mês.",
            TaskPriority.High,
            TaskSchedule.At(new DateOnly(2026, 9, 18), new TimeOnly(9, 0)));

    [Fact]
    public async Task ASecondStartOnAPopulatedDatabase_KeepsEveryTask()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var task = ATaskWithHistory();

        await using (var write = db.CreateContext())
        {
            write.Tasks.Add(task);
            await write.SaveChangesAsync(Ct);
        }

        // A versão nova subindo sobre o banco que já estava lá.
        await db.MigrateAsync(Ct);

        await using var read = db.CreateContext();
        var loaded = await read.Tasks.Include(item => item.Occurrences).SingleAsync(Ct);

        loaded.Id.Should().Be(task.Id);
        loaded.Title.Should().Be("Renovar o contrato");
        loaded.Occurrences.Should().ContainSingle();
    }

    [Fact]
    public async Task AnUpgradeAppliesNoMigrationTwice()
    {
        // __EFMigrationsHistory é o que impede a InitialSchema de rodar de novo
        // contra um banco cheio. Se ele se perdesse, o upgrade estouraria.
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        string[] before;

        await using (var first = db.CreateContext())
        {
            before = [.. await first.Database.GetAppliedMigrationsAsync(Ct)];
        }

        await db.MigrateAsync(Ct);

        await using var second = db.CreateContext();
        var after = await second.Database.GetAppliedMigrationsAsync(Ct);

        after.Should().Equal(before);
        (await second.Database.GetPendingMigrationsAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheUsersReminderPreferencesSurviveTheUpgrade()
    {
        // ADR-014: o padrão de lembrete é dado do usuário e mora no banco. É
        // justamente o tipo de ajuste que sumiria se o instalador mexesse lá.
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var chosen = new ReminderSettings(ReminderPolicy.Urgent, PausedUntilUtc: null);

        await using (var write = db.CreateContext())
        {
            await new ReminderSettingsStore(write, NullLogger<ReminderSettingsStore>.Instance)
                .SaveAsync(chosen, Ct);
            await write.SaveChangesAsync(Ct);
        }

        await db.MigrateAsync(Ct);

        await using var read = db.CreateContext();
        var restored = await new ReminderSettingsStore(read, NullLogger<ReminderSettingsStore>.Instance)
            .GetAsync(Ct);

        restored.Should().Be(chosen);
        restored.DefaultPolicy.Should().Be(ReminderPolicy.Urgent);
    }

    [Fact]
    public async Task InstallingOverAnExistingVersion_NeverStartsFromAnEmptyDatabase()
    {
        // O sintoma clássico de um instalador que apaga dados: o app abre
        // funcionando, mas "vazio". Aqui isso falharia a build.
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var write = db.CreateContext())
        {
            write.Tasks.Add(ATaskWithHistory());
            write.Tasks.Add(TaskItem.Create("Comprar pão", Now));
            await write.SaveChangesAsync(Ct);
        }

        await db.MigrateAsync(Ct);
        await db.MigrateAsync(Ct);

        await using var read = db.CreateContext();

        (await read.Tasks.CountAsync(Ct)).Should().Be(2);
    }

    [Fact]
    public async Task TheDatabaseIsCreatedOutsideTheInstallDirectory()
    {
        // Onde o initializer realmente escreve — não onde a configuração diz
        // que escreveria. É o caminho que um upgrade precisa não alcançar.
        var directory = Path.Combine(
            Path.GetTempPath(), $"mytaskapp-upgrade-{Guid.CreateVersion7():N}");

        try
        {
            var options = new DatabaseOptions { Directory = directory, FileName = "app.db" };

            await using var context = new MyTaskAppDbContext(
                new DbContextOptionsBuilder<MyTaskAppDbContext>()
                    .UseSqlite(options.BuildConnectionString())
                    .Options);

            await new DatabaseInitializer(
                context,
                Options.Create(options),
                NullLogger<DatabaseInitializer>.Instance).InitializeAsync(Ct);

            var path = options.ResolveFullPath();

            File.Exists(path).Should().BeTrue();
            path.Should().NotStartWith(AppContext.BaseDirectory);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
