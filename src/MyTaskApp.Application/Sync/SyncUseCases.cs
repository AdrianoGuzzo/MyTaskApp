using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Domain;
using MyTaskApp.Sync.Contracts;
using MyTaskApp.Sync.Contracts.Definitions;

namespace MyTaskApp.Application.Sync;

/// <summary>A conta da sincronização vista pela tela.</summary>
public interface ISyncAccount
{
    bool IsAvailable { get; }

    Uri? Server { get; }

    Guid DeviceId { get; }

    Task<SyncAuthorization> BeginConnectAsync(CancellationToken cancellationToken);

    Task DisconnectAsync(CancellationToken cancellationToken);

    Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken);

    void SyncNow();

    Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken);

    Task RenameDeviceAsync(Guid deviceId, string nickname, CancellationToken cancellationToken);

    Task RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken);
}

/// <summary>
/// A conta da sincronização vista pela tela (ADR-061): conectar pelo
/// navegador, desconectar, ligar e desligar, e os dispositivos. Singleton,
/// porque o login espera o navegador por minutos, fora de qualquer escopo.
/// </summary>
public sealed class SyncAccountService(
    ISyncAuthenticator authenticator,
    ISyncApi api,
    IUseCaseRunner runner,
    IDeviceIdentity device,
    SyncScheduler scheduler,
    SyncDeviceDirectory directory,
    ILogger<SyncAccountService> logger) : ISyncAccount
{
    public bool IsAvailable => authenticator.IsAvailable;

    public Uri? Server => authenticator.ServerBaseUrl;

    public Guid DeviceId => device.DeviceId;

    /// <summary>
    /// Abre o login. A conclusão liga a instalação à conta e acorda o
    /// agendador; na primeira vez, ele traz o que o servidor já tem e só então
    /// manda o que só existe aqui.
    /// </summary>
    public async Task<SyncAuthorization> BeginConnectAsync(CancellationToken cancellationToken)
    {
        var state = await ReadAsync(cancellationToken);

        // Revogado não volta: entra como um dispositivo novo.
        if (state.State == SyncLinkState.DeviceRevoked)
        {
            device.Rotate();
        }

        var authorization = await authenticator.BeginAuthorizationAsync(cancellationToken);

        return authorization with { Completion = FinishAsync(authorization.Completion, cancellationToken) };
    }

    private async Task FinishAsync(Task login, CancellationToken cancellationToken)
    {
        try
        {
            await login;
        }
        catch (SyncApiException exception) when (exception.Kind == SyncFailureKind.DeviceRevoked)
        {
            device.Rotate();
            throw new DomainException("Este computador tinha sido revogado na sua conta. Ele foi registrado como um dispositivo novo: conecte de novo.");
        }

        var me = await api.MeAsync(cancellationToken);
        var workspaces = await api.WorkspacesAsync(cancellationToken);
        var workspace = workspaces.FirstOrDefault(candidate => candidate.IsPersonal) ?? workspaces.FirstOrDefault()
            ?? throw new DomainException("A conta não tem um espaço de dados. Tente de novo em instantes.");

        await api.HelloAsync(SyncEngine.Hello(), cancellationToken);
        var devices = await api.DevicesAsync(cancellationToken);
        directory.Update(devices);

        await runner.RunAsync<ISyncLocalStore>(
            async (store, token) =>
            {
                await store.LinkAsync(
                    new SyncAccountLink(me.User.Id, me.User.DisplayName, me.User.Email, workspace.Id, device.DeviceId, authenticator.ServerBaseUrl!),
                    token);
                await store.SaveKnownDevicesAsync(devices, token);
            },
            cancellationToken);

        logger.LogInformation("SyncConnected {WorkspaceId}", workspace.Id);
        scheduler.RequestNow();
    }

    /// <summary>Sai da conta neste computador. Nenhum dado local é apagado.</summary>
    public async Task DisconnectAsync(CancellationToken cancellationToken)
    {
        await authenticator.SignOutAsync(cancellationToken);
        await runner.RunAsync<ISyncLocalStore>((store, token) => store.UnlinkAsync(token), cancellationToken);
        logger.LogInformation("SyncDisconnected");

        // Um ciclo ocioso, sem rede: só para o indicador do cabeçalho reler o estado (ADR-063).
        scheduler.RequestNow();
    }

    /// <summary>Ligar sincroniza na hora; pausar também acorda o laço, que só avisa o indicador (ADR-063).</summary>
    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken)
    {
        await runner.RunAsync<ISyncLocalStore>((store, token) => store.SetEnabledAsync(enabled, token), cancellationToken);
        scheduler.RequestNow();
    }

    public void SyncNow() => scheduler.RequestNow();

    public async Task<IReadOnlyList<DeviceInfo>> ListDevicesAsync(CancellationToken cancellationToken)
    {
        var devices = await api.DevicesAsync(cancellationToken);
        directory.Update(devices);
        await runner.RunAsync<ISyncLocalStore>((store, token) => store.SaveKnownDevicesAsync(devices, token), cancellationToken);
        return devices;
    }

    public async Task RenameDeviceAsync(Guid deviceId, string nickname, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(nickname) || nickname.Trim().Length > SyncSchema.MaxNicknameLength)
        {
            throw new DomainException($"O apelido precisa ter de 1 a {SyncSchema.MaxNicknameLength} caracteres.");
        }

        await api.RenameDeviceAsync(deviceId, nickname.Trim(), cancellationToken);
        await ListDevicesAsync(cancellationToken);
    }

    /// <summary>Revoga outro dispositivo; revogar este é o mesmo que ele ser revogado de fora.</summary>
    public async Task RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken)
    {
        await api.RevokeDeviceAsync(deviceId, cancellationToken);

        if (deviceId == device.DeviceId)
        {
            await authenticator.SignOutAsync(cancellationToken);
            await runner.RunAsync<ISyncLocalStore>((store, token) => store.SetStateAsync(SyncLinkState.DeviceRevoked, token), cancellationToken);
            return;
        }

        await ListDevicesAsync(cancellationToken);
    }

    private Task<SyncLocalState> ReadAsync(CancellationToken cancellationToken) =>
        runner.RunAsync<ISyncLocalStore, SyncLocalState>((store, token) => store.ReadAsync(token), cancellationToken);
}

/// <summary>O que a tela de sincronização mostra no topo.</summary>
public sealed record SyncStatusView(
    bool IsAvailable,
    Uri? Server,
    SyncLocalState Local,
    SyncCounts Counts,
    bool IsRunning,
    SyncCycleResult? LastCycle);

public sealed record GetSyncStatus;

public sealed class GetSyncStatusHandler(ISyncLocalStore store, ISyncAuthenticator authenticator, IDeviceIdentity device, SyncScheduler scheduler)
{
    public async Task<SyncStatusView> HandleAsync(GetSyncStatus query, CancellationToken cancellationToken = default) =>
        new(
            authenticator.IsAvailable,
            authenticator.ServerBaseUrl,
            await store.ReadAsync(cancellationToken),
            await store.CountAsync(device.DeviceId, cancellationToken),
            scheduler.IsRunning,
            scheduler.LastResult);
}

public sealed record ListSyncConflicts;

public sealed class ListSyncConflictsHandler(ISyncLocalStore store)
{
    public Task<IReadOnlyList<SyncConflictView>> HandleAsync(ListSyncConflicts query, CancellationToken cancellationToken = default) =>
        store.ListConflictsAsync(cancellationToken);
}

/// <param name="Reapply"><c>true</c>: "Reaplicar a minha"; <c>false</c>: "Descartar".</param>
public sealed record ResolveSyncConflict(Guid ConflictId, bool Reapply);

public sealed class ResolveSyncConflictHandler(ISyncLocalStore store, ISyncTrigger trigger, IDataChangeNotifier notifier)
{
    public async Task HandleAsync(ResolveSyncConflict command, CancellationToken cancellationToken = default)
    {
        if (command.Reapply)
        {
            await store.ReapplyConflictAsync(command.ConflictId, cancellationToken);
            notifier.NotifyChanged(DataArea.Tasks | DataArea.Tags | DataArea.Time | DataArea.StickyNotes);
            trigger.Signal();
            return;
        }

        await store.DismissConflictAsync(command.ConflictId, cancellationToken);
    }
}

public sealed record SyncFailuresView(IReadOnlyList<SyncFailedOperationView> Operations, IReadOnlyList<SyncApplyFailureView> Received);

public sealed record ListSyncFailures;

public sealed class ListSyncFailuresHandler(ISyncLocalStore store)
{
    public async Task<SyncFailuresView> HandleAsync(ListSyncFailures query, CancellationToken cancellationToken = default) =>
        new(await store.ListFailedAsync(cancellationToken), await store.ListApplyFailuresAsync(cancellationToken));
}

public sealed record RetrySyncOperation(Guid OpId);

public sealed class RetrySyncOperationHandler(ISyncLocalStore store, ISyncTrigger trigger)
{
    public async Task HandleAsync(RetrySyncOperation command, CancellationToken cancellationToken = default)
    {
        await store.RetryFailedAsync(command.OpId, cancellationToken);
        trigger.Signal();
    }
}

/// <summary>Desiste da alteração local recusada: o que vale no servidor volta por cima.</summary>
public sealed record DiscardSyncOperation(Guid OpId);

public sealed class DiscardSyncOperationHandler(ISyncLocalStore store, RemoteRefetcher refetcher)
{
    public async Task HandleAsync(DiscardSyncOperation command, CancellationToken cancellationToken = default)
    {
        if (await store.DiscardFailedAsync(command.OpId, cancellationToken) is { } entity)
        {
            await refetcher.RefetchAsync([entity], cancellationToken);
        }
    }
}

/// <summary>Busca de novo o que não tinha entrado neste computador.</summary>
public sealed record RetryReceivedChanges;

public sealed class RetryReceivedChangesHandler(ISyncLocalStore store, RemoteRefetcher refetcher)
{
    public async Task HandleAsync(RetryReceivedChanges command, CancellationToken cancellationToken = default)
    {
        var failures = await store.ListApplyFailuresAsync(cancellationToken);
        await refetcher.RefetchAsync([.. failures.Select(failure => new EntityRef(failure.EntityType, failure.EntityId))], cancellationToken);
    }
}

/// <summary>
/// Os códigos de uma mudança recebida que ainda não entrou porque espera algo
/// deste computador (ADR-065), e não porque falhou. Cada ciclo tenta de novo.
/// </summary>
public static class SyncWaitingCodes
{
    /// <summary>Um comando local daqui já usa o apelido do que chegou.</summary>
    public const string LocalAliasTaken = "local_alias_taken";

    /// <summary>O botão de diretório de um comando que está esperando.</summary>
    public const string WaitingCommand = "waiting_command";

    public static bool IsWaiting(string? code) => code is LocalAliasTaken or WaitingCommand;
}

/// <summary>
/// Busca de novo o que esperava (ADR-065): renomeado o comando local, o que
/// estava de fora entra no ciclo seguinte, sem ninguém clicar em nada.
/// </summary>
public sealed record RetryWaitingReceivedChanges;

public sealed class RetryWaitingReceivedChangesHandler(ISyncLocalStore store, RemoteRefetcher refetcher)
{
    public async Task HandleAsync(RetryWaitingReceivedChanges command, CancellationToken cancellationToken = default)
    {
        var waiting = (await store.ListApplyFailuresAsync(cancellationToken))
            .Where(failure => SyncWaitingCodes.IsWaiting(failure.ErrorCode))
            .Select(failure => new EntityRef(failure.EntityType, failure.EntityId))
            .ToList();

        await refetcher.RefetchAsync(waiting, cancellationToken);
    }
}

/// <summary>O estado do servidor de algumas entidades, aplicado por cima do local, linha a linha.</summary>
public sealed class RemoteRefetcher(
    ISyncLocalStore store,
    ISyncApi api,
    IRemoteChangeApplier applier,
    ReconcileAfterRemoteChangeHandler reconcile,
    IDataChangeNotifier notifier)
{
    public async Task RefetchAsync(IReadOnlyList<EntityRef> entities, CancellationToken cancellationToken)
    {
        if (entities.Count == 0 || (await store.ReadAsync(cancellationToken)).WorkspaceId is not { } workspaceId)
        {
            return;
        }

        foreach (var chunk in entities.Chunk(SyncSchema.MaxFetchEntities))
        {
            var fetched = await api.FetchAsync(workspaceId, new FetchRequest(chunk), cancellationToken);
            var result = await applier.ApplyAsync(fetched.Changes, nextCursor: null, isolateFailures: true, cancellationToken);

            if (result.TaskIds.Count > 0 || result.MergedTagIds?.Count > 0)
            {
                await reconcile.HandleAsync(
                    new ReconcileAfterRemoteChange(result.TaskIds, result.RearmTaskIds, result.Moves, result.MergedTagIds),
                    cancellationToken);
            }

            notifier.NotifyChanged(result.Areas);
        }
    }
}

public sealed record ListCommandReviews;

public sealed class ListCommandReviewsHandler(ISyncLocalStore store, IDeviceIdentity device)
{
    public Task<IReadOnlyList<SyncCommandReviewItem>> HandleAsync(ListCommandReviews query, CancellationToken cancellationToken = default) =>
        store.ListCommandReviewsAsync(device.DeviceId, cancellationToken);
}

/// <summary>"Revisei e libero": o comando pode rodar aqui até mudar de novo em outro computador.</summary>
public sealed record ApproveCommandReview(string EntityType, Guid EntityId);

public sealed class ApproveCommandReviewHandler(ISyncLocalStore store)
{
    public Task HandleAsync(ApproveCommandReview command, CancellationToken cancellationToken = default) =>
        store.MarkCommandReviewedAsync(command.EntityType, command.EntityId, cancellationToken);
}

/// <summary>De onde veio a tarefa, para a linha da janela: "Criada em Notebook · 12/10 · alterada por último em Desktop".</summary>
public sealed record TaskOriginView(string CreatedOn, DateTimeOffset? CreatedAt, string? UpdatedOn, DateTimeOffset? UpdatedAt);

public sealed record GetTaskOrigin(Guid TaskId);

public sealed class GetTaskOriginHandler(ISyncLocalStore store, IDeviceIdentity device, SyncDeviceDirectory directory)
{
    public const string ThisComputer = "este computador";

    public const string AnotherDevice = "outro dispositivo";

    /// <summary><c>null</c> quando a tarefa nunca passou pela sincronização.</summary>
    public async Task<TaskOriginView?> HandleAsync(GetTaskOrigin query, CancellationToken cancellationToken = default)
    {
        if (await store.OriginOfAsync(SyncEntityTypes.Task, query.TaskId, cancellationToken) is not { } origin)
        {
            return null;
        }

        return new TaskOriginView(
            Name(origin.CreatedByDeviceId) ?? AnotherDevice,
            origin.OriginCreatedAt,
            Name(origin.UpdatedByDeviceId),
            origin.UpdatedAt);
    }

    private string? Name(Guid? deviceId) =>
        deviceId is null ? null
        : deviceId == device.DeviceId ? ThisComputer
        : directory.NicknameOf(deviceId) ?? AnotherDevice;
}
