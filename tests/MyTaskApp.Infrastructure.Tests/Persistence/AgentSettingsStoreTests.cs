using Microsoft.EntityFrameworkCore;
using MyTaskApp.Infrastructure.Persistence;
using MyTaskApp.Infrastructure.Persistence.Repositories;

namespace MyTaskApp.Infrastructure.Tests.Persistence;

/// <summary>Os parâmetros de cada agente, uma linha por provider (ADR-030).</summary>
public class AgentSettingsStoreTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task NothingSaved_IsNull_SoTheAgentDefaultApplies()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);
        await using var context = db.CreateContext();

        (await new AgentSettingsStore(context).GetArgumentsAsync("claude-code", Ct)).Should().BeNull();
    }

    [Fact]
    public async Task WhatIsSaved_IsWhatComesBack_ForThatAgentOnly()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var write = db.CreateContext())
        {
            await new AgentSettingsStore(write).SaveArgumentsAsync("claude-code", "--model opus", Ct);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var store = new AgentSettingsStore(read);

        (await store.GetArgumentsAsync("claude-code", Ct)).Should().Be("--model opus");
        (await store.GetArgumentsAsync("codex", Ct)).Should().BeNull();
    }

    /// <summary>Vazio é escolha do usuário — abrir sem parâmetro —, e não "sem configuração".</summary>
    [Fact]
    public async Task SavingAgain_UpdatesTheSameRow_AndKeepsAnEmptyText()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        foreach (var arguments in new[] { "--dangerously-skip-permissions", "" })
        {
            await using var write = db.CreateContext();
            await new AgentSettingsStore(write).SaveArgumentsAsync("claude-code", arguments, Ct);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();

        (await read.AgentSettings.CountAsync(Ct)).Should().Be(1);
        (await new AgentSettingsStore(read).GetArgumentsAsync("claude-code", Ct)).Should().BeEmpty();
    }

    /// <summary>
    /// A linha pode nascer só pelo acompanhamento (ADR-036): os parâmetros
    /// continuam "nunca salvos", e o padrão do agente continua valendo.
    /// </summary>
    [Fact]
    public async Task Monitoring_IsSavedOnItsOwn_WithoutFreezingTheArguments()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var write = db.CreateContext())
        {
            await new AgentSettingsStore(write).SaveMonitoringAsync("claude-code", false, Ct);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();
        var store = new AgentSettingsStore(read);

        (await store.GetMonitoringAsync("claude-code", Ct)).Should().BeFalse();
        (await store.GetArgumentsAsync("claude-code", Ct)).Should().BeNull();
        (await store.GetMonitoringAsync("codex", Ct)).Should().BeNull();
    }

    /// <summary>Os dois no mesmo "Iniciar": uma linha só, não duas com a mesma chave.</summary>
    [Fact]
    public async Task ArgumentsAndMonitoring_InTheSameSave_ShareTheRow()
    {
        await using var db = await new TempSqliteDatabase().MigrateAsync(Ct);

        await using (var write = db.CreateContext())
        {
            var store = new AgentSettingsStore(write);
            await store.SaveArgumentsAsync("claude-code", "--model opus", Ct);
            await store.SaveMonitoringAsync("claude-code", true, Ct);
            await write.SaveChangesAsync(Ct);
        }

        await using var read = db.CreateContext();

        (await read.AgentSettings.CountAsync(Ct)).Should().Be(1);
        (await new AgentSettingsStore(read).GetArgumentsAsync("claude-code", Ct)).Should().Be("--model opus");
        (await new AgentSettingsStore(read).GetMonitoringAsync("claude-code", Ct)).Should().BeTrue();
    }
}
