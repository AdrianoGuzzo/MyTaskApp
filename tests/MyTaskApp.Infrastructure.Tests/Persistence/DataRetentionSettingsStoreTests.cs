using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using MyTaskApp.Domain.Lifecycle;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>
/// A configuração de retenção em linha única (§11), no mesmo desenho do
/// <c>ReminderSettingsStore</c> (ADR-014).
/// </summary>
public class DataRetentionSettingsStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static DataRetentionSettingsStore StoreOver(MyTaskAppDbContext context) =>
        new(context, NullLogger<DataRetentionSettingsStore>.Instance);

    /// <summary>
    /// Instalação nova não semeia linha nenhuma: semear um singleton mutável por
    /// migration vira armadilha no dia em que o padrão mudar.
    /// </summary>
    [Fact]
    public async Task AFreshInstall_ReadsTheFactoryDefaultWithoutSeedingARow()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await using var context = db.CreateContext();

        var policy = await StoreOver(context).GetAsync(Ct);

        policy.Should().Be(DataRetentionPolicy.Factory);
        policy.AutoArchiveEnabled.Should().BeFalse();
        (await context.DataRetentionSettings.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task WhatIsSaved_IsWhatComesBack()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var write = db.CreateContext())
        {
            await StoreOver(write).SaveAsync(new DataRetentionPolicy(true, 90, 7), Ct);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var policy = await StoreOver(read).GetAsync(Ct);

        policy.AutoArchiveEnabled.Should().BeTrue();
        policy.AutoArchiveAfterDays.Should().Be(90);
        policy.TrashRetentionDays.Should().Be(7);
    }

    [Fact]
    public async Task SavingTwice_UpdatesTheSameRowInsteadOfAddingASecondOne()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var first = db.CreateContext())
        {
            await StoreOver(first).SaveAsync(new DataRetentionPolicy(true, 30, 30), Ct);
            await first.SaveChangesAsync(Ct);
        }

        await using (var second = db.CreateContext())
        {
            await StoreOver(second).SaveAsync(new DataRetentionPolicy(false, 60, 15), Ct);
            await second.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();

        (await read.DataRetentionSettings.CountAsync(Ct)).Should().Be(1);
        (await StoreOver(read).GetAsync(Ct)).TrashRetentionDays.Should().Be(15);
    }

    /// <summary>
    /// Preferência de usuário duplicada é uma dessas que só aparece quando já
    /// está errada há meses. Quem impede é o banco.
    /// </summary>
    [Fact]
    public async Task TheDatabaseRefusesASecondRow()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await using var context = db.CreateContext();

        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO DataRetentionSettings "
            + "(Id, AutoArchiveEnabled, AutoArchiveAfterDays, TrashRetentionDays) "
            + "VALUES (1, 0, 30, 30);",
            Ct);

        var second = async () => await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO DataRetentionSettings "
            + "(Id, AutoArchiveEnabled, AutoArchiveAfterDays, TrashRetentionDays) "
            + "VALUES (2, 0, 30, 30);",
            Ct);

        await second.Should().ThrowAsync<SqliteException>();
    }

    /// <summary>
    /// Degrada, não derruba — e aqui degradar é a opção segura: o padrão de
    /// fábrica tem o arquivamento automático desligado, então uma linha
    /// corrompida nunca vira uma varredura inesperada.
    /// </summary>
    [Fact]
    public async Task ACorruptedRow_FallsBackToTheFactoryDefaultInsteadOfThrowing()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await using var context = db.CreateContext();

        await context.Database.ExecuteSqlRawAsync(
            "INSERT INTO DataRetentionSettings "
            + "(Id, AutoArchiveEnabled, AutoArchiveAfterDays, TrashRetentionDays) "
            + "VALUES (1, 1, 0, 0);",
            Ct);

        var policy = await StoreOver(context).GetAsync(Ct);

        policy.Should().Be(DataRetentionPolicy.Factory);
    }
}
