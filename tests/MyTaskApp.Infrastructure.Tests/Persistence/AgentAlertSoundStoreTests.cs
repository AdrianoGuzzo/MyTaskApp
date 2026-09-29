using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Sounds;
using MyTaskApp.Domain.Agents;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>O som de cada estado do agente, uma linha por estado (ADR-042).</summary>
public class AgentAlertSoundStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NothingSaved_ComesBackEmpty_SoTheDefaultsApply()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await using var context = db.CreateContext();

        (await new AgentAlertSoundStore(context).GetAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task WhatIsSaved_IsWhatComesBack()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var write = db.CreateContext())
        {
            var store = new AgentAlertSoundStore(write);
            await store.SaveAsync(new AgentAlertSound(AgentActivity.WaitingForUser, true, "custom:gongo.mp3"), Ct);
            await store.SaveAsync(new AgentAlertSound(AgentActivity.Failed, false, BuiltInSounds.Bell), Ct);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();

        (await new AgentAlertSoundStore(read).GetAsync(Ct)).Should().Equal(
            new AgentAlertSound(AgentActivity.WaitingForUser, true, "custom:gongo.mp3"),
            new AgentAlertSound(AgentActivity.Failed, false, BuiltInSounds.Bell));
    }

    /// <summary>Duas mudanças no mesmo <c>SaveChanges</c> não inserem a mesma chave duas vezes.</summary>
    [Fact]
    public async Task SavingTheSameStateAgain_UpdatesItsRow()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var write = db.CreateContext())
        {
            var store = new AgentAlertSoundStore(write);
            await store.SaveAsync(new AgentAlertSound(AgentActivity.WaitingReview, true, BuiltInSounds.Bell), Ct);
            await store.SaveAsync(new AgentAlertSound(AgentActivity.WaitingReview, false, BuiltInSounds.Pop), Ct);
            await write.SaveChangesAsync(Ct);
        }

        await using (var write = db.CreateContext())
        {
            await new AgentAlertSoundStore(write).SaveAsync(
                new AgentAlertSound(AgentActivity.WaitingReview, true, BuiltInSounds.Soft),
                Ct);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();

        (await new AgentAlertSoundStore(read).GetAsync(Ct)).Should().Equal(
            new AgentAlertSound(AgentActivity.WaitingReview, true, BuiltInSounds.Soft));
    }
}
