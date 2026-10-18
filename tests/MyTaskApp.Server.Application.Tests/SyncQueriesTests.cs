using System.Text.Json;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Server.Application.Tests;

/// <summary>O pull com cursor, a página fechada e os dispositivos (ADR-061).</summary>
public sealed class SyncQueriesTests
{
    private readonly InMemorySyncServer _server = new();

    private readonly Guid _user = Guid.CreateVersion7();

    private readonly Guid _device = Guid.CreateVersion7();

    private readonly Guid _workspace;

    public SyncQueriesTests() => _workspace = _server.AddWorkspace(_user);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SyncCaller Caller => new(_user, _device);

    [Fact]
    public async Task PullingPageByPage_BringsEverythingOnce_InParentOrder()
    {
        var task = Guid.CreateVersion7();
        var tag = Guid.CreateVersion7();
        await PushAsync(
            Ops.Upsert(_device, SyncEntityTypes.Tag, tag, 0, Ops.Tag("Casa")),
            Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task()),
            Ops.Upsert(_device, SyncEntityTypes.Occurrence, Guid.CreateVersion7(), 0, Ops.Occurrence(task)),
            Ops.Upsert(_device, SyncEntityTypes.TaskTag, SyncIds.TaskTag(task, tag), 0, Ops.TaskTag(task, tag)));

        var seen = new List<SyncChange>();
        var cursor = SyncCursor.Start;

        for (var page = 0; page < 10; page++)
        {
            var response = await _server.Queries.PullAsync(Caller, _workspace, cursor, limit: 1, Ct);
            seen.AddRange(response.Changes);
            cursor = response.NextCursor;

            if (!response.HasMore)
            {
                break;
            }
        }

        seen.Select(change => change.EntityId).Distinct().Should().HaveCount(4);
        SyncCursor.TryDecode(cursor, out var last).Should().BeTrue();
        last.Should().Be(_server.LastSeqOf(_workspace));
    }

    [Fact]
    public async Task ATaskAndItsOccurrence_TravelTogether_EvenWhenThePageIsFull()
    {
        var task = Guid.CreateVersion7();
        await PushAsync(
            Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task()),
            Ops.Upsert(_device, SyncEntityTypes.Occurrence, Guid.CreateVersion7(), 0, Ops.Occurrence(task)));

        var response = await _server.Queries.PullAsync(Caller, _workspace, SyncCursor.Start, limit: 1, Ct);

        response.Changes.Select(change => change.EntityType).Should().Equal(SyncEntityTypes.Task, SyncEntityTypes.Occurrence);
        response.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task AParentThatChangedLater_ComesWithTheChild()
    {
        var task = Guid.CreateVersion7();
        await PushAsync(
            Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task()),
            Ops.Upsert(_device, SyncEntityTypes.Occurrence, Guid.CreateVersion7(), 0, Ops.Occurrence(task)),
            Ops.Upsert(_device, SyncEntityTypes.Tag, Guid.CreateVersion7(), 0, Ops.Tag("Casa")));
        await PushAsync(Ops.Upsert(_device, SyncEntityTypes.Task, task, 1, new() { [SyncFields.Task.Title] = SyncValue.String("Renomeada") }));

        // Por posição, a ocorrência (2) vem antes da tarefa (4); a tarefa entra junto.
        var response = await _server.Queries.PullAsync(Caller, _workspace, SyncCursor.Start, limit: 1, Ct);

        response.Changes.Should().Contain(change => change.EntityType == SyncEntityTypes.Task && change.Fields[SyncFields.Task.Title].GetString() == "Renomeada");
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, null)]
    [InlineData(true, 1)]
    [InlineData(true, null)]
    public async Task ATaskWithAThousandOccurrences_ComesOnce_NotTheWholeFamilyPerPage(bool taskChangedLast, int? limit)
    {
        // SEC-005: antes, cada página de limit=1 trazia as ~1.000 ocorrências de novo.
        var task = await PushTaskWithOccurrencesAsync(1_000);

        if (taskChangedLast)
        {
            await PushAsync(Ops.Upsert(_device, SyncEntityTypes.Task, task, 1, new() { [SyncFields.Task.Title] = SyncValue.String("Renomeada") }));
        }

        var pages = await PullAllAsync(_server, limit);
        var changes = pages.SelectMany(page => page.Changes).ToList();

        changes.Select(change => change.EntityId).Distinct().Should().HaveCount(1_001);
        changes.Should().HaveCountLessThanOrEqualTo(1_002, "só a primeira ocorrência pode vir de carona e voltar depois");
        pages.Should().OnlyContain(page => page.Changes.Count <= (limit ?? SyncSchema.DefaultPullLimit) + 1);
    }

    [Fact]
    public async Task ThePageStopsAtTheEntityCeiling_AndTheCursorAlwaysMoves()
    {
        var server = new InMemorySyncServer(new PullLimits { MaxEntities = 10 });
        var workspace = server.AddWorkspace(_user);
        await PushTaskWithOccurrencesAsync(1_000, server, workspace);

        var pages = await PullAllAsync(server, SyncSchema.MaxPullLimit, workspace);

        pages.Should().OnlyContain(page => page.Changes.Count <= 10);
        pages.SkipLast(1).Should().OnlyContain(page => page.HasMore);
        pages.SelectMany(page => page.Changes).Select(change => change.EntityId).Distinct().Should().HaveCount(1_001);
    }

    [Fact]
    public async Task ThePageStopsAtTheByteCeiling_ButAnItemBiggerThanItStillGoesAlone()
    {
        const int maxBytes = 4_000;
        var server = new InMemorySyncServer(new PullLimits { MaxBytes = maxBytes });
        var workspace = server.AddWorkspace(_user);
        var big = Guid.CreateVersion7();
        await PushTaskWithOccurrencesAsync(30, server, workspace);
        await server.Push.PushAsync(Caller, workspace, new PushRequest(SyncSchema.Current, _device,
        [
            Ops.Upsert(_device, SyncEntityTypes.Task, big, 0, Ops.Task().With(SyncFields.Task.Description, SyncValue.String(new string('x', 10_000)))),
            Ops.Upsert(_device, SyncEntityTypes.Occurrence, Guid.CreateVersion7(), 0, Ops.Occurrence(big)),
        ]), Ct);

        var pages = await PullAllAsync(server, SyncSchema.MaxPullLimit, workspace);

        foreach (var page in pages.Where(page => page.Changes.Sum(change => JsonSerializer.SerializeToUtf8Bytes(change, SyncJson.Options).Length) > maxBytes))
        {
            page.Changes.Should().Contain(change => change.EntityId == big, "só o item maior que o teto passa dele");
            page.Changes.Should().HaveCountLessThanOrEqualTo(2, "e vai só com a ocorrência que ele exige");
        }

        pages.Should().HaveCountGreaterThan(2);
        pages.SelectMany(page => page.Changes).Select(change => change.EntityId).Distinct().Should().HaveCount(33);
    }

    [Fact]
    public async Task AParentTheDeviceAlreadyHas_DoesNotRideAlongAgain()
    {
        var task = await PushTaskWithOccurrencesAsync(1);
        var cursor = (await PullAllAsync(_server, null))[^1].NextCursor;
        await PushAsync(Ops.Upsert(_device, SyncEntityTypes.Occurrence, Guid.CreateVersion7(), 0, Ops.Occurrence(task)));
        await PushAsync(Ops.Upsert(_device, SyncEntityTypes.Task, task, 1, new() { [SyncFields.Task.Title] = SyncValue.String("Renomeada") }));

        var response = await _server.Queries.PullAsync(Caller, _workspace, cursor, limit: 1, Ct);

        response.Changes.Should().ContainSingle().Which.EntityType.Should().Be(SyncEntityTypes.Occurrence);
        response.HasMore.Should().BeTrue();
    }

    [Fact]
    public async Task ATimeEntry_BringsItsOccurrenceAndTask_WhenTheDeviceMayNotHaveThem()
    {
        var task = Guid.CreateVersion7();
        var occurrence = Guid.CreateVersion7();
        await PushAsync(
            Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task()),
            Ops.Upsert(_device, SyncEntityTypes.Occurrence, occurrence, 0, Ops.Occurrence(task)));
        var cursor = (await PullAllAsync(_server, null))[^1].NextCursor;
        await PushAsync(Ops.Upsert(_device, SyncEntityTypes.TimeEntry, Guid.CreateVersion7(), 0, Ops.TimeEntry(occurrence)));
        await PushAsync(Ops.Upsert(_device, SyncEntityTypes.Occurrence, occurrence, 1, new()
        {
            [SyncFields.Occurrence.State] = SyncValue.Object(
                (SyncFields.Occurrence.Status, SyncValue.Integer(1)),
                (SyncFields.Occurrence.CompletedAt, SyncValue.Instant(Ops.Created))),
        }));
        await PushAsync(Ops.Upsert(_device, SyncEntityTypes.Task, task, 1, new() { [SyncFields.Task.Title] = SyncValue.String("Renomeada") }));

        var response = await _server.Queries.PullAsync(Caller, _workspace, cursor, limit: 1, Ct);

        // Quem apontava para a ocorrência e para a tarefa mudou depois do cursor: nada prova que o cliente as tenha.
        response.Changes.Select(change => change.EntityType).Should().Equal(SyncEntityTypes.Task, SyncEntityTypes.Occurrence, SyncEntityTypes.TimeEntry);
    }

    [Fact]
    public async Task ThePull_ReadsTheStoreAFewTimes_HoweverBigTheFamily()
    {
        await PushTaskWithOccurrencesAsync(1_000);
        _server.Reads = 0;

        await _server.Queries.PullAsync(Caller, _workspace, SyncCursor.Start, limit: 1, Ct);
        await _server.Queries.PullAsync(Caller, _workspace, SyncCursor.Start, limit: SyncSchema.MaxPullLimit, Ct);

        _server.Reads.Should().BeLessThanOrEqualTo(2 * 5, "uma leitura do log e algumas em lote por página, nunca uma por referência");
    }

    [Fact]
    public async Task FromTheStart_NoTombstone_ButIncrementally_TheDeletionComes()
    {
        var task = Guid.CreateVersion7();
        await PushAsync(Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task()));
        var before = await _server.Queries.PullAsync(Caller, _workspace, SyncCursor.Start, null, Ct);
        await PushAsync(Ops.Delete(_device, SyncEntityTypes.Task, task, 1));

        var fresh = await _server.Queries.PullAsync(Caller, _workspace, SyncCursor.Start, null, Ct);
        var incremental = await _server.Queries.PullAsync(Caller, _workspace, before.NextCursor, null, Ct);

        fresh.Changes.Should().BeEmpty();
        fresh.HasMore.Should().BeFalse();
        incremental.Changes.Should().ContainSingle(change => change.EntityId == task && change.Deleted && change.Fields.Count == 0);
    }

    [Fact]
    public async Task ACursorAheadOfTheServer_AsksForAFullResync()
    {
        var pull = () => _server.Queries.PullAsync(Caller, _workspace, SyncCursor.Encode(99), null, Ct);

        (await pull.Should().ThrowAsync<SyncServerException>()).Which.Code.Should().Be(SyncErrorCodes.CursorAhead);
    }

    [Fact]
    public async Task AGarbageCursor_IsABadRequest()
    {
        var pull = () => _server.Queries.PullAsync(Caller, _workspace, "%%%", null, Ct);

        (await pull.Should().ThrowAsync<SyncServerException>()).Which.Status.Should().Be(400);
    }

    [Fact]
    public async Task AnotherUser_SeesNothingOfThisWorkspace()
    {
        await PushAsync(Ops.Upsert(_device, SyncEntityTypes.Task, Guid.CreateVersion7(), 0, Ops.Task()));
        var stranger = new SyncCaller(Guid.CreateVersion7(), Guid.CreateVersion7());

        var pull = () => _server.Queries.PullAsync(stranger, _workspace, SyncCursor.Start, null, Ct);
        var fetch = () => _server.Queries.FetchAsync(stranger, _workspace, new FetchRequest([]), Ct);

        (await pull.Should().ThrowAsync<SyncServerException>()).Which.Status.Should().Be(404);
        (await fetch.Should().ThrowAsync<SyncServerException>()).Which.Status.Should().Be(404);
        (await _server.Queries.WorkspacesAsync(stranger, Ct)).Should().BeEmpty();
    }

    [Fact]
    public async Task Fetch_BringsTheCurrentStateOfWhatWasAsked_TombstonesIncluded()
    {
        var alive = Guid.CreateVersion7();
        var gone = Guid.CreateVersion7();
        await PushAsync(
            Ops.Upsert(_device, SyncEntityTypes.Tag, alive, 0, Ops.Tag("Casa")),
            Ops.Upsert(_device, SyncEntityTypes.Tag, gone, 0, Ops.Tag("Rua")));
        await PushAsync(Ops.Delete(_device, SyncEntityTypes.Tag, gone, 1));

        var response = await _server.Queries.FetchAsync(Caller, _workspace, new FetchRequest(
            [new EntityRef(SyncEntityTypes.Tag, alive), new EntityRef(SyncEntityTypes.Tag, gone), new EntityRef(SyncEntityTypes.Tag, Guid.CreateVersion7())]), Ct);

        response.Changes.Should().HaveCount(2);
        response.Changes.Single(change => change.EntityId == gone).Deleted.Should().BeTrue();
    }

    [Fact]
    public async Task TheFirstHello_RegistersTheDevice_AndLaterOnesUpdateIt()
    {
        var first = await _server.Devices.HelloAsync(Caller, new DeviceHello("  Notebook  ", "Windows", "1.11.0", SyncSchema.Current), Ct);
        var second = await _server.Devices.HelloAsync(Caller, new DeviceHello("Outro nome", "Windows", "1.12.0", SyncSchema.Current), Ct);

        first.Nickname.Should().Be("Notebook");
        first.IsCurrent.Should().BeTrue();
        second.Nickname.Should().Be("Notebook", "o apelido muda pela tela, e não a cada olá");
        second.AppVersion.Should().Be("1.12.0");
    }

    [Fact]
    public async Task ARevokedDevice_CannotComeBack_AndItsTokensAreRevoked()
    {
        var other = Guid.CreateVersion7();
        await _server.Devices.HelloAsync(new SyncCaller(_user, other), new DeviceHello("Antigo", "Windows", "1.0", SyncSchema.Current), Ct);

        await _server.Devices.RevokeAsync(Caller, other, Ct);
        var hello = () => _server.Devices.HelloAsync(new SyncCaller(_user, other), new DeviceHello(null, "Windows", "1.0", SyncSchema.Current), Ct);

        _server.Revoked.Should().ContainSingle().Which.Should().Be((_user, other));
        (await hello.Should().ThrowAsync<SyncServerException>()).Which.Code.Should().Be(SyncErrorCodes.DeviceRevoked);
        (await _server.Devices.IsActiveAsync(new SyncCaller(_user, other), Ct)).Should().BeFalse();
    }

    [Fact]
    public async Task OnlyMyOwnDevices_CanBeRenamedOrRevoked()
    {
        var mine = Guid.CreateVersion7();
        await _server.Devices.HelloAsync(new SyncCaller(_user, mine), new DeviceHello("Meu", "Windows", "1.0", SyncSchema.Current), Ct);
        var stranger = new SyncCaller(Guid.CreateVersion7(), Guid.CreateVersion7());

        var rename = () => _server.Devices.RenameAsync(stranger, mine, "Roubado", Ct);
        var revoke = () => _server.Devices.RevokeAsync(stranger, mine, Ct);

        (await rename.Should().ThrowAsync<SyncServerException>()).Which.Status.Should().Be(404);
        (await revoke.Should().ThrowAsync<SyncServerException>()).Which.Status.Should().Be(404);
        _server.Revoked.Should().BeEmpty();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("um apelido com mais de sessenta caracteres, que não cabe na lista")]
    public async Task ABadNickname_IsRefused(string nickname)
    {
        await _server.Devices.HelloAsync(Caller, new DeviceHello("Notebook", "Windows", "1.0", SyncSchema.Current), Ct);

        var rename = () => _server.Devices.RenameAsync(Caller, _device, nickname, Ct);

        (await rename.Should().ThrowAsync<SyncServerException>()).Which.Status.Should().Be(400);
    }

    private Task<PushResponse> PushAsync(params SyncOperation[] operations) =>
        _server.Push.PushAsync(Caller, _workspace, new PushRequest(SyncSchema.Current, _device, operations), Ct);

    /// <summary>Uma tarefa e as ocorrências dela, em pushes do tamanho máximo.</summary>
    private async Task<Guid> PushTaskWithOccurrencesAsync(int occurrences, InMemorySyncServer? server = null, Guid? workspace = null)
    {
        var task = Guid.CreateVersion7();
        var operations = new List<SyncOperation> { Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task()) };
        operations.AddRange(Enumerable.Range(0, occurrences)
            .Select(_ => Ops.Upsert(_device, SyncEntityTypes.Occurrence, Guid.CreateVersion7(), 0, Ops.Occurrence(task))));

        foreach (var chunk in operations.Chunk(SyncSchema.MaxOperationsPerPush))
        {
            var response = await (server ?? _server).Push.PushAsync(
                Caller, workspace ?? _workspace, new PushRequest(SyncSchema.Current, _device, chunk), Ct);
            response.Results.Should().OnlyContain(result => result.Outcome == OperationOutcome.Applied);
        }

        return task;
    }

    /// <summary>Do zero até o fim, conferindo que cada página com <c>hasMore</c> anda o cursor.</summary>
    private async Task<List<PullResponse>> PullAllAsync(InMemorySyncServer server, int? limit, Guid? workspace = null)
    {
        var pages = new List<PullResponse>();
        var cursor = SyncCursor.Start;

        while (pages.Count < 5_000)
        {
            var response = await server.Queries.PullAsync(Caller, workspace ?? _workspace, cursor, limit, Ct);
            pages.Add(response);

            if (!response.HasMore)
            {
                return pages;
            }

            SyncCursor.TryDecode(cursor, out var before).Should().BeTrue();
            SyncCursor.TryDecode(response.NextCursor, out var after).Should().BeTrue();
            after.Should().BeGreaterThan(before);
            cursor = response.NextCursor;
        }

        throw new InvalidOperationException("O pull não terminou.");
    }
}
