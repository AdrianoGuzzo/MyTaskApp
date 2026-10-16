using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Server.Application.Tests;

/// <summary>O push: idempotência, pais, exclusão, unicidade e isolamento (ADR-061).</summary>
public sealed class PushProcessorTests
{
    private readonly InMemorySyncServer _server = new();

    private readonly Guid _user = Guid.CreateVersion7();

    private readonly Guid _device = Guid.CreateVersion7();

    private readonly Guid _workspace;

    public PushProcessorTests() => _workspace = _server.AddWorkspace(_user);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SyncCaller Caller => new(_user, _device);

    [Fact]
    public async Task ACreation_IsVersionOne_WithTheAuthorAndTheOrigin()
    {
        var task = Guid.CreateVersion7();

        var result = await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task()));

        result.Outcome.Should().Be(OperationOutcome.Applied);
        result.Version.Should().Be(1);
        result.PreviousVersion.Should().Be(0);
        var stored = _server.Get(_workspace, SyncEntityTypes.Task, task)!;
        stored.CreatedByDeviceId.Should().Be(_device);
        stored.CreatedByUserId.Should().Be(_user);
        stored.OriginCreatedAt.Should().Be(Ops.Created);
        stored.LastSeq.Should().Be(1);
        _server.LastSeqOf(_workspace).Should().Be(1);
    }

    [Fact]
    public async Task TheSameOperationTwice_ReturnsTheStoredResult_AndChangesNothing()
    {
        var operation = Ops.Upsert(_device, SyncEntityTypes.Task, Guid.CreateVersion7(), 0, Ops.Task());

        var first = await PushOneAsync(operation);
        var again = await PushOneAsync(operation);

        again.Duplicate.Should().BeTrue();
        (again with { Duplicate = false }).Should().BeEquivalentTo(first);
        _server.LastSeqOf(_workspace).Should().Be(1);
        _server.Log.Should().ContainSingle();
    }

    [Fact]
    public async Task TheSameIdWithOtherContent_IsRefused()
    {
        var operation = Ops.Upsert(_device, SyncEntityTypes.Task, Guid.CreateVersion7(), 0, Ops.Task());
        await PushOneAsync(operation);

        var tampered = await PushOneAsync(operation with { Fields = Ops.Task("Outro título") });

        tampered.Error!.Code.Should().Be(SyncErrorCodes.IdempotencyKeyReuse);
    }

    [Fact]
    public async Task AChildBeforeItsParent_IsRetryable_AndNotRemembered()
    {
        var task = Guid.CreateVersion7();
        var occurrence = Ops.Upsert(_device, SyncEntityTypes.Occurrence, Guid.CreateVersion7(), 0, Ops.Occurrence(task));

        var early = await PushOneAsync(occurrence);
        early.Error!.Code.Should().Be(SyncErrorCodes.ParentMissing);
        SyncErrorCodes.IsRetryable(early.Error.Code).Should().BeTrue();

        await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task()));
        var retried = await PushOneAsync(occurrence);

        retried.Outcome.Should().Be(OperationOutcome.Applied);
        retried.Duplicate.Should().BeFalse();
    }

    [Fact]
    public async Task AParentEarlierInTheSameBatch_IsEnough()
    {
        var task = Guid.CreateVersion7();

        var response = await _server.Push.PushAsync(Caller, _workspace, Request(
            Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task()),
            Ops.Upsert(_device, SyncEntityTypes.Occurrence, Guid.CreateVersion7(), 0, Ops.Occurrence(task))), Ct);

        response.Results.Should().OnlyContain(result => result.Outcome == OperationOutcome.Applied);
        _server.LastSeqOf(_workspace).Should().Be(2, "uma posição do log por mudança, sem buraco");
    }

    [Fact]
    public async Task DeletingATask_TakesItsChildrenAlong_AsTombstones()
    {
        var (task, occurrence, tag, link) = await SeedTaskWithTagAsync();

        var result = await PushOneAsync(Ops.Delete(_device, SyncEntityTypes.Task, task, 1));

        result.Outcome.Should().Be(OperationOutcome.Applied);
        _server.Get(_workspace, SyncEntityTypes.Task, task)!.IsDeleted.Should().BeTrue();
        _server.Get(_workspace, SyncEntityTypes.Occurrence, occurrence)!.IsDeleted.Should().BeTrue();
        _server.Get(_workspace, SyncEntityTypes.TaskTag, link)!.IsDeleted.Should().BeTrue();
        _server.Get(_workspace, SyncEntityTypes.Tag, tag)!.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task DeletingATag_EmptiesTheStickyNoteTag_InsteadOfDeletingTheNote()
    {
        var tag = Guid.CreateVersion7();
        var note = Guid.CreateVersion7();
        await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Tag, tag, 0, Ops.Tag("Casa")));
        await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.StickyNote, note, 0, Ops.StickyNote(tag)));

        await PushOneAsync(Ops.Delete(_device, SyncEntityTypes.Tag, tag, 1));

        var stored = _server.Get(_workspace, SyncEntityTypes.StickyNote, note)!;
        stored.IsDeleted.Should().BeFalse();
        SyncValue.IsNull(stored.Data[SyncFields.StickyNote.TagId]).Should().BeTrue();
        stored.Version.Should().Be(2);
    }

    [Fact]
    public async Task ADeletedEntity_DoesNotComeBack()
    {
        var task = Guid.CreateVersion7();
        await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task()));
        await PushOneAsync(Ops.Delete(_device, SyncEntityTypes.Task, task, 1));

        var late = await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Task, task, 1, new() { [SyncFields.Task.Title] = SyncValue.String("Editada offline") }));

        late.Error!.Code.Should().Be(SyncErrorCodes.EntityDeleted);
        _server.Get(_workspace, SyncEntityTypes.Task, task)!.IsDeleted.Should().BeTrue();
    }

    /// <summary>
    /// ADR-066: o comando que voltou a ser compartilhado depois de excluído em
    /// outro computador. Quem manda já viu a exclusão (a versão base alcança a
    /// do tombstone) e manda a entidade inteira: é recriar de propósito.
    /// </summary>
    [Fact]
    public async Task ADeviceThatSawTheDelete_CanCreateItAgain_Whole()
    {
        var task = Guid.CreateVersion7();
        await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task()));
        await PushOneAsync(Ops.Delete(_device, SyncEntityTypes.Task, task, 1));

        var partial = await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Task, task, 2, new() { [SyncFields.Task.Title] = SyncValue.String("De volta") }));
        partial.Error!.Code.Should().Be(SyncErrorCodes.ValidationFailed);

        var whole = await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Task, task, 2, Ops.Task("De volta")));

        whole.Outcome.Should().Be(OperationOutcome.Applied);
        var stored = _server.Get(_workspace, SyncEntityTypes.Task, task)!;
        stored.IsDeleted.Should().BeFalse();
        stored.Version.Should().Be(3);
        stored.Data[SyncFields.Task.Title].GetString().Should().Be("De volta");
    }

    [Fact]
    public async Task ADeleteThatArrivesBeforeTheCreation_StillWins()
    {
        var task = Guid.CreateVersion7();

        await PushOneAsync(Ops.Delete(_device, SyncEntityTypes.Task, task, 0));
        var creation = await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task()));

        creation.Error!.Code.Should().Be(SyncErrorCodes.EntityDeleted);
    }

    [Fact]
    public async Task DeletingTwice_IsANoOp()
    {
        var task = Guid.CreateVersion7();
        await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task()));
        await PushOneAsync(Ops.Delete(_device, SyncEntityTypes.Task, task, 1));

        var again = await PushOneAsync(Ops.Delete(_device, SyncEntityTypes.Task, task, 1));

        again.Outcome.Should().Be(OperationOutcome.Noop);
    }

    [Fact]
    public async Task ATagLinkMarkedAgain_Revives()
    {
        var (task, _, tag, link) = await SeedTaskWithTagAsync();
        await PushOneAsync(Ops.Delete(_device, SyncEntityTypes.TaskTag, link, 1));

        var again = await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.TaskTag, link, 2, Ops.TaskTag(task, tag)));

        again.Outcome.Should().Be(OperationOutcome.Applied);
        _server.Get(_workspace, SyncEntityTypes.TaskTag, link)!.IsDeleted.Should().BeFalse();
    }

    [Fact]
    public async Task ADuplicateTagName_IsRefused_PointingAtTheOtherTag()
    {
        var existing = Guid.CreateVersion7();
        await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Tag, existing, 0, Ops.Tag("Casa")));

        var duplicate = await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Tag, Guid.CreateVersion7(), 0, Ops.Tag("CASA")));

        duplicate.Error!.Code.Should().Be(SyncErrorCodes.UniqueViolation);
        duplicate.RejectedFields.Should().ContainSingle().Which.ConflictingEntityId.Should().Be(existing);
    }

    [Fact]
    public async Task RenamingIntoAnotherName_KeepsTheOldName_AndAppliesTheRest()
    {
        var home = Guid.CreateVersion7();
        var work = Guid.CreateVersion7();
        await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Tag, home, 0, Ops.Tag("Casa")));
        await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Tag, work, 0, Ops.Tag("Trabalho")));

        var result = await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Tag, work, 1, new()
        {
            [SyncFields.Tag.Name] = SyncValue.String("casa"),
            [SyncFields.Tag.ColorHex] = SyncValue.String("#FF0000"),
        }));

        result.Outcome.Should().Be(OperationOutcome.PartiallyApplied);
        result.RejectedFields.Should().ContainSingle(field => field.Field == SyncFields.Tag.Name && field.ConflictingEntityId == home);
        var stored = _server.Get(_workspace, SyncEntityTypes.Tag, work)!;
        stored.Data[SyncFields.Tag.Name].GetString().Should().Be("Trabalho");
        stored.Data[SyncFields.Tag.ColorHex].GetString().Should().Be("#FF0000");
    }

    [Fact]
    public async Task ConcurrentEdits_FromTwoDevices_MergeByField()
    {
        var task = Guid.CreateVersion7();
        var other = Guid.CreateVersion7();
        await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task()));

        await _server.Push.PushAsync(new SyncCaller(_user, other), _workspace, new PushRequest(SyncSchema.Current, other,
            [Ops.Upsert(other, SyncEntityTypes.Task, task, 1, new() { [SyncFields.Task.Title] = SyncValue.String("De B") })]), Ct);
        var mine = await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Task, task, 1, new()
        {
            [SyncFields.Task.Title] = SyncValue.String("De A"),
            [SyncFields.Task.Priority] = SyncValue.Integer(3),
        }));

        mine.Outcome.Should().Be(OperationOutcome.PartiallyApplied);
        mine.RejectedFields.Should().ContainSingle().Which.ServerValue.GetString().Should().Be("De B");
        var stored = _server.Get(_workspace, SyncEntityTypes.Task, task)!;
        stored.Data[SyncFields.Task.Title].GetString().Should().Be("De B");
        stored.Data[SyncFields.Task.Priority].GetInt64().Should().Be(3);
        stored.Version.Should().Be(3);
    }

    [Fact]
    public async Task AnInvalidValue_IsAPermanentRefusal()
    {
        var result = await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Task, Guid.CreateVersion7(), 0, Ops.Task(new string('x', 201))));

        result.Error!.Code.Should().Be(SyncErrorCodes.ValidationFailed);
        SyncErrorCodes.IsRetryable(result.Error.Code).Should().BeFalse();
    }

    [Fact]
    public async Task AnUnknownType_IsRefused()
    {
        var result = await PushOneAsync(Ops.Upsert(_device, "calendarEvent", Guid.CreateVersion7(), 0, Ops.Task()));

        result.Error!.Code.Should().Be(SyncErrorCodes.UnknownEntityType);
    }

    [Fact]
    public async Task SomeoneOutsideTheWorkspace_CannotPushIntoIt()
    {
        var stranger = new SyncCaller(Guid.CreateVersion7(), _device);

        var push = () => _server.Push.PushAsync(stranger, _workspace, Request(Ops.Upsert(_device, SyncEntityTypes.Task, Guid.CreateVersion7(), 0, Ops.Task())), Ct);

        (await push.Should().ThrowAsync<SyncServerException>()).Which.Status.Should().Be(404);
    }

    [Fact]
    public async Task AViewer_CannotPush()
    {
        var viewer = Guid.CreateVersion7();
        _server.AddMember(_workspace, viewer, WorkspaceRole.Viewer);

        var push = () => _server.Push.PushAsync(new SyncCaller(viewer, _device), _workspace, Request(), Ct);

        (await push.Should().ThrowAsync<SyncServerException>()).Which.Status.Should().Be(403);
    }

    [Fact]
    public async Task AReferenceToAnotherWorkspace_IsAMissingParent()
    {
        var otherWorkspace = _server.AddWorkspace(_user);
        var foreignTask = Guid.CreateVersion7();
        await _server.Push.PushAsync(Caller, otherWorkspace, Request(Ops.Upsert(_device, SyncEntityTypes.Task, foreignTask, 0, Ops.Task())), Ct);

        var result = await PushOneAsync(Ops.Upsert(_device, SyncEntityTypes.Occurrence, Guid.CreateVersion7(), 0, Ops.Occurrence(foreignTask)));

        result.Error!.Code.Should().Be(SyncErrorCodes.ParentMissing);
    }

    [Fact]
    public async Task AnOperationSignedByAnotherDevice_IsRefused()
    {
        var result = await PushOneAsync(Ops.Upsert(Guid.CreateVersion7(), SyncEntityTypes.Task, Guid.CreateVersion7(), 0, Ops.Task()));

        result.Error!.Code.Should().Be(SyncErrorCodes.DeviceMismatch);
    }

    [Fact]
    public async Task AnOutdatedClient_IsAskedToUpgrade()
    {
        var push = () => _server.Push.PushAsync(Caller, _workspace, new PushRequest(0, _device, []), Ct);

        (await push.Should().ThrowAsync<SyncServerException>()).Which.Status.Should().Be(426);
    }

    private async Task<(Guid Task, Guid Occurrence, Guid Tag, Guid Link)> SeedTaskWithTagAsync()
    {
        var task = Guid.CreateVersion7();
        var occurrence = Guid.CreateVersion7();
        var tag = Guid.CreateVersion7();
        var link = SyncIds.TaskTag(task, tag);

        await _server.Push.PushAsync(Caller, _workspace, Request(
            Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task()),
            Ops.Upsert(_device, SyncEntityTypes.Occurrence, occurrence, 0, Ops.Occurrence(task)),
            Ops.Upsert(_device, SyncEntityTypes.Tag, tag, 0, Ops.Tag("Casa")),
            Ops.Upsert(_device, SyncEntityTypes.TaskTag, link, 0, Ops.TaskTag(task, tag))), Ct);

        return (task, occurrence, tag, link);
    }

    private async Task<OperationResult> PushOneAsync(SyncOperation operation) =>
        (await _server.Push.PushAsync(Caller, _workspace, Request(operation), Ct)).Results.Single();

    private PushRequest Request(params SyncOperation[] operations) => new(SyncSchema.Current, _device, operations);
}
