using System.Text.Json;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Server.Application.Tests;

/// <summary>Só o contrato entra, e com teto: campo desconhecido, entidade e workspace (SEC-006, ADR-072).</summary>
public sealed class PushLimitsTests
{
    private readonly Guid _user = Guid.CreateVersion7();

    private readonly Guid _device = Guid.CreateVersion7();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private SyncCaller Caller => new(_user, _device);

    /// <summary>O tamanho de uma tarefa recém-criada sem descrição: a base dos tetos pequenos dos testes.</summary>
    private static int BaseTaskBytes => StoredEntity.SizeOf(Ops.Task());

    [Fact]
    public async Task AFieldOutsideTheContract_RefusesTheWholeOperation_AndNothingIsStored()
    {
        var (server, workspace) = Server();
        var task = Guid.CreateVersion7();
        var fields = Ops.Task()
            .With("isAdmin", SyncValue.Boolean(true))
            .With("role", SyncValue.String("owner"))
            .With("userId", SyncValue.Guid(Guid.CreateVersion7()))
            .With("createdByUserId", SyncValue.Guid(Guid.CreateVersion7()));

        var result = await PushOneAsync(server, workspace, Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, fields));

        result.Outcome.Should().Be(OperationOutcome.Rejected);
        result.Error!.Code.Should().Be(SyncErrorCodes.UnknownField);
        result.Error.Message.Should().Contain("isAdmin").And.Contain("createdByUserId");
        SyncErrorCodes.IsRetryable(result.Error.Code).Should().BeFalse();
        server.Get(workspace, SyncEntityTypes.Task, task).Should().BeNull();
        server.LastSeqOf(workspace).Should().Be(0);
    }

    [Fact]
    public async Task AnUpdateWithAFieldOutsideTheContract_LeavesTheEntityAsItWas()
    {
        var (server, workspace) = Server();
        var task = Guid.CreateVersion7();
        await PushOneAsync(server, workspace, Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task()));
        var junk = new Dictionary<string, JsonElement>
        {
            [SyncFields.Task.Title] = SyncValue.String("Outro"),
            ["lixo0"] = SyncValue.String(new string('x', 300_000)),
        };

        var result = await PushOneAsync(server, workspace, Ops.Upsert(_device, SyncEntityTypes.Task, task, 1, junk));

        result.Error!.Code.Should().Be(SyncErrorCodes.UnknownField);
        var stored = server.Get(workspace, SyncEntityTypes.Task, task)!;
        stored.Version.Should().Be(1);
        stored.Data.Should().NotContainKey("lixo0");
        stored.Data[SyncFields.Task.Title].GetString().Should().Be("Pagar boleto");
    }

    [Fact]
    public async Task AMemberOutsideTheContract_InsideAnObject_IsRefusedToo()
    {
        var (server, workspace) = Server();
        var fields = Ops.Task().With(
            SyncFields.Task.Placement,
            SyncValue.Object((SyncFields.Placement.Kind, SyncValue.Integer(0)), ("workspaceId", SyncValue.Guid(Guid.CreateVersion7()))));

        var result = await PushOneAsync(server, workspace, Ops.Upsert(_device, SyncEntityTypes.Task, Guid.CreateVersion7(), 0, fields));

        result.Error!.Code.Should().Be(SyncErrorCodes.UnknownField);
        result.Error.Message.Should().Contain("placement.workspaceId");
    }

    [Fact]
    public async Task WhatAnOlderServerKeptOutsideTheContract_GoesAwayOnTheNextWrite()
    {
        var (server, workspace) = Server();
        var task = Guid.CreateVersion7();
        await PushOneAsync(server, workspace, Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task()));
        var legacy = server.Get(workspace, SyncEntityTypes.Task, task)!;
        legacy.Data["colorOfTheFuture"] = SyncValue.String("#123456");
        server.Seed(legacy);

        var update = new Dictionary<string, JsonElement> { [SyncFields.Task.Priority] = SyncValue.Integer(3) };
        var result = await PushOneAsync(server, workspace, Ops.Upsert(_device, SyncEntityTypes.Task, task, 1, update));

        result.Outcome.Should().Be(OperationOutcome.Applied);
        server.Get(workspace, SyncEntityTypes.Task, task)!.Data.Should().NotContainKey("colorOfTheFuture");
    }

    [Fact]
    public async Task ALongDescription_Fits_WhileTheEntityIsUnderTheCap()
    {
        var (server, workspace) = Server();
        var description = new string('x', 100_000);

        var result = await PushOneAsync(
            server,
            workspace,
            Ops.Upsert(_device, SyncEntityTypes.Task, Guid.CreateVersion7(), 0, Ops.Task().With(SyncFields.Task.Description, SyncValue.String(description))));

        result.Outcome.Should().Be(OperationOutcome.Applied);
    }

    [Fact]
    public async Task AnEntityAboveTheCap_IsRefused_OnCreateAndOnUpdate()
    {
        var (server, workspace) = Server(new PushLimits { MaxEntityBytes = BaseTaskBytes + 1_000 });
        var task = Guid.CreateVersion7();

        var tooBig = await PushOneAsync(
            server,
            workspace,
            Ops.Upsert(_device, SyncEntityTypes.Task, Guid.CreateVersion7(), 0, Ops.Task().With(SyncFields.Task.Description, Text(2_000))));
        await PushOneAsync(
            server,
            workspace,
            Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task().With(SyncFields.Task.Description, Text(500))));
        var grown = await PushOneAsync(
            server,
            workspace,
            Ops.Upsert(_device, SyncEntityTypes.Task, task, 1, new Dictionary<string, JsonElement> { [SyncFields.Task.Description] = Text(1_500) }));

        tooBig.Error!.Code.Should().Be(SyncErrorCodes.EntityTooLarge);
        SyncErrorCodes.IsRetryable(tooBig.Error.Code).Should().BeFalse();
        grown.Error!.Code.Should().Be(SyncErrorCodes.EntityTooLarge);
        var stored = server.Get(workspace, SyncEntityTypes.Task, task)!;
        stored.Version.Should().Be(1);
        stored.Data[SyncFields.Task.Description].GetString().Should().HaveLength(500);
    }

    [Fact]
    public async Task TheStoredSize_IsWhatTheMergedJsonTakes()
    {
        var (server, workspace) = Server();
        var task = Guid.CreateVersion7();

        await PushOneAsync(server, workspace, Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task()));
        await PushOneAsync(
            server,
            workspace,
            Ops.Upsert(_device, SyncEntityTypes.Task, task, 1, new Dictionary<string, JsonElement> { [SyncFields.Task.Description] = SyncValue.String("Ação") }));

        var stored = server.Get(workspace, SyncEntityTypes.Task, task)!;
        stored.DataBytes.Should().Be(StoredEntity.SizeOf(stored.Data));
        stored.DataBytes.Should().BeGreaterThan(BaseTaskBytes);
    }

    [Fact]
    public async Task TheEntityQuota_RefusesTheNextCreation_ButUpdatesAndDeletesPass()
    {
        var (server, workspace) = Server(new PushLimits { MaxWorkspaceEntities = 2 });
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        var created = await PushAsync(
            server,
            workspace,
            Ops.Upsert(_device, SyncEntityTypes.Task, first, 0, Ops.Task("Um")),
            Ops.Upsert(_device, SyncEntityTypes.Task, second, 0, Ops.Task("Dois")),
            Ops.Upsert(_device, SyncEntityTypes.Task, Guid.CreateVersion7(), 0, Ops.Task("Três")));
        var update = await PushOneAsync(
            server,
            workspace,
            Ops.Upsert(_device, SyncEntityTypes.Task, first, 1, new Dictionary<string, JsonElement> { [SyncFields.Task.Priority] = SyncValue.Integer(3) }));
        var delete = await PushOneAsync(server, workspace, Ops.Delete(_device, SyncEntityTypes.Task, second, 1));
        var afterDelete = await PushOneAsync(server, workspace, Ops.Upsert(_device, SyncEntityTypes.Task, Guid.CreateVersion7(), 0, Ops.Task("Quatro")));

        created.Select(result => result.Outcome).Should().Equal(OperationOutcome.Applied, OperationOutcome.Applied, OperationOutcome.Rejected);
        created[2].Error!.Code.Should().Be(SyncErrorCodes.QuotaExceeded);
        update.Outcome.Should().Be(OperationOutcome.Applied);
        delete.Outcome.Should().Be(OperationOutcome.Applied);
        afterDelete.Outcome.Should().Be(OperationOutcome.Applied, "excluir libera lugar na cota");
    }

    [Fact]
    public async Task TheByteQuota_RefusesWhatGrows_ButLetsShrink()
    {
        var (server, workspace) = Server(new PushLimits { MaxWorkspaceBytes = (2L * BaseTaskBytes) + 1_000 });
        var task = Guid.CreateVersion7();
        await PushOneAsync(server, workspace, Ops.Upsert(_device, SyncEntityTypes.Task, task, 0, Ops.Task().With(SyncFields.Task.Description, Text(800))));
        await PushOneAsync(server, workspace, Ops.Upsert(_device, SyncEntityTypes.Task, Guid.CreateVersion7(), 0, Ops.Task()));

        var grow = await PushOneAsync(
            server,
            workspace,
            Ops.Upsert(_device, SyncEntityTypes.Task, task, 1, new Dictionary<string, JsonElement> { [SyncFields.Task.Description] = Text(1_500) }));
        var shrink = await PushOneAsync(
            server,
            workspace,
            Ops.Upsert(_device, SyncEntityTypes.Task, task, 1, new Dictionary<string, JsonElement> { [SyncFields.Task.Description] = Text(100) }));

        grow.Error!.Code.Should().Be(SyncErrorCodes.QuotaExceeded);
        shrink.Outcome.Should().Be(OperationOutcome.Applied);
    }

    [Fact]
    public async Task AFullQuota_IsNotReplayed_ToTheSameOperationLater()
    {
        var (server, workspace) = Server(new PushLimits { MaxWorkspaceEntities = 1 });
        var first = Guid.CreateVersion7();
        await PushOneAsync(server, workspace, Ops.Upsert(_device, SyncEntityTypes.Task, first, 0, Ops.Task()));
        var waiting = Ops.Upsert(_device, SyncEntityTypes.Task, Guid.CreateVersion7(), 0, Ops.Task("Depois"));

        var refused = await PushOneAsync(server, workspace, waiting);
        await PushOneAsync(server, workspace, Ops.Delete(_device, SyncEntityTypes.Task, first, 1));
        var resent = await PushOneAsync(server, workspace, waiting);

        refused.Error!.Code.Should().Be(SyncErrorCodes.QuotaExceeded);
        resent.Outcome.Should().Be(OperationOutcome.Applied);
        resent.Duplicate.Should().BeFalse();
    }

    private static JsonElement Text(int length) => SyncValue.String(new string('x', length));

    private (InMemorySyncServer Server, Guid Workspace) Server(PushLimits? limits = null)
    {
        var server = new InMemorySyncServer(pushLimits: limits);
        return (server, server.AddWorkspace(_user));
    }

    private async Task<IReadOnlyList<OperationResult>> PushAsync(InMemorySyncServer server, Guid workspace, params SyncOperation[] operations) =>
        (await server.Push.PushAsync(Caller, workspace, new PushRequest(SyncSchema.Current, _device, operations), Ct)).Results;

    private async Task<OperationResult> PushOneAsync(InMemorySyncServer server, Guid workspace, SyncOperation operation) =>
        (await PushAsync(server, workspace, operation)).Single();
}
