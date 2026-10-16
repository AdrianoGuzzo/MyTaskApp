using Microsoft.EntityFrameworkCore;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Infrastructure.Tests.Sync;

/// <summary>
/// Comando global só deste computador, a menos que compartilhado (ADR-066):
/// nada vai nem vem do que não é compartilhado, os botões de diretório seguem
/// o comando, e o apelido de um comando local não é tomado pelo que chega.
/// </summary>
public sealed class CommandSharingSyncTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SyncCaptureKit.Now;

    private SyncCaptureKit _a = null!;

    private SyncCaptureKit _b = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _a = await SyncCaptureKit.CreateAsync(Ct);
        _b = await SyncCaptureKit.CreateAsync(Ct);
    }

    public async ValueTask DisposeAsync()
    {
        await _a.DisposeAsync();
        await _b.DisposeAsync();
    }

    [Fact]
    public async Task ANewGlobalCommand_StaysHere_WithItsButtons_UntilShared()
    {
        var (command, _) = await SeedAsync(_a, shared: false);

        var outbox = await _a.OutboxAsync(Ct);
        outbox.Select(row => row.EntityType).Should().NotContain([SyncEntityTypes.Command, SyncEntityTypes.DirectoryCommand]);
        outbox.Should().Contain(row => row.EntityType == SyncEntityTypes.TagDirectory);
        await _a.AcknowledgeAllAsync(Ct);

        await EditCommandAsync(_a, command.Id, loaded => loaded.SetSharing(true));

        (await _a.OutboxAsync(Ct)).Select(row => row.EntityType)
            .Should().Equal(SyncEntityTypes.Command, SyncEntityTypes.DirectoryCommand);
    }

    [Fact]
    public async Task ADirectoryCommand_AlwaysTravels()
    {
        var tag = Tag.Create("ECO", "#22C55E", Now);
        var directory = tag.AddDirectory("@core", @"C:\Projects\core", null, null, Now);
        var command = DevelopmentCommand.CreateForDirectory(directory.Id, "dotnet run", null, DevelopmentCommandSettings.Default with { Name = "Executar" }, Now);

        await _a.SaveAsync(context => context.AddRange(tag, command), Ct);

        command.IsShared.Should().BeTrue();
        (await _a.OutboxAsync(Ct)).Should().Contain(row => row.EntityType == SyncEntityTypes.Command && row.EntityId == command.Id);
    }

    [Fact]
    public async Task Unsharing_SendsNothingMore_AndDropsWhatHadNotLeft()
    {
        var (command, link) = await SeedAsync(_a, shared: true);
        await _a.AcknowledgeAllAsync(Ct);
        await EditCommandAsync(_a, command.Id, loaded => loaded.Update("@run", "dotnet run -c Release", null, Now));
        (await _a.OutboxAsync(Ct)).Should().ContainSingle();

        await EditCommandAsync(_a, command.Id, loaded => loaded.SetSharing(false));
        await EditCommandAsync(_a, command.Id, loaded => loaded.Update("@run", "dotnet run --no-build", null, Now));

        (await _a.OutboxAsync(Ct)).Should().BeEmpty();
        var states = await _a.StatesAsync(Ct);
        states.Single(state => state.EntityId == command.Id).IsDetached.Should().BeTrue();
        states.Single(state => state.EntityId == link.Id).IsDetached.Should().BeTrue();
    }

    [Fact]
    public async Task DeletingAnUnsharedCommand_SendsNoDelete_NotEvenForItsButtons()
    {
        var (command, _) = await SeedAsync(_a, shared: true);
        await _a.AcknowledgeAllAsync(Ct);
        await EditCommandAsync(_a, command.Id, loaded => loaded.SetSharing(false));

        await using (var context = _a.CreateContext())
        {
            // Como o caso de uso: os botões saem pela cascata do banco, sem o tracker ver.
            context.DevelopmentCommands.Remove(await context.DevelopmentCommands.Include(loaded => loaded.Parameters).SingleAsync(Ct));
            await context.SaveChangesAsync(Ct);
        }

        (await _a.OutboxAsync(Ct)).Should().BeEmpty();

        await using (var context = _a.CreateContext())
        {
            (await _a.Reconciler(context).ScanAsync(Ct)).Should().Be(0);
        }
    }

    [Fact]
    public async Task DeletingASharedCommand_StillLeavesEveryComputer()
    {
        var (command, _) = await SeedAsync(_a, shared: true);
        await _a.AcknowledgeAllAsync(Ct);

        await using (var context = _a.CreateContext())
        {
            context.DevelopmentCommands.Remove(await context.DevelopmentCommands.Include(loaded => loaded.Parameters).SingleAsync(Ct));
            await context.SaveChangesAsync(Ct);
        }

        (await _a.OutboxAsync(Ct)).Should().ContainSingle(row => row.EntityId == command.Id && row.Kind == SyncOperationKind.Delete);
    }

    [Fact]
    public async Task SharingAgain_SendsTheWholeCommand_OnTheLastVersionTheServerShowed()
    {
        var (command, link) = await SeedAsync(_a, shared: true);
        await _a.AcknowledgeAllAsync(Ct);
        await EditCommandAsync(_a, command.Id, loaded => loaded.SetSharing(false));
        await EditCommandAsync(_a, command.Id, loaded => loaded.Update("@run", "dotnet run --no-build", null, Now));

        await EditCommandAsync(_a, command.Id, loaded => loaded.SetSharing(true));

        var outbox = await _a.OutboxAsync(Ct);
        outbox.Select(row => row.EntityId).Should().Equal(command.Id, link.Id);
        outbox.Should().OnlyContain(row => row.Kind == SyncOperationKind.Upsert && row.BaseVersion == 1);
        var fields = SyncCaptureKit.Fields(outbox[0]);
        fields.Keys.Should().BeEquivalentTo(SyncRegistry.Command.Fields.Select(field => field.Name));
        fields[SyncFields.Command.Text].GetString().Should().Be("dotnet run --no-build");
        (await _a.StatesAsync(Ct)).Should().OnlyContain(state => !state.IsDetached);
    }

    [Fact]
    public async Task ACommandFromAnotherComputer_ArrivesShared()
    {
        await SeedAsync(_a, shared: true);

        await _b.ApplyAsync(await _a.PageFromOutboxAsync(Ct), Ct);

        await using var context = _b.CreateContext();
        (await context.DevelopmentCommands.SingleAsync(Ct)).IsShared.Should().BeTrue();
    }

    [Fact]
    public async Task AnUnsharedCommand_IgnoresEditsAndDeletesFromElsewhere_ButKeepsPace()
    {
        var (command, link) = await SeedAsync(_a, shared: true);
        var page = await _a.PageFromOutboxAsync(Ct);
        await _b.ApplyAsync(page, Ct);
        await EditCommandAsync(_b, command.Id, loaded => loaded.SetSharing(false));

        var edited = page.Single(change => change.EntityId == command.Id);
        var fields = new Dictionary<string, System.Text.Json.JsonElement>(edited.Fields)
        {
            [SyncFields.Command.Text] = SyncValue.String("rm -rf bin"),
        };
        await _b.ApplyAsync([edited with { Version = 2, Fields = fields }], Ct, cursor: "c2");
        await _b.ApplyAsync([Tombstone(SyncEntityTypes.DirectoryCommand, link.Id, 2)], Ct, cursor: "c3");
        await _b.ApplyAsync([Tombstone(SyncEntityTypes.Command, command.Id, 3)], Ct, cursor: "c4");

        await using (var context = _b.CreateContext())
        {
            (await context.DevelopmentCommands.SingleAsync(Ct)).Command.Should().Be("dotnet run");
            (await context.TagDirectoryCommands.CountAsync(Ct)).Should().Be(1);
        }

        var states = await _b.StatesAsync(Ct);
        states.Single(state => state.EntityId == command.Id).Should().Match<Infrastructure.Sync.Persistence.SyncEntityStateRow>(
            state => state.ServerVersion == 3 && !state.IsDeleted && state.IsDetached);
        states.Single(state => state.EntityId == link.Id).ServerVersion.Should().Be(2);
    }

    [Fact]
    public async Task AButtonCreatedElsewhere_ForACommandThisComputerUnshared_StaysOut()
    {
        var (command, _) = await SeedAsync(_a, shared: true);
        await _b.ApplyAsync(await _a.PageFromOutboxAsync(Ct), Ct);
        await EditCommandAsync(_b, command.Id, loaded => loaded.SetSharing(false));

        var tag = Tag.Create("APP", "#3B82F6", Now);
        var directory = tag.AddDirectory("@app", @"C:\Projects\app", null, null, Now);
        await _a.AcknowledgeAllAsync(Ct);
        await _a.SaveAsync(context => context.Add(tag), Ct);
        await using (var context = _a.CreateContext())
        {
            var loaded = await context.Tags.Include(candidate => candidate.Directories).ThenInclude(candidate => candidate.Commands)
                .SingleAsync(candidate => candidate.Id == tag.Id, Ct);
            loaded.AddDirectoryCommand(directory.Id, command.Id, Now);
            await context.SaveChangesAsync(Ct);
        }

        var result = await _b.ApplyAsync(await _a.PageFromOutboxAsync(Ct, version: 2), Ct, cursor: "c2");

        result.Failures.Should().BeEmpty();
        await using var check = _b.CreateContext();
        (await check.TagDirectoryCommands.CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task ASharedCommand_WithTheAliasOfALocalOne_Waits_AndTheLocalOneStaysAsItIs()
    {
        var local = DevelopmentCommand.Create("@run", "./run-local.ps1", null, Now);
        await _b.SaveAsync(context => context.Add(local), Ct);
        var (remote, link) = await SeedAsync(_a, shared: true);
        var page = await _a.PageFromOutboxAsync(Ct);

        var result = await _b.ApplyAsync(page, Ct);

        result.Failures.Should().BeEmpty();
        await using (var context = _b.CreateContext())
        {
            var only = await context.DevelopmentCommands.SingleAsync(Ct);
            only.Id.Should().Be(local.Id);
            only.Alias.Should().Be("@run");
            only.Command.Should().Be("./run-local.ps1");
            only.IsShared.Should().BeFalse();
        }

        var states = await _b.StatesAsync(Ct);
        var waiting = states.Single(state => state.EntityId == remote.Id);
        waiting.ApplyErrorCode.Should().Be(SyncWaitingCodes.LocalAliasTaken);
        waiting.ApplyError.Should().Contain("@run").And.Contain("Compartilhar entre computadores");
        states.Single(state => state.EntityId == link.Id).ApplyErrorCode.Should().Be(SyncWaitingCodes.WaitingCommand);
        states.Should().NotContain(state => state.EntityId == local.Id);

        await using (var context = _b.CreateContext())
        {
            (await _b.Reconciler(context).ScanAsync(Ct)).Should().Be(0, "o que espera nunca existiu aqui, e o local não sai");
        }

        // Renomeado o daqui, a busca de novo traz o de lá inteiro.
        await EditCommandAsync(_b, local.Id, loaded => loaded.Update("@run-local", loaded.Command, null, Now));
        result = await _b.ApplyAsync(page, Ct, cursor: null, isolate: true);

        result.Failures.Should().BeEmpty();
        await using (var context = _b.CreateContext())
        {
            (await context.DevelopmentCommands.Select(command => command.Alias).ToListAsync(Ct))
                .Should().BeEquivalentTo(["@run", "@run-local"]);
            (await context.TagDirectoryCommands.SingleAsync(Ct)).DevelopmentCommandId.Should().Be(remote.Id);
        }

        (await _b.StatesAsync(Ct)).Should().OnlyContain(state => state.ApplyErrorCode == null);
    }

    [Fact]
    public async Task AWaitingCommand_DeletedElsewhere_StopsWaiting()
    {
        await _b.SaveAsync(context => context.Add(DevelopmentCommand.Create("@run", "./run-local.ps1", null, Now)), Ct);
        var (remote, _) = await SeedAsync(_a, shared: true);
        await _b.ApplyAsync(await _a.PageFromOutboxAsync(Ct), Ct);

        await _b.ApplyAsync([Tombstone(SyncEntityTypes.Command, remote.Id, 2)], Ct, cursor: "c2");

        (await _b.StatesAsync(Ct)).Single(state => state.EntityId == remote.Id).ApplyErrorCode.Should().BeNull();
    }

    /// <summary>Uma etiqueta com diretório e o comando @run ligado a ele.</summary>
    private static async Task<(DevelopmentCommand Command, TagDirectoryCommand Link)> SeedAsync(SyncCaptureKit kit, bool shared)
    {
        var tag = Tag.Create("ECO", "#22C55E", Now);
        var directory = tag.AddDirectory("@core", @"C:\Projects\core", null, null, Now);
        var command = DevelopmentCommand.Create("@run", "dotnet run", null, Now);
        command.SetSharing(shared);
        var link = tag.AddDirectoryCommand(directory.Id, command.Id, Now);

        await kit.SaveAsync(context => context.AddRange(command, tag), Ct);

        return (command, link);
    }

    private static async Task EditCommandAsync(SyncCaptureKit kit, Guid id, Action<DevelopmentCommand> edit)
    {
        await using var context = kit.CreateContext();
        var loaded = await context.DevelopmentCommands.Include(command => command.Parameters).SingleAsync(command => command.Id == id, Ct);
        edit(loaded);
        await context.SaveChangesAsync(Ct);
    }

    private static SyncChange Tombstone(string type, Guid id, long version) =>
        new(type, id, version, true, new Dictionary<string, System.Text.Json.JsonElement>(), null, null, null, null, Now);
}
