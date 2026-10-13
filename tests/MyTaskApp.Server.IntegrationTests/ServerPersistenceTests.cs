using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Server.Application;
using MyTaskApp.Server.Infrastructure.Persistence;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Server.IntegrationTests;

/// <summary>
/// A sincronização do servidor sobre PostgreSQL de verdade (ADR-061): a
/// transação, a trava do workspace, os índices e o <c>jsonb</c>.
/// </summary>
public sealed class ServerPersistenceTests(PostgresFixture postgres) : IAsyncLifetime
{
    private static readonly DateTimeOffset Created = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private ServerHarness _server = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _server = await ServerHarness.CreateAsync(postgres, Ct);

    public async ValueTask DisposeAsync()
    {
        if (_server is not null)
        {
            await _server.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheMigrations_CreateAnEmptyServer_AndRunTwiceWithoutHarm()
    {
        await using var context = new ServerDbContext(new DbContextOptionsBuilder<ServerDbContext>().UseNpgsql(_server.ConnectionString).Options);

        await context.Database.MigrateAsync(Ct);

        (await context.Database.GetPendingMigrationsAsync(Ct)).Should().BeEmpty();
        (await context.SyncEntities.CountAsync(Ct)).Should().Be(0);
    }

    [Fact]
    public async Task WhatWasPushed_ComesBackInThePull_WithEveryValueAsSent()
    {
        var (user, workspace) = await _server.CreateUserAsync(Ct);
        var device = Guid.CreateVersion7();
        var task = Guid.CreateVersion7();
        var fields = TaskFields("Pagar boleto");
        fields["colorOfTheFuture"] = SyncValue.String("campo de um cliente mais novo");

        await PushAsync(user, device, workspace, Upsert(device, SyncEntityTypes.Task, task, 0, fields));
        var pulled = await PullAsync(user, device, workspace, SyncCursor.Start);

        var change = pulled.Changes.Should().ContainSingle().Subject;
        change.Version.Should().Be(1);
        change.CreatedByDeviceId.Should().Be(device);
        change.OriginCreatedAt.Should().Be(Created);
        foreach (var (name, value) in fields)
        {
            SyncValue.AreEqual(change.Fields[name], value).Should().BeTrue(name);
        }
    }

    [Fact]
    public async Task TheSameOperation_IsAppliedOnce_EvenAfterTheResponseWasLost()
    {
        var (user, workspace) = await _server.CreateUserAsync(Ct);
        var device = Guid.CreateVersion7();
        var operation = Upsert(device, SyncEntityTypes.Task, Guid.CreateVersion7(), 0, TaskFields("Pagar boleto"));

        var first = (await PushAsync(user, device, workspace, operation)).Results.Single();
        var retry = (await PushAsync(user, device, workspace, operation)).Results.Single();

        retry.Duplicate.Should().BeTrue();
        retry.Version.Should().Be(first.Version);
        await using var context = Context();
        (await context.ChangeLog.CountAsync(Ct)).Should().Be(1);
        (await context.Workspaces.SingleAsync(Ct)).LastSeq.Should().Be(1);
    }

    [Fact]
    public async Task ConcurrentPushes_GetDenseOrderedPositions_AndNoneIsLost()
    {
        var (user, workspace) = await _server.CreateUserAsync(Ct);
        var devices = Enumerable.Range(0, 6).Select(_ => Guid.CreateVersion7()).ToList();

        await Task.WhenAll(devices.Select(device => Task.Run(
            () => PushAsync(user, device, workspace, [.. Enumerable.Range(0, 5).Select(index => Upsert(device, SyncEntityTypes.Task, Guid.CreateVersion7(), 0, TaskFields($"T{index}")))]),
            Ct)));

        await using var context = Context();
        var positions = await context.SyncEntities.Select(entity => entity.LastSeq).OrderBy(seq => seq).ToListAsync(Ct);
        positions.Should().Equal(Enumerable.Range(1, 30).Select(seq => (long)seq), "uma posição por mudança, em ordem e sem buraco");
        (await context.Workspaces.SingleAsync(Ct)).LastSeq.Should().Be(30);
    }

    [Fact]
    public async Task TagNames_AreUniqueIgnoringCase_ButADeletedOneFreesTheName()
    {
        var (user, workspace) = await _server.CreateUserAsync(Ct);
        var device = Guid.CreateVersion7();
        var first = Guid.CreateVersion7();
        await PushAsync(user, device, workspace, Upsert(device, SyncEntityTypes.Tag, first, 0, TagFields("Casa")));

        var clash = (await PushAsync(user, device, workspace, Upsert(device, SyncEntityTypes.Tag, Guid.CreateVersion7(), 0, TagFields("CASA")))).Results.Single();
        clash.Error!.Code.Should().Be(SyncErrorCodes.UniqueViolation);

        await PushAsync(user, device, workspace, Delete(device, SyncEntityTypes.Tag, first, 1));
        var reuse = (await PushAsync(user, device, workspace, Upsert(device, SyncEntityTypes.Tag, Guid.CreateVersion7(), 0, TagFields("Casa")))).Results.Single();

        reuse.Outcome.Should().Be(OperationOutcome.Applied);
    }

    [Fact]
    public async Task DeletingATask_CascadesThroughTheReferenceTable()
    {
        var (user, workspace) = await _server.CreateUserAsync(Ct);
        var device = Guid.CreateVersion7();
        var task = Guid.CreateVersion7();
        var occurrence = Guid.CreateVersion7();
        await PushAsync(user, device, workspace,
            Upsert(device, SyncEntityTypes.Task, task, 0, TaskFields("Pagar boleto")),
            Upsert(device, SyncEntityTypes.Occurrence, occurrence, 0, OccurrenceFields(task)));

        await PushAsync(user, device, workspace, Delete(device, SyncEntityTypes.Task, task, 1));

        var pulled = await PullAsync(user, device, workspace, SyncCursor.Encode(2));
        pulled.Changes.Should().HaveCount(2).And.OnlyContain(change => change.Deleted);
        await using var context = Context();
        (await context.SyncReferences.CountAsync(Ct)).Should().Be(0, "o tombstone não aponta para nada");
    }

    [Fact]
    public async Task TwoUsers_DoNotSeeEachOther_EvenWithTheSameEntityId()
    {
        var (alice, aliceWorkspace) = await _server.CreateUserAsync(Ct);
        var (bob, bobWorkspace) = await _server.CreateUserAsync(Ct);
        var device = Guid.CreateVersion7();
        var sameId = Guid.CreateVersion7();

        await PushAsync(alice, device, aliceWorkspace, Upsert(device, SyncEntityTypes.Task, sameId, 0, TaskFields("Da Alice")));
        await PushAsync(bob, device, bobWorkspace, Upsert(device, SyncEntityTypes.Task, sameId, 0, TaskFields("Do Bob")));

        (await PullAsync(bob, device, bobWorkspace, SyncCursor.Start)).Changes.Single().Fields[SyncFields.Task.Title].GetString().Should().Be("Do Bob");

        var intrusion = () => PullAsync(bob, device, aliceWorkspace, SyncCursor.Start);
        var push = () => PushAsync(bob, device, aliceWorkspace, Upsert(device, SyncEntityTypes.Task, Guid.CreateVersion7(), 0, TaskFields("Invasão")));

        (await intrusion.Should().ThrowAsync<SyncServerException>()).Which.Status.Should().Be(404);
        (await push.Should().ThrowAsync<SyncServerException>()).Which.Status.Should().Be(404);
        (await _server.RunAsync(provider => provider.GetRequiredService<SyncQueries>().WorkspacesAsync(new SyncCaller(bob, device), Ct)))
            .Should().ContainSingle().Which.Id.Should().Be(bobWorkspace);
    }

    [Fact]
    public async Task ThePersonalWorkspace_IsCreatedOnce()
    {
        var (user, workspace) = await _server.CreateUserAsync(Ct);

        var again = await _server.RunAsync(provider => provider.GetRequiredService<WorkspaceProvisioner>().EnsurePersonalAsync(user, Ct));

        again.Should().Be(workspace);
        await using var context = Context();
        (await context.Workspaces.CountAsync(Ct)).Should().Be(1);
    }

    [Fact]
    public async Task Devices_ArePersisted_AndARevokedOneStaysRevoked()
    {
        var (user, _) = await _server.CreateUserAsync(Ct);
        var device = Guid.CreateVersion7();
        var caller = new SyncCaller(user, device);

        await _server.RunAsync(provider => provider.GetRequiredService<DeviceUseCases>().HelloAsync(caller, new DeviceHello("Notebook", "Windows", "1.11.0", SyncSchema.Current), Ct));
        await _server.RunAsync(provider => provider.GetRequiredService<DeviceUseCases>().RevokeAsync(caller, device, Ct));

        var active = await _server.RunAsync(provider => provider.GetRequiredService<DeviceUseCases>().IsActiveAsync(caller, Ct));
        active.Should().BeFalse();
        var list = await _server.RunAsync(provider => provider.GetRequiredService<DeviceUseCases>().ListAsync(caller, Ct));
        list.Should().ContainSingle().Which.RevokedAt.Should().NotBeNull();
    }

    private ServerDbContext Context() =>
        new(new DbContextOptionsBuilder<ServerDbContext>().UseNpgsql(_server.ConnectionString).Options);

    private Task<PushResponse> PushAsync(Guid user, Guid device, Guid workspace, params SyncOperation[] operations) =>
        _server.RunAsync(provider => provider.GetRequiredService<PushProcessor>()
            .PushAsync(new SyncCaller(user, device), workspace, new PushRequest(SyncSchema.Current, device, operations), Ct));

    private Task<PullResponse> PullAsync(Guid user, Guid device, Guid workspace, string cursor) =>
        _server.RunAsync(provider => provider.GetRequiredService<SyncQueries>()
            .PullAsync(new SyncCaller(user, device), workspace, cursor, null, Ct));

    private static SyncOperation Upsert(Guid device, string type, Guid id, long baseVersion, Dictionary<string, JsonElement> fields) =>
        new(Guid.CreateVersion7(), device, type, id, SyncOperationKind.Upsert, baseVersion, fields, Created);

    private static SyncOperation Delete(Guid device, string type, Guid id, long baseVersion) =>
        new(Guid.CreateVersion7(), device, type, id, SyncOperationKind.Delete, baseVersion, new Dictionary<string, JsonElement>());

    private static Dictionary<string, JsonElement> TaskFields(string title) => new()
    {
        [SyncFields.Task.Title] = SyncValue.String(title),
        [SyncFields.Task.Priority] = SyncValue.Integer(1),
        [SyncFields.Task.Reminder] = SyncValue.Object(
            (SyncFields.Reminder.IsEnabled, SyncValue.Boolean(false)),
            (SyncFields.Reminder.Anchor, SyncValue.Integer(0)),
            (SyncFields.Reminder.Offset, SyncValue.Duration(TimeSpan.Zero)),
            (SyncFields.Reminder.Repeat, SyncValue.Boolean(false)),
            (SyncFields.Reminder.RepeatEvery, SyncValue.Duration(TimeSpan.FromMinutes(15))),
            (SyncFields.Reminder.Channels, SyncValue.Integer(1))),
        [SyncFields.CreatedAt] = SyncValue.Instant(Created),
    };

    private static Dictionary<string, JsonElement> OccurrenceFields(Guid task) => new()
    {
        [SyncFields.Occurrence.TaskId] = SyncValue.Guid(task),
        [SyncFields.Occurrence.Schedule] = SyncValue.Object((SyncFields.Occurrence.Date, SyncValue.Null), (SyncFields.Occurrence.Time, SyncValue.Null)),
        [SyncFields.Occurrence.State] = SyncValue.Object((SyncFields.Occurrence.Status, SyncValue.Integer(0)), (SyncFields.Occurrence.CompletedAt, SyncValue.Null)),
    };

    private static Dictionary<string, JsonElement> TagFields(string name) => new()
    {
        [SyncFields.Tag.Name] = SyncValue.String(name),
        [SyncFields.Tag.ColorHex] = SyncValue.String("#00FF00"),
        [SyncFields.CreatedAt] = SyncValue.Instant(Created),
    };
}
