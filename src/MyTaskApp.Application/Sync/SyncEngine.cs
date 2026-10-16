using System.Reflection;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Devices;
using MyTaskApp.Sync.Contracts;

namespace MyTaskApp.Application.Sync;

/// <summary>Como terminou um ciclo, para o agendador decidir quando voltar.</summary>
public enum SyncCycleStatus
{
    /// <summary>Tudo enviado e recebido.</summary>
    Completed,

    /// <summary>Nada a fazer agora: sem servidor, sem conta, ou desligado.</summary>
    Idle,

    /// <summary>Falha passageira (rede, servidor): tenta de novo com espera crescente.</summary>
    Transient,

    /// <summary>Precisa de alguém: entrar de novo, atualizar o app. O agendador para de insistir.</summary>
    Blocked,
}

public sealed record SyncCycleResult(SyncCycleStatus Status, TimeSpan? RetryAfter = null, string? Message = null)
{
    public static SyncCycleResult Idle { get; } = new(SyncCycleStatus.Idle);

    public static SyncCycleResult Completed { get; } = new(SyncCycleStatus.Completed);
}

/// <summary>Um ciclo de sincronização, do jeito que o agendador o vê.</summary>
public interface ISyncCycleRunner
{
    Task<SyncCycleResult> RunCycleAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Os apelidos dos dispositivos da conta, para a origem da tarefa dizer
/// "criada no Notebook" e não um GUID. Renovado a cada ciclo.
/// </summary>
public sealed class SyncDeviceDirectory
{
    private volatile IReadOnlyDictionary<Guid, DeviceInfo> _devices = new Dictionary<Guid, DeviceInfo>();

    public IReadOnlyDictionary<Guid, DeviceInfo> Devices => _devices;

    public void Update(IEnumerable<DeviceInfo> devices) => _devices = devices.ToDictionary(device => device.DeviceId);

    public string? NicknameOf(Guid? deviceId) =>
        deviceId is { } id && _devices.TryGetValue(id, out var device) ? device.Nickname : null;
}

/// <summary>
/// Um ciclo de sincronização (ADR-061): olá → (na primeira vez, o pull
/// completo e a varredura do que já existia) → push → pull.
/// </summary>
/// <remarks>
/// <para>
/// <b>Na primeira conexão, o pull vem antes da varredura.</b> O que o servidor
/// já tem chega com estado e não é mandado de volta; só o que existe só aqui
/// vira operação. É isso que evita subir em dobro.
/// </para>
/// <para>
/// <b>Cada página entra numa transação, com o cursor.</b> Uma página que falha
/// três vezes é aplicada linha a linha: a linha ruim fica marcada e o resto
/// segue — uma mudança estranha não trava a sincronização inteira.
/// </para>
/// <para>
/// <b>Falha passageira não é erro do usuário.</b> Rede e servidor voltam
/// sozinhos, e o outbox espera. O que precisa de alguém — login vencido,
/// dispositivo revogado, versão antiga — para o agendador e aparece na tela.
/// </para>
/// </remarks>
public sealed class SyncEngine(
    ISyncApi api,
    ISyncAuthenticator authenticator,
    IUseCaseRunner runner,
    IDeviceIdentity device,
    IDataChangeNotifier notifier,
    SyncDeviceDirectory directory,
    ILogger<SyncEngine> logger) : ISyncCycleRunner
{
    public const int PushBatchSize = 100;

    public const int PullPageSize = SyncSchema.DefaultPullLimit;

    /// <summary>Quantas vezes a mesma página falha inteira antes de ser aplicada linha a linha.</summary>
    public const int PageFailuresBeforeIsolation = 3;

    private readonly Dictionary<string, int> _pageFailures = [];

    public async Task<SyncCycleResult> RunCycleAsync(CancellationToken cancellationToken)
    {
        if (!authenticator.IsAvailable)
        {
            return SyncCycleResult.Idle;
        }

        var state = await ReadAsync(cancellationToken);

        if (state is not { State: SyncLinkState.Linked, IsEnabled: true, WorkspaceId: { } workspaceId })
        {
            return SyncCycleResult.Idle;
        }

        if (state.LinkedDeviceId != device.DeviceId)
        {
            // O banco veio de outro computador (backup restaurado, pasta copiada).
            await StoreAsync(store => store.SetStateAsync(SyncLinkState.NeedsReconnect, cancellationToken), cancellationToken);
            await StoreAsync(store => store.RecordErrorAsync("device_changed", "Este banco veio de outro computador. Conecte a conta de novo.", cancellationToken), cancellationToken);
            return new SyncCycleResult(SyncCycleStatus.Blocked, Message: "device_changed");
        }

        try
        {
            await api.HelloAsync(Hello(), cancellationToken);
            await RefreshDevicesAsync(cancellationToken);

            if (state.ClientSchemaVersion < SyncSchema.Current)
            {
                await JoinAsync(workspaceId, state, cancellationToken);
            }

            // O legado criado aqui ganha lugar antes do push, e sobe junto (ADR-062).
            await SettleAsync(cancellationToken);

            var pushed = await PushAllAsync(workspaceId, cancellationToken);
            var pulled = await PullAllAsync(workspaceId, cancellationToken);

            // O que chegou agora pode ser legado de um id antigo daqui: sobe no próximo ciclo.
            if (pulled)
            {
                await SettleAsync(cancellationToken);
            }

            await StoreAsync(store => store.RecordSuccessAsync(pushed, pulled, cancellationToken), cancellationToken);

            return SyncCycleResult.Completed;
        }
        catch (SyncApiException exception)
        {
            return await FailedAsync(exception, cancellationToken);
        }
    }

    /// <summary>
    /// A primeira vez (ou depois de o app ganhar tipos novos): tudo do servidor
    /// primeiro, e então o que só existe aqui vira operação.
    /// </summary>
    private async Task JoinAsync(Guid workspaceId, SyncLocalState state, CancellationToken cancellationToken)
    {
        logger.LogInformation("SyncJoinStarted {SchemaVersion}", state.ClientSchemaVersion);

        if (state.Cursor.Length > 0)
        {
            await StoreAsync(store => store.ResetCursorAsync(cancellationToken), cancellationToken);
        }

        // Voltando à mesma conta depois de desconectar: o que mudou aqui nesse
        // meio-tempo vira operação antes do pull, que senão gravaria o valor do
        // servidor por cima sem ninguém saber.
        if (state.HasKnownEntities)
        {
            await runner.RunAsync<ISyncReconciler, int>((reconciler, token) => reconciler.ScanAsync(token), cancellationToken);
        }

        await PullAllAsync(workspaceId, cancellationToken);

        var created = await runner.RunAsync<ISyncReconciler, int>((reconciler, token) => reconciler.ScanAsync(token), cancellationToken);

        await StoreAsync(store => store.MarkSchemaCurrentAsync(SyncSchema.Current, cancellationToken), cancellationToken);

        logger.LogInformation("SyncJoinCompleted {Operations}", created);
    }

    private async Task<bool> PushAllAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var pushed = false;

        while (true)
        {
            var operations = await runner.RunAsync<ISyncLocalStore, IReadOnlyList<SyncOperation>>(
                (store, token) => store.TakeOperationsAsync(device.DeviceId, PushBatchSize, token),
                cancellationToken);

            if (operations.Count == 0)
            {
                return pushed;
            }

            var response = await api.PushAsync(workspaceId, new PushRequest(SyncSchema.Current, device.DeviceId, operations), cancellationToken);

            var outcome = await runner.RunAsync<ISyncLocalStore, PushOutcome>(
                (store, token) => store.ApplyPushResultsAsync(response.Results, token),
                cancellationToken);

            pushed = true;
            await AfterLocalChangesAsync(outcome.TaskIds, [], outcome.Areas, cancellationToken);

            logger.LogInformation(
                "SyncPushed {Done} {Conflicts} {Failed} {Waiting}",
                outcome.Done,
                outcome.Conflicts,
                outcome.Failed,
                outcome.Waiting);

            // Só sobrou o que espera o pai: o próximo ciclo tenta, e não este em volta.
            if (!outcome.MadeProgress)
            {
                return pushed;
            }
        }
    }

    private async Task<bool> PullAllAsync(Guid workspaceId, CancellationToken cancellationToken)
    {
        var pulled = false;

        while (true)
        {
            var cursor = (await ReadAsync(cancellationToken)).Cursor;
            PullResponse page;

            try
            {
                page = await api.PullAsync(workspaceId, cursor, PullPageSize, cancellationToken);
            }
            catch (SyncApiException exception) when (exception.Kind == SyncFailureKind.CursorAhead)
            {
                // O servidor foi restaurado de um backup: recomeça do zero; o que já existe é ignorado pela versão.
                logger.LogWarning("SyncCursorAhead");
                await StoreAsync(store => store.ResetCursorAsync(cancellationToken), cancellationToken);
                continue;
            }

            var result = await ApplyPageAsync(page, cursor, cancellationToken);
            pulled |= result.Applied > 0;

            await AfterLocalChangesAsync(result.TaskIds, result.RearmTaskIds, result.Areas, cancellationToken, result.Moves, result.MergedTagIds);

            if (!page.HasMore)
            {
                return pulled;
            }
        }
    }

    private async Task<RemoteApplyResult> ApplyPageAsync(PullResponse page, string cursor, CancellationToken cancellationToken)
    {
        var isolate = _pageFailures.GetValueOrDefault(cursor) >= PageFailuresBeforeIsolation;

        try
        {
            var result = await runner.RunAsync<IRemoteChangeApplier, RemoteApplyResult>(
                (applier, token) => applier.ApplyAsync(page.Changes, page.NextCursor, isolate, token),
                cancellationToken);

            _pageFailures.Remove(cursor);
            return result;
        }
        catch (Exception exception) when (exception is not OperationCanceledException and not SyncApiException)
        {
            var failures = _pageFailures[cursor] = _pageFailures.GetValueOrDefault(cursor) + 1;
            logger.LogWarning(exception, "SyncPageApplyFailed {Failures}", failures);

            throw new SyncApiException(SyncFailureKind.Transient, "Não foi possível gravar o que veio do servidor. Tentando de novo.");
        }
    }

    private async Task AfterLocalChangesAsync(
        IReadOnlyList<Guid> taskIds,
        IReadOnlyList<Guid> rearmTaskIds,
        DataArea areas,
        CancellationToken cancellationToken,
        IReadOnlyList<PlacementMove>? moves = null,
        IReadOnlyList<Guid>? mergedTagIds = null)
    {
        if (taskIds.Count > 0 || mergedTagIds?.Count > 0)
        {
            await runner.RunAsync<ReconcileAfterRemoteChangeHandler>(
                (handler, token) => handler.HandleAsync(new ReconcileAfterRemoteChange(taskIds, rearmTaskIds, moves, mergedTagIds), token),
                cancellationToken);
        }


        if (areas != DataArea.None)
        {
            notifier.NotifyChanged(areas);
        }
    }

    private async Task<SyncCycleResult> FailedAsync(SyncApiException exception, CancellationToken cancellationToken)
    {
        logger.LogWarning("SyncCycleFailed {Kind} {Code}", exception.Kind, exception.Code);

        var (state, blocked) = exception.Kind switch
        {
            SyncFailureKind.NeedsReconnect or SyncFailureKind.Unauthorized => (SyncLinkState.NeedsReconnect, true),
            SyncFailureKind.DeviceRevoked => (SyncLinkState.DeviceRevoked, true),
            SyncFailureKind.UpgradeRequired or SyncFailureKind.Forbidden or SyncFailureKind.NotFound or SyncFailureKind.Rejected => ((SyncLinkState?)null, true),
            _ => (null, false),
        };

        if (state is { } newState)
        {
            await StoreAsync(store => store.SetStateAsync(newState, cancellationToken), cancellationToken);
        }

        await StoreAsync(store => store.RecordErrorAsync(exception.Code ?? exception.Kind.ToString(), exception.Message, cancellationToken), cancellationToken);

        return blocked
            ? new SyncCycleResult(SyncCycleStatus.Blocked, Message: exception.Message)
            : new SyncCycleResult(SyncCycleStatus.Transient, exception.RetryAfter, exception.Message);
    }

    /// <summary>Os apelidos, em memória e gravados: o "Enviar para" precisa deles sem rede (ADR-062).</summary>
    private async Task RefreshDevicesAsync(CancellationToken cancellationToken)
    {
        var devices = await api.DevicesAsync(cancellationToken);
        directory.Update(devices);
        await StoreAsync(store => store.SaveKnownDevicesAsync(devices, cancellationToken), cancellationToken);
    }

    private async Task SettleAsync(CancellationToken cancellationToken)
    {
        var settled = await runner.RunAsync<SettleDevicePlacementsHandler, int>(
            (handler, token) => handler.HandleAsync(token),
            cancellationToken);

        if (settled > 0)
        {
            logger.LogInformation("SyncPlacementsSettled {Count}", settled);
            notifier.NotifyChanged(DataArea.Tasks | DataArea.Tags);
        }
    }

    private Task<SyncLocalState> ReadAsync(CancellationToken cancellationToken) =>
        runner.RunAsync<ISyncLocalStore, SyncLocalState>((store, token) => store.ReadAsync(token), cancellationToken);

    private Task StoreAsync(Func<ISyncLocalStore, Task> action, CancellationToken cancellationToken) =>
        runner.RunAsync<ISyncLocalStore>((store, _) => action(store), cancellationToken);

    /// <summary>O "olá": plataforma e versão, para o servidor recusar versões antigas e a tela listar os dispositivos.</summary>
    public static DeviceHello Hello() => new(
        Nickname: null,
        PlatformName(),
        (Assembly.GetEntryAssembly() ?? typeof(SyncEngine).Assembly).GetName().Version?.ToString(3) ?? "0.0.0",
        SyncSchema.Current,
        Capabilities: []);

    /// <summary>O sistema, para a lista de dispositivos; o celular também aparece nela (ADR-065).</summary>
    internal static string PlatformName() =>
        OperatingSystem.IsWindows() ? "Windows"
        : OperatingSystem.IsAndroid() ? "Android"
        : OperatingSystem.IsIOS() ? "iOS"
        : OperatingSystem.IsLinux() ? "Linux"
        : OperatingSystem.IsMacOS() ? "macOS"
        : "Outro";
}
