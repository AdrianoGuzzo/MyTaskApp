using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Application.Planning;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Deadlines;
using MyTaskApp.Domain.Devices;
using MyTaskApp.Domain.External;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.StickyNotes;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;
using MyTaskApp.Infrastructure.Persistence.Repositories;
using MyTaskApp.Infrastructure.Sync.Apply;
using MyTaskApp.Infrastructure.Sync.Capture;
using MyTaskApp.Infrastructure.Sync.Persistence;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Infrastructure.Tests.Sync;

/// <summary>
/// O que chega do servidor entrando no SQLite (ADR-061): dois bancos, A e B.
/// O outbox de A é a página que B aplica.
/// </summary>
public sealed class RemoteChangeApplierTests : IAsyncLifetime
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
    public async Task EverySharedField_ArrivesAsItLeft_ForEveryType()
    {
        await SeedEverythingAsync(_a);

        await _b.ApplyAsync(await _a.PageFromOutboxAsync(Ct), Ct);

        var sent = await _a.ProjectAllAsync(Ct);
        var arrived = await _b.ProjectAllAsync(Ct);

        arrived.Keys.Should().BeEquivalentTo(sent.Keys);
        sent.Select(pair => pair.Key.Type).Distinct().Should().HaveCount(SyncRegistry.All.Count, "a semente cobre todos os tipos");

        foreach (var (key, fields) in sent)
        {
            foreach (var (field, value) in fields)
            {
                SyncValue.AreEqual(arrived[key][field], value).Should().BeTrue($"{key.Type}.{field}: {value} ≠ {arrived[key][field]}");
            }
        }
    }

    [Fact]
    public async Task WhatIsLocal_GetsTheLocalDefault_AndNeverTheOtherMachinesValue()
    {
        await SeedEverythingAsync(_a);

        await _b.ApplyAsync(await _a.PageFromOutboxAsync(Ct), Ct);

        await using var read = _b.Database.CreateContext();
        (await read.TagDirectories.SingleAsync(Ct)).Path.Should().BeNull("o caminho de A não vale em B");
        var development = await read.TaskDevelopments.SingleAsync(Ct);
        development.Status.Should().Be(TaskDevelopmentStatus.NotCreatedHere);
        development.RepositoryPath.Should().BeEmpty();
        development.WorktreePath.Should().BeEmpty();
        var note = await read.StickyNotes.SingleAsync(Ct);
        note.IsOpen.Should().BeFalse("um post-it de outro computador não abre janela aqui");
        note.Width.Should().Be(StickyNote.DefaultWidth);
    }

    [Fact]
    public async Task ApplyingGeneratesNoOperation_AndTheNextLocalEditCarriesTheServerVersion()
    {
        var task = TaskItem.Create("Pagar boleto", Now);
        await _a.SaveAsync(context => context.Add(task), Ct);

        await _b.ApplyAsync(await _a.PageFromOutboxAsync(Ct, version: 4), Ct);
        (await _b.OutboxAsync(Ct)).Should().BeEmpty();
        _b.Trigger.Signals.Should().Be(0);

        await using (var context = _b.CreateContext())
        {
            var loaded = await new TaskItemRepository(context).FindByIdAsync(task.Id, Ct);
            loaded!.ChangePriority(TaskPriority.Urgent);
            await context.SaveChangesAsync(Ct);
        }

        var operation = (await _b.OutboxAsync(Ct)).Should().ContainSingle().Subject;
        operation.BaseVersion.Should().Be(4);
        SyncCaptureKit.Fields(operation).Keys.Should().Equal(SyncFields.Task.Priority);
    }

    [Fact]
    public async Task Placement_AnOldClientsEditKeepsIt_AndOnlyAnExplicitNullClearsIt()
    {
        var task = TaskItem.Create("Pagar boleto", Now, placement: DevicePlacement.On(_a.Device.DeviceId));
        await _a.SaveAsync(context => context.Add(task), Ct);
        await _b.ApplyAsync(await _a.PageFromOutboxAsync(Ct), Ct);

        var current = await _a.ProjectAllAsync(Ct);

        // Um servidor de antes do ADR-062 não conhece o campo e não o devolve.
        var withoutPlacement = WithAllTaskFields(
            Change(SyncEntityTypes.Task, task.Id, 2, new() { [SyncFields.Task.Title] = SyncValue.String("Pagar o boleto") }),
            current,
            task.Id);
        var legacyFields = new Dictionary<string, JsonElement>(withoutPlacement.Fields);
        legacyFields.Remove(SyncFields.Task.Placement);
        await _b.ApplyAsync([withoutPlacement with { Fields = legacyFields }], Ct, cursor: "c2");
        (await PlacementInBAsync(task.Id)).Should().Be(DevicePlacement.On(_a.Device.DeviceId));

        await _b.ApplyAsync(
            [WithAllTaskFields(Change(SyncEntityTypes.Task, task.Id, 3, new() { [SyncFields.Task.Placement] = SyncValue.Null }), current, task.Id)],
            Ct,
            cursor: "c3");
        (await PlacementInBAsync(task.Id)).Should().BeNull();
    }

    [Fact]
    public async Task AMoveFromAnotherComputer_IsReported_AndRearmsTheReminder()
    {
        var task = TaskItem.Create("Pagar boleto", Now, placement: DevicePlacement.On(_a.Device.DeviceId));
        await _a.SaveAsync(context => context.Add(task), Ct);
        await _b.ApplyAsync(await _a.PageFromOutboxAsync(Ct), Ct);
        var current = await _a.ProjectAllAsync(Ct);
        var sentToB = SyncValue.Object(
            (SyncFields.Placement.Kind, SyncValue.Integer((long)PlacementKind.Device)),
            (SyncFields.Placement.DeviceId, SyncValue.Guid(_b.Device.DeviceId)));

        var result = await _b.ApplyAsync(
            [WithAllTaskFields(
                Change(SyncEntityTypes.Task, task.Id, 2, new() { [SyncFields.Task.Placement] = sentToB }) with { UpdatedByDeviceId = _a.Device.DeviceId },
                current,
                task.Id)],
            Ct,
            cursor: "c2");

        var move = result.Moves.Should().ContainSingle().Subject;
        move.Before.Should().Be(DevicePlacement.On(_a.Device.DeviceId));
        move.After.Should().Be(DevicePlacement.On(_b.Device.DeviceId));
        move.ByDeviceId.Should().Be(_a.Device.DeviceId);
        move.IsNew.Should().BeFalse();
        result.RearmTaskIds.Should().Contain(task.Id);
    }

    private async Task<DevicePlacement?> PlacementInBAsync(Guid taskId)
    {
        await using var read = _b.Database.CreateContext();
        return (await read.Tasks.AsNoTracking().SingleAsync(task => task.Id == taskId, Ct)).Placement;
    }

    [Fact]
    public async Task TheCursorAdvancesWithThePage()
    {
        await _a.SaveAsync(context => context.Add(Tag.Create("Casa", "#00FF00", Now)), Ct);

        await _b.ApplyAsync(await _a.PageFromOutboxAsync(Ct), Ct, cursor: "abc");

        await using var read = _b.Database.CreateContext();
        (await read.SyncState.SingleAsync(Ct)).Cursor.Should().Be("abc");
    }

    [Fact]
    public async Task AFailureInTheMiddleOfThePage_RollsBackTheDataAndTheCursor()
    {
        await _a.SaveAsync(context => context.Add(TaskItem.Create("Pagar boleto", Now)), Ct);
        var page = await _a.PageFromOutboxAsync(Ct);
        page.Add(BrokenTag());

        var applying = () => _b.ApplyAsync(page, Ct, cursor: "depois");

        await applying.Should().ThrowAsync<RemoteValueException>();

        await using var read = _b.Database.CreateContext();
        (await read.Tasks.CountAsync(Ct)).Should().Be(0);
        (await read.SyncEntityStates.CountAsync(Ct)).Should().Be(0);
        (await read.SyncState.CountAsync(Ct)).Should().Be(0, "o cursor fica onde estava");
    }

    [Fact]
    public async Task InIsolation_ABadRowIsMarked_AndTheRestOfThePageGoesIn()
    {
        await _a.SaveAsync(context => context.Add(TaskItem.Create("Pagar boleto", Now)), Ct);
        var page = await _a.PageFromOutboxAsync(Ct);
        var broken = BrokenTag();
        page.Insert(0, broken);

        var result = await _b.ApplyAsync(page, Ct, cursor: "depois", isolate: true);

        result.Failures.Should().ContainSingle(failure => failure.EntityId == broken.EntityId);
        await using var read = _b.Database.CreateContext();
        (await read.Tasks.CountAsync(Ct)).Should().Be(1);
        (await read.Tags.CountAsync(Ct)).Should().Be(0);
        (await read.SyncEntityStates.SingleAsync(state => state.EntityId == broken.EntityId, Ct)).ApplyErrorCode
            .Should().Be(SyncErrorCodes.ValidationFailed);
        (await read.SyncState.SingleAsync(Ct)).Cursor.Should().Be("depois");
    }

    [Fact]
    public async Task ATombstone_DeletesWithItsChildren_AndAnOlderVersionDoesNotBringItBack()
    {
        var task = TaskItem.Create("Pagar boleto", Now);
        await _a.SaveAsync(context => context.Add(task), Ct);
        var created = await _a.PageFromOutboxAsync(Ct);
        await _b.ApplyAsync(created, Ct);

        await _b.ApplyAsync([Tombstone(SyncEntityTypes.Task, task.Id, version: 2)], Ct);
        await _b.ApplyAsync(created, Ct, cursor: "velho");

        await using var read = _b.Database.CreateContext();
        (await read.Tasks.CountAsync(Ct)).Should().Be(0);
        (await read.Occurrences.CountAsync(Ct)).Should().Be(0, "a cascata do banco leva a ocorrência");
        var state = await read.SyncEntityStates.SingleAsync(state => state.EntityId == task.Id, Ct);
        state.IsDeleted.Should().BeTrue();
        state.ServerVersion.Should().Be(2);
    }

    [Fact]
    public async Task AFieldWithAPendingLocalEdit_KeepsTheLocalValue()
    {
        var task = TaskItem.Create("Pagar boleto", Now);
        await _a.SaveAsync(context => context.Add(task), Ct);
        await _b.ApplyAsync(await _a.PageFromOutboxAsync(Ct), Ct);

        await using (var context = _b.CreateContext())
        {
            var loaded = await new TaskItemRepository(context).FindByIdAsync(task.Id, Ct);
            loaded!.Rename("Pagar boleto da luz (B)");
            await context.SaveChangesAsync(Ct);
        }

        var remote = Change(SyncEntityTypes.Task, task.Id, version: 2, new()
        {
            [SyncFields.Task.Title] = SyncValue.String("Pagar boleto (A)"),
            [SyncFields.Task.Priority] = SyncValue.Integer((long)TaskPriority.Urgent),
        });
        await _b.ApplyAsync([WithAllTaskFields(remote, await _a.ProjectAllAsync(Ct), task.Id)], Ct);

        await using var read = _b.Database.CreateContext();
        var stored = await read.Tasks.SingleAsync(Ct);
        stored.Title.Should().Be("Pagar boleto da luz (B)", "a edição local ainda vai ao servidor, que decide");
        stored.Priority.Should().Be(TaskPriority.Urgent);
    }

    [Fact]
    public async Task ATombstoneOverAPendingLocalEdit_LeavesAVisibleConflict()
    {
        var task = TaskItem.Create("Pagar boleto", Now);
        await _a.SaveAsync(context => context.Add(task), Ct);
        await _b.ApplyAsync(await _a.PageFromOutboxAsync(Ct), Ct);

        await using (var context = _b.CreateContext())
        {
            var loaded = await new TaskItemRepository(context).FindByIdAsync(task.Id, Ct);
            loaded!.Rename("Editado offline em B");
            await context.SaveChangesAsync(Ct);
        }

        await _b.ApplyAsync([Tombstone(SyncEntityTypes.Task, task.Id, version: 2)], Ct);

        await using var read = _b.Database.CreateContext();
        (await read.SyncOutbox.CountAsync(Ct)).Should().Be(0);
        var conflict = await read.SyncConflicts.SingleAsync(Ct);
        conflict.Reason.Should().Be(SyncErrorCodes.EntityDeleted);
        conflict.Field.Should().Be(SyncFields.Task.Title);
        conflict.LocalValue.Should().Contain("Editado offline em B");
    }

    [Fact]
    public async Task ARemoteDirectoryUpdate_NeverTouchesTheLocalMapping()
    {
        var tag = Tag.Create("ECO", "#22C55E", Now);
        var directory = tag.AddDirectory("@core", @"C:\Projects\core", null, null, Now);
        await _a.SaveAsync(context => context.Add(tag), Ct);
        await _b.ApplyAsync(await _a.PageFromOutboxAsync(Ct), Ct);

        await using (var context = _b.CreateContext())
        {
            var loaded = await new TagRepository(context).FindByIdAsync(tag.Id, Ct);
            loaded!.UpdateDirectory(directory.Id, "@core", @"D:\Code\core", null, null);
            await context.SaveChangesAsync(Ct);
        }

        (await _b.OutboxAsync(Ct)).Should().BeEmpty("mapear a pasta é só local");

        var renamed = (await _a.ProjectAllAsync(Ct))[(SyncEntityTypes.TagDirectory, directory.Id)];
        renamed[SyncFields.TagDirectory.Alias] = SyncValue.String("@nucleo");
        await _b.ApplyAsync([Change(SyncEntityTypes.TagDirectory, directory.Id, 2, renamed)], Ct);

        await using var read = _b.Database.CreateContext();
        var stored = await read.TagDirectories.SingleAsync(Ct);
        stored.Alias.Should().Be("@nucleo");
        stored.Path.Should().Be(@"D:\Code\core");
    }

    [Fact]
    public async Task OnTheFirstConnection_ATagWithTheSameName_BecomesOne_AndTheDifferenceIsRecorded()
    {
        var localTag = Tag.Create("casa", "#111111", Now);
        var directory = localTag.AddDirectory("@scripts", @"C:\scripts", null, null, Now);
        var task = TaskItem.Create("Varrer", Now);
        task.SetTags([localTag.Id]);
        _b.Switch.Set(false);
        await _b.SaveAsync(context => context.AddRange(localTag, task), Ct);
        _b.Switch.Set(true);

        var remoteTag = Tag.Create("Casa", "#00FF00", Now);
        await _a.SaveAsync(context => context.Add(remoteTag), Ct);

        await _b.ApplyAsync(await _a.PageFromOutboxAsync(Ct), Ct);

        await using var read = _b.Database.CreateContext();
        var tag = await read.Tags.SingleAsync(Ct);
        tag.Id.Should().Be(remoteTag.Id);
        tag.Name.Should().Be("Casa");
        tag.ColorHex.Should().Be("#00FF00");
        (await read.TaskItemTags.SingleAsync(Ct)).TagId.Should().Be(remoteTag.Id);
        var mergedDirectory = await read.TagDirectories.SingleAsync(Ct);
        mergedDirectory.Id.Should().Be(directory.Id);
        mergedDirectory.TagId.Should().Be(remoteTag.Id);
        mergedDirectory.Path.Should().Be(@"C:\scripts");

        // O nome difere só na caixa, a cor de verdade: as duas ficam registradas.
        var conflicts = await read.SyncConflicts.ToListAsync(Ct);
        conflicts.Should().OnlyContain(conflict => conflict.Reason == SyncConflictReasons.MergedOnJoin && conflict.EntityId == remoteTag.Id);
        conflicts.Select(conflict => conflict.Field).Should().BeEquivalentTo(SyncFields.Tag.Name, SyncFields.Tag.ColorHex);
        conflicts.Single(conflict => conflict.Field == SyncFields.Tag.ColorHex).LocalValue.Should().Contain("#111111");
    }

    [Fact]
    public async Task AfterApplying_TheConclusionIsRecomputed_AndANewPendingOccurrenceGetsItsReminder()
    {
        var reminder = new ReminderPolicy(true, ReminderAnchor.AfterCreation, TimeSpan.FromHours(1), false, TimeSpan.FromMinutes(15), AlertChannels.Notification);
        var done = TaskItem.Create("Feita", Now);
        done.CompleteOccurrence(done.Occurrences.Single().Id, Now.AddMinutes(5));
        var open = TaskItem.Create("Aberta", Now, reminder: reminder);
        await _a.SaveAsync(context => context.AddRange(done, open), Ct);

        var result = await _b.ApplyAsync(await _a.PageFromOutboxAsync(Ct), Ct);

        await using (var context = _b.CreateContext())
        {
            var handler = new ReconcileAfterRemoteChangeHandler(
                new TaskItemRepository(context),
                new EfUnitOfWork(context),
                new UserClock(_b.Clock, Options.Create(new ApplicationOptions { TimeZoneId = "UTC" }), NullLogger<UserClock>.Instance),
                _b.Clock);
            await handler.HandleAsync(new ReconcileAfterRemoteChange(result.TaskIds, result.RearmTaskIds), Ct);
        }

        await using var read = _b.Database.CreateContext();
        (await read.Tasks.SingleAsync(item => item.Id == done.Id, Ct)).ConcludedAt.Should().Be(Now.AddMinutes(5));
        (await read.Occurrences.SingleAsync(item => item.TaskItemId == open.Id, Ct)).Reminder.IsArmed.Should().BeTrue();
        (await _b.OutboxAsync(Ct)).Should().BeEmpty("recalcular o que é local não volta para o servidor");
    }

    [Fact]
    public async Task TheFirstScan_SendsWhatExisted_AndOnlyOnce()
    {
        _a.Switch.Set(false);
        await SeedEverythingAsync(_a);
        _a.Switch.Set(true);

        await using (var context = _a.CreateContext())
        {
            (await _a.Reconciler(context).ScanAsync(Ct)).Should().BeGreaterThan(0);
        }

        var outbox = await _a.OutboxAsync(Ct);
        outbox.Select(row => (row.EntityType, row.EntityId)).Should().OnlyHaveUniqueItems();
        outbox.Select(row => row.EntityType).Distinct().Should().HaveCount(SyncRegistry.All.Count);
        outbox.Select(row => SyncRegistry.Find(row.EntityType)!.Rank).Should().BeInAscendingOrder("pai antes do filho");
        (await _a.StatesAsync(Ct)).Should().OnlyContain(state => state.CreatedByDeviceId == _a.Device.DeviceId);

        await using (var context = _a.CreateContext())
        {
            (await _a.Reconciler(context).ScanAsync(Ct)).Should().Be(0);
        }
    }

    [Fact]
    public async Task WhatCameFromTheServer_IsNotSentBackByTheScan()
    {
        await _a.SaveAsync(context => context.Add(TaskItem.Create("Pagar boleto", Now)), Ct);
        await _b.ApplyAsync(await _a.PageFromOutboxAsync(Ct), Ct);

        await using var context = _b.CreateContext();
        (await _b.Reconciler(context).ScanAsync(Ct)).Should().Be(0);
    }

    /// <summary>Um pouco de tudo: cada tipo do contrato, com os compostos e as listas preenchidos.</summary>
    private static async Task SeedEverythingAsync(SyncCaptureKit kit)
    {
        var tag = Tag.Create("ECO", "#22C55E", Now, DevicePlacement.Everywhere);
        var directory = tag.AddDirectory("@core", @"C:\Projects\core", "Core", "API", Now, "develop");
        var command = DevelopmentCommand.Create(
            "@run",
            "dotnet run --launch-profile {profile}",
            "Sobe a API",
            new DevelopmentCommandSettings(
                "Executar",
                CommandMode.Terminal,
                "src/Api",
                KeepTerminalOpen: false,
                RequiresConfirmation: true,
                [new CommandParameterSpec("profile", "Perfil", CommandParameterType.Choice, "Dev", true, ["Dev", "Prod"])]),
            Now);
        command.SetSharing(true);
        tag.AddDirectoryCommand(directory.Id, command.Id, Now);

        var task = TaskItem.Create(
            "Implementar sincronização",
            Now,
            "Com outbox",
            TaskPriority.High,
            TaskSchedule.At(new DateOnly(2026, 10, 14), new TimeOnly(9, 30)),
            new ReminderPolicy(true, ReminderAnchor.AfterCreation, TimeSpan.FromMinutes(30), true, TimeSpan.FromMinutes(10), AlertChannels.All),
            DevicePlacement.On(kit.Device.DeviceId));
        task.SetTags([tag.Id]);
        task.ChangePlan("Escrever o interceptor", TimeSpan.FromMinutes(90));
        task.LinkExternal(ExternalLink.Create("jira", "ECO-42", "Sync", "https://eco.atlassian.net/browse/ECO-42", "Story", "Doing", Now));
        task.SetOccurrenceDeadline(task.Occurrences.Single().Id, new TaskDeadline(new DateOnly(2026, 10, 20), new TimeOnly(18, 0)), DeadlineAlertStage.None);
        var development = task.BeginDevelopment(null, @"C:\Projects\core", "origin/develop", "feature/sync", @"C:\Projects\core-sync", Now, "v1.2.0");
        task.SetDevelopmentCommands(development.Id, ["dotnet restore", "dotnet build"], Now);
        task.SetDevelopmentAgentPrompt(development.Id, "Implemente o outbox");

        var entry = TimeEntry.Manual(task.Occurrences.Single().Id, Now.AddHours(-2), Now.AddHours(-1), "Desenho", Now);

        var note = StickyNote.Create(Now);
        note.Edit("Ligar para o banco", Now);
        note.ChangeAppearance(tag.Id, StickyNoteColorMode.Palette, StickyNotePaletteColor.Blue, StickyNoteEmphasis.Attention, 85);
        note.Place(10, 20, 300, 240);

        await kit.SaveAsync(context => context.AddRange(command, tag, task, entry, note), Ct);
    }

    private static SyncChange BrokenTag() => Change(SyncEntityTypes.Tag, Guid.CreateVersion7(), 1, new()
    {
        [SyncFields.Tag.Name] = SyncValue.String(new string('x', 41)),
        [SyncFields.Tag.ColorHex] = SyncValue.String("#000000"),
        [SyncFields.CreatedAt] = SyncValue.Instant(Now),
    });

    private static SyncChange Change(string type, Guid id, long version, Dictionary<string, JsonElement> fields) =>
        new(type, id, version, false, fields, null, null, Now, null, Now);

    private static SyncChange Tombstone(string type, Guid id, long version) =>
        new(type, id, version, true, new Dictionary<string, JsonElement>(), null, null, null, null, Now);

    /// <summary>O pull traz o estado inteiro: os campos da mudança sobre os que já valiam.</summary>
    private static SyncChange WithAllTaskFields(
        SyncChange change,
        Dictionary<(string Type, Guid Id), Dictionary<string, JsonElement>> current,
        Guid taskId)
    {
        var full = new Dictionary<string, JsonElement>(current[(SyncEntityTypes.Task, taskId)]);

        foreach (var (field, value) in change.Fields)
        {
            full[field] = value;
        }

        return change with { Fields = full };
    }
}
