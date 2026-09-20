using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Application.Reminders;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

public class ReminderSettingsStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task OnAFreshInstall_TheDefaultsAreTheFactoryOnes()
    {
        // Sem linha semeada por migration: o padrão vem de um lugar só.
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await using var context = db.CreateContext();

        var settings = await Store(context).GetAsync(Ct);

        settings.Should().Be(ReminderSettings.Factory);
        settings.DefaultPolicy.Should().Be(ReminderPolicy.Default);
    }

    [Fact]
    public async Task WhatIsSaved_IsWhatComesBack()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var pausedUntil = new DateTimeOffset(2026, 9, 17, 16, 0, 0, TimeSpan.Zero);
        var settings = new ReminderSettings(ReminderPolicy.Urgent, pausedUntil);

        await using (var write = db.CreateContext())
        {
            await Store(write).SaveAsync(settings, Ct);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();

        (await Store(read).GetAsync(Ct)).Should().Be(settings);
    }

    [Fact]
    public async Task SavingTwice_KeepsASingleRow()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        foreach (var policy in new[] { ReminderPolicy.Urgent, ReminderPolicy.None })
        {
            await using var write = db.CreateContext();
            await Store(write).SaveAsync(new ReminderSettings(policy, null), Ct);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();

        (await read.ReminderSettings.CountAsync(Ct)).Should().Be(1);
        (await Store(read).GetAsync(Ct)).DefaultPolicy.Should().Be(ReminderPolicy.None);
    }

    [Fact]
    public async Task ThePause_SurvivesReopeningTheDatabase()
    {
        // Reiniciar o app é justamente quando o usuário mais corre risco de ser
        // incomodado por algo que ele mandou calar.
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        var pausedUntil = new DateTimeOffset(2026, 9, 17, 16, 0, 0, TimeSpan.Zero);

        await using (var write = db.CreateContext())
        {
            await Store(write).SaveAsync(
                new ReminderSettings(ReminderPolicy.Default, pausedUntil), Ct);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var settings = await Store(read).GetAsync(Ct);

        settings.PausedUntilUtc.Should().Be(pausedUntil);
        settings.IsPausedAt(pausedUntil.AddMinutes(-1)).Should().BeTrue();
        settings.IsPausedAt(pausedUntil.AddMinutes(1)).Should().BeFalse();
    }

    [Fact]
    public async Task AnInvalidStoredPolicy_DegradesToTheFactoryDefault()
    {
        // Banco editado à mão não pode impedir o app de abrir.
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var write = db.CreateContext())
        {
            await write.Database.ExecuteSqlRawAsync(
                """
                INSERT INTO ReminderSettings
                    (Id, IsEnabled, Anchor, Offset, RepeatUntilAcknowledged, RepeatEvery, Channels, PausedUntilUtc)
                VALUES (1, 1, 0, 0, 1, 0, 0, NULL)
                """,
                Ct);
        }

        await using var read = db.CreateContext();

        (await Store(read).GetAsync(Ct)).Should().Be(ReminderSettings.Factory);
    }

    [Fact]
    public async Task ASecondRow_IsRefusedByTheDatabase()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await using var context = db.CreateContext();

        var insert = async () => await context.Database.ExecuteSqlRawAsync(
            """
            INSERT INTO ReminderSettings
                (Id, IsEnabled, Anchor, Offset, RepeatUntilAcknowledged, RepeatEvery, Channels, PausedUntilUtc)
            VALUES (2, 1, 0, 0, 1, 0, 7, NULL)
            """,
            Ct);

        await insert.Should().ThrowAsync<Exception>();
    }

    private static ReminderSettingsStore Store(MyTaskAppDbContext context) =>
        new(context, NullLogger<ReminderSettingsStore>.Instance);
}
