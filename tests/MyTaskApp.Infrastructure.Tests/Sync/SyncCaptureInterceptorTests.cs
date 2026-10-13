using Microsoft.EntityFrameworkCore;
using MyTaskApp.Domain.Reminders;
using MyTaskApp.Domain.StickyNotes;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Domain.TimeTracking;
using MyTaskApp.Infrastructure.Persistence.Repositories;
using MyTaskApp.Infrastructure.Sync.Capture;
using MyTaskApp.Infrastructure.Sync.Persistence;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Infrastructure.Tests.Sync;

/// <summary>
/// A captura em outbox (ADR-060): cada SaveChanges de negócio vira operação no
/// mesmo SaveChanges, só com os campos compartilhados que mudaram.
/// </summary>
public sealed class SyncCaptureInterceptorTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SyncCaptureKit.Now;

    private SyncCaptureKit _kit = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _kit = await SyncCaptureKit.CreateAsync(Ct);

    public ValueTask DisposeAsync() => _kit.DisposeAsync();

    [Fact]
    public async Task AnInstallationThatNeverConnected_WritesNoSyncRowAtAll()
    {
        _kit.Switch.Set(false);

        await _kit.SaveAsync(context => context.Add(TaskItem.Create("Pagar boleto", Now)), Ct);

        (await _kit.OutboxAsync(Ct)).Should().BeEmpty();
        (await _kit.StatesAsync(Ct)).Should().BeEmpty();
        _kit.Trigger.Signals.Should().Be(0);
    }

    [Fact]
    public async Task CreatingATask_EnqueuesTheTaskBeforeItsOccurrence_WithEveryField()
    {
        var task = TaskItem.Create("Pagar boleto", Now, "No banco", TaskPriority.High);

        await _kit.SaveAsync(context => context.Add(task), Ct);

        var outbox = await _kit.OutboxAsync(Ct);
        outbox.Select(row => row.EntityType).Should().Equal(SyncEntityTypes.Task, SyncEntityTypes.Occurrence);
        outbox.Should().OnlyContain(row => row.Kind == SyncOperationKind.Upsert && row.BaseVersion == 0 && !row.Attempted);

        var fields = SyncCaptureKit.Fields(outbox[0]);
        fields.Keys.Should().BeEquivalentTo(SyncRegistry.Task.Fields.Select(field => field.Name));
        fields[SyncFields.Task.Title].GetString().Should().Be("Pagar boleto");
        fields[SyncFields.Task.Priority].GetInt64().Should().Be((long)TaskPriority.High);
        fields.Should().NotContainKey("concludedAt");
        outbox[0].OriginCreatedAt.Should().Be(Now);

        var states = await _kit.StatesAsync(Ct);
        states.Should().HaveCount(2).And.OnlyContain(state => state.CreatedByDeviceId == _kit.Device.DeviceId);
        _kit.Trigger.Signals.Should().Be(1);
    }

    [Fact]
    public async Task EditingBeforeTheFirstSend_GoesIntoTheSameOperation()
    {
        var task = await CreateTaskAsync();

        await EditTaskAsync(task.Id, loaded => loaded.Rename("Pagar o boleto da luz"));

        var outbox = await _kit.OutboxAsync(Ct);
        outbox.Should().HaveCount(2);
        SyncCaptureKit.Fields(outbox[0])[SyncFields.Task.Title].GetString().Should().Be("Pagar o boleto da luz");
    }

    [Fact]
    public async Task EditingAfterTheSend_IsANewOperationWithOnlyTheChangedField()
    {
        var task = await CreateTaskAsync();
        await _kit.MarkAllAttemptedAsync(Ct);

        await EditTaskAsync(task.Id, loaded => loaded.Rename("Pagar o boleto da luz"));

        var outbox = await _kit.OutboxAsync(Ct);
        outbox.Should().HaveCount(3);
        SyncCaptureKit.Fields(outbox[2]).Keys.Should().Equal(SyncFields.Task.Title);
        outbox[2].BaseVersion.Should().Be(0, "o servidor ainda não confirmou a criação");
    }

    [Fact]
    public async Task AfterTheServerAccepted_TheNextEditCarriesTheServerVersion()
    {
        var task = await CreateTaskAsync();
        await _kit.AcknowledgeAllAsync(Ct);

        await EditTaskAsync(task.Id, loaded => loaded.ChangePriority(TaskPriority.Urgent));

        var operation = (await _kit.OutboxAsync(Ct)).Should().ContainSingle().Subject;
        operation.BaseVersion.Should().Be(1);
        SyncCaptureKit.Fields(operation).Keys.Should().Equal(SyncFields.Task.Priority);
    }

    [Fact]
    public async Task ReplacingTheReminderPolicy_IsOneCompositeField()
    {
        var task = await CreateTaskAsync();
        await _kit.AcknowledgeAllAsync(Ct);

        await EditTaskAsync(task.Id, loaded => loaded.ChangeReminder(ReminderPolicy.Urgent));

        var operation = (await _kit.OutboxAsync(Ct)).Should().ContainSingle().Subject;
        var fields = SyncCaptureKit.Fields(operation);
        fields.Keys.Should().Equal(SyncFields.Task.Reminder);
        SyncValue.Member(fields[SyncFields.Task.Reminder], SyncFields.Reminder.IsEnabled).GetBoolean()
            .Should().Be(ReminderPolicy.Urgent.IsEnabled);
    }

    [Fact]
    public async Task LocalOnlyChanges_DoNotTravel()
    {
        var task = await CreateTaskAsync();
        await _kit.AcknowledgeAllAsync(Ct);

        await EditTaskAsync(task.Id, loaded =>
        {
            var occurrence = loaded.Occurrences.Single();
            loaded.PlaceOccurrence(occurrence.Id, 3);
            occurrence.ArmReminder(Now.AddHours(1));
        });

        (await _kit.OutboxAsync(Ct)).Should().BeEmpty();
        _kit.Trigger.Signals.Should().Be(1, "só a criação avisou");
    }

    [Fact]
    public async Task TagLinks_TravelOneByOne_WithAnIdFromThePair()
    {
        var urgent = Tag.Create("Urgente", "#FF0000", Now);
        var home = Tag.Create("Casa", "#00FF00", Now);
        var task = TaskItem.Create("Pagar boleto", Now);
        task.SetTags([urgent.Id]);
        await _kit.SaveAsync(context => context.AddRange(urgent, home, task), Ct);
        await _kit.AcknowledgeAllAsync(Ct);

        await EditTaskAsync(task.Id, loaded => loaded.SetTags([home.Id]));

        var outbox = await _kit.OutboxAsync(Ct);
        outbox.Select(row => (row.EntityType, row.Kind, row.EntityId)).Should().BeEquivalentTo(
        [
            (SyncEntityTypes.TaskTag, SyncOperationKind.Upsert, SyncIds.TaskTag(task.Id, home.Id)),
            (SyncEntityTypes.TaskTag, SyncOperationKind.Delete, SyncIds.TaskTag(task.Id, urgent.Id)),
        ]);
    }

    [Fact]
    public async Task PermanentDeletion_SendsOnlyTheRoot()
    {
        var urgent = Tag.Create("Urgente", "#FF0000", Now);
        var task = TaskItem.Create("Pagar boleto", Now);
        task.SetTags([urgent.Id]);
        await _kit.SaveAsync(context => context.AddRange(urgent, task), Ct);
        await _kit.AcknowledgeAllAsync(Ct);

        await using (var context = _kit.CreateContext())
        {
            var repository = new TaskItemRepository(context);
            var loaded = await repository.FindByIdAsync(task.Id, Ct);
            repository.Remove(loaded!);
            await context.SaveChangesAsync(Ct);
        }

        var operation = (await _kit.OutboxAsync(Ct)).Should().ContainSingle().Subject;
        operation.EntityType.Should().Be(SyncEntityTypes.Task);
        operation.Kind.Should().Be(SyncOperationKind.Delete);
        operation.BaseVersion.Should().Be(1);

        var states = await _kit.StatesAsync(Ct);
        states.Single(state => state.EntityId == task.Id).IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task DeletingSomethingThatNeverLeft_LeavesNoTrace()
    {
        var task = await CreateTaskAsync();

        await using (var context = _kit.CreateContext())
        {
            var repository = new TaskItemRepository(context);
            repository.Remove((await repository.FindByIdAsync(task.Id, Ct))!);
            await context.SaveChangesAsync(Ct);
        }

        (await _kit.OutboxAsync(Ct)).Should().BeEmpty();
        (await _kit.StatesAsync(Ct)).Where(state => state.EntityType == SyncEntityTypes.Task).Should().BeEmpty();
    }

    [Fact]
    public async Task ARunningTimer_StaysHere_UntilItStops()
    {
        var task = await CreateTaskAsync();
        await _kit.AcknowledgeAllAsync(Ct);
        var entry = TimeEntry.StartTimer(task.Occurrences.Single().Id, Now);

        await _kit.SaveAsync(context => context.Add(entry), Ct);
        (await _kit.OutboxAsync(Ct)).Should().BeEmpty();

        await using (var context = _kit.CreateContext())
        {
            var loaded = await context.TimeEntries.SingleAsync(Ct);
            loaded.Stop(Now.AddMinutes(30));
            await context.SaveChangesAsync(Ct);
        }

        var operation = (await _kit.OutboxAsync(Ct)).Should().ContainSingle().Subject;
        operation.EntityType.Should().Be(SyncEntityTypes.TimeEntry);
        operation.BaseVersion.Should().Be(0);
        SyncValue.ReadInstant(SyncCaptureKit.Fields(operation)[SyncFields.TimeEntry.EndedAt]).Should().Be(Now.AddMinutes(30));
    }

    [Fact]
    public async Task TheDirectoryPath_NeverTravels_ButItsAliasDoes()
    {
        var tag = Tag.Create("ECO", "#22C55E", Now);
        var directory = tag.AddDirectory("@core", @"C:\Projects\core", null, null, Now);
        await _kit.SaveAsync(context => context.Add(tag), Ct);

        var created = (await _kit.OutboxAsync(Ct)).Single(row => row.EntityType == SyncEntityTypes.TagDirectory);
        SyncCaptureKit.Fields(created).Keys.Should().NotContain("path");
        created.Fields.Should().NotContain("Projects");
        await _kit.AcknowledgeAllAsync(Ct);

        await EditTagAsync(tag.Id, loaded => loaded.UpdateDirectory(directory.Id, "@core", @"D:\Code\core", null, null));
        (await _kit.OutboxAsync(Ct)).Should().BeEmpty();

        await EditTagAsync(tag.Id, loaded => loaded.UpdateDirectory(directory.Id, "@nucleo", @"D:\Code\core", null, null));
        var renamed = (await _kit.OutboxAsync(Ct)).Should().ContainSingle().Subject;
        SyncCaptureKit.Fields(renamed).Keys.Should().Equal(SyncFields.TagDirectory.Alias);
    }

    [Fact]
    public async Task MovingAStickyNoteWindow_DoesNotTravel_ButEditingItDoes()
    {
        var note = StickyNote.Create(Now);
        note.Edit("Ligar para o banco", Now);
        await _kit.SaveAsync(context => context.Add(note), Ct);
        await _kit.AcknowledgeAllAsync(Ct);

        await using (var context = _kit.CreateContext())
        {
            var loaded = await context.StickyNotes.SingleAsync(Ct);
            loaded.Place(100, 200, 300, 240);
            loaded.Close();
            await context.SaveChangesAsync(Ct);
        }

        (await _kit.OutboxAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task ARolledBackTransaction_LeavesNoOperationBehind()
    {
        var failing = async () =>
        {
            await using var context = _kit.CreateContext();
            await new EfUnitOfWork(context).ExecuteInTransactionAsync(
                async token =>
                {
                    context.Add(TaskItem.Create("Pagar boleto", Now));
                    await context.SaveChangesAsync(token);
                    throw new InvalidOperationException("regra recusou depois");
                },
                Ct);
        };

        await failing.Should().ThrowAsync<InvalidOperationException>();

        (await _kit.OutboxAsync(Ct)).Should().BeEmpty();
        (await _kit.StatesAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task UnderSuppression_RemoteWritesGenerateNothing()
    {
        using (SyncSuppression.Enter())
        {
            await _kit.SaveAsync(context => context.Add(TaskItem.Create("Veio do servidor", Now)), Ct);
        }

        (await _kit.OutboxAsync(Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task TheSynchronousSaveChanges_CapturesToo()
    {
        await using (var context = _kit.CreateContext())
        {
            context.Add(Tag.Create("Casa", "#00FF00", Now));
            context.SaveChanges();
        }

        (await _kit.OutboxAsync(Ct)).Should().ContainSingle(row => row.EntityType == SyncEntityTypes.Tag);
    }

    private async Task<TaskItem> CreateTaskAsync()
    {
        var task = TaskItem.Create("Pagar boleto", Now);
        await _kit.SaveAsync(context => context.Add(task), Ct);
        return task;
    }

    private async Task EditTaskAsync(Guid id, Action<TaskItem> change)
    {
        await using var context = _kit.CreateContext();
        var task = await new TaskItemRepository(context).FindByIdAsync(id, Ct);
        change(task!);
        await context.SaveChangesAsync(Ct);
    }

    private async Task EditTagAsync(Guid id, Action<Tag> change)
    {
        await using var context = _kit.CreateContext();
        var tag = await new TagRepository(context).FindByIdAsync(id, Ct);
        change(tag!);
        await context.SaveChangesAsync(Ct);
    }
}
