using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Remote;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Tests.Fakes;
using MyTaskApp.Domain.Remote;
using MyTaskApp.Remote.Contracts;
using NSubstitute;

namespace MyTaskApp.Application.Tests.Remote;

/// <summary>
/// O agente com tudo em memória: o diário, a fila do servidor (que confere o
/// ciclo de vida como o de verdade) e um controlador com chave ES256 real.
/// </summary>
internal sealed class RemoteDispatchKit : IAsyncDisposable
{
    public static readonly Guid Workspace = Guid.CreateVersion7();

    private readonly ServiceProvider _services;

    public RemoteDispatchKit(RemoteDispatchLimits? limits = null)
    {
        Sync.ReadAsync(Arg.Any<CancellationToken>())
            .Returns(_ => SyncLocalState.Unlinked with { State = SyncLinkState.Linked, IsEnabled = true, WorkspaceId = LinkedWorkspace });

        _services = new ServiceCollection()
            .AddSingleton<TimeProvider>(Time)
            .AddSingleton<IRemoteSettingsStore>(Settings)
            .AddSingleton<ITrustedControllerRepository>(Trusted)
            .AddSingleton<IDeviceIdentity>(Identity)
            .AddSingleton(Sync)
            .AddSingleton<IRemoteOperationJournal>(Journal)
            .AddSingleton<IRemoteEntityProbe>(Entities)
            .AddSingleton<IRemoteOperationExecutor>(Executor)
            .AddSingleton(limits ?? RemoteDispatchLimits.Default)
            .AddSingleton<IRemoteOperationsApi>(Server)
            .AddSingleton<ISyncTrigger>(Trigger)
            .AddSingleton<IUseCaseRunner, ScopedUseCaseRunner>()
            .AddSingleton<ILogger<RemoteOperationDispatcher>>(NullLogger<RemoteOperationDispatcher>.Instance)
            .AddScoped<AdmitRemoteOperationHandler>()
            .AddSingleton<RemoteOperationDispatcher>()
            .AddSingleton<IRemoteHub>(Hub)
            .AddSingleton<ILogger<RemoteOperationScheduler>>(NullLogger<RemoteOperationScheduler>.Instance)
            .AddSingleton<RemoteOperationScheduler>()
            .BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });

        Dispatcher.Finished += (id, status) => FinishedOf(id).TrySetResult(status);
    }

    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<RemoteOperationStatus>> _finished = new();

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 10, 24, 12, 0, 0, TimeSpan.Zero));

    public FakeRemoteSettingsStore Settings { get; } = new();

    public FakeTrustedControllerRepository Trusted { get; } = new();

    public FakeDeviceIdentity Identity { get; } = new();

    public ISyncLocalStore Sync { get; } = Substitute.For<ISyncLocalStore>();

    public Guid LinkedWorkspace { get; set; } = Workspace;

    public InMemoryRemoteJournal Journal { get; } = new();

    public FakeEntityProbe Entities { get; } = new();

    public FakeExecutor Executor { get; } = new();

    public FakeOperationQueue Server { get; } = new();

    public CountingSyncTrigger Trigger { get; } = new();

    public Controller Phone { get; } = new("Pixel 8");

    public FakeRemoteHub Hub { get; } = new();

    public RemoteOperationScheduler Scheduler => _services.GetRequiredService<RemoteOperationScheduler>();

    public RemoteOperationDispatcher Dispatcher => _services.GetRequiredService<RemoteOperationDispatcher>();

    public Task<RemoteAdmission> AdmitAsync(SignedEnvelope envelope, int running = 0) =>
        _services.GetRequiredService<IUseCaseRunner>().RunAsync<AdmitRemoteOperationHandler, RemoteAdmission>(
            (handler, token) => handler.HandleAsync(new AdmitRemoteOperation(envelope, running), token));

    /// <summary>Esta máquina confia no controlador, como depois do pareamento.</summary>
    public TrustedController Trust(Controller controller)
    {
        var trusted = TrustedController.Approve(
            controller.DeviceId,
            controller.Nickname,
            controller.Key.Fingerprint,
            controller.Key.SubjectPublicKeyInfo,
            Guid.CreateVersion7(),
            Time.GetUtcNow());
        Trusted.All.Add(trusted);
        return trusted;
    }

    /// <summary>Um pedido válido para esta máquina; o <paramref name="change"/> estraga o que o teste quiser.</summary>
    public RemoteOperationRequest Request(Func<RemoteOperationRequest, RemoteOperationRequest>? change = null, Controller? by = null)
    {
        var now = Time.GetUtcNow();
        var request = new RemoteOperationRequest(
            Guid.CreateVersion7(),
            Workspace,
            Identity.DeviceId,
            (by ?? Phone).DeviceId,
            RemoteOperationKinds.EnvironmentInspect,
            JsonSerializer.SerializeToElement(new EnvironmentInspectionRequest(), RemoteJson.Options),
            null,
            [],
            now,
            now + RemoteLimits.DefaultAcceptanceWindow);

        return change is null ? request : change(request);
    }

    /// <summary>Assina e põe na fila do servidor.</summary>
    public SignedEnvelope Queue(RemoteOperationRequest request, Controller? by = null)
    {
        var envelope = (by ?? Phone).Sign(request);
        Server.Enqueue(request.OperationId, envelope);
        return envelope;
    }

    /// <summary>
    /// Espera o despachante terminar a operação (ela roda fora do <c>PollAsync</c>).
    /// Sem bloquear a thread: o executor pode estar rodando nela.
    /// </summary>
    public Task<RemoteOperationStatus> WaitFinishedAsync(Guid id) =>
        FinishedOf(id).Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private TaskCompletionSource<RemoteOperationStatus> FinishedOf(Guid id) =>
        _finished.GetOrAdd(id, _ => new TaskCompletionSource<RemoteOperationStatus>(TaskCreationOptions.RunContinuationsAsynchronously));

    public async ValueTask DisposeAsync()
    {
        await Scheduler.DisposeAsync();
        await Dispatcher.StopAsync(TimeSpan.FromSeconds(5));
        await _services.DisposeAsync();
        Phone.Dispose();
    }
}

/// <summary>Um controlador: o celular com a chave dele.</summary>
internal sealed class Controller(string nickname) : IDisposable
{
    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public Guid DeviceId { get; } = Guid.CreateVersion7();

    public string Nickname { get; } = nickname;

    public DevicePublicKey Key => DevicePublicKey.Es256Of(_key.ExportSubjectPublicKeyInfo());

    public SignedEnvelope Sign(RemoteOperationRequest request) => Sign(request, RemoteJws.OperationType);

    /// <summary>A resposta a uma aprovação, como o controlador assinaria; o <paramref name="change"/> estraga o que o teste quiser.</summary>
    public SignedEnvelope Answer(
        ApprovalRequest request,
        ApprovalDecision decision,
        DateTimeOffset at,
        string? typed = null,
        Func<ApprovalResponse, ApprovalResponse>? change = null)
    {
        var response = new ApprovalResponse(request.ApprovalId, request.OperationId, request.RequestHash, decision, typed, false, at);
        return Sign(change is null ? response : change(response), RemoteJws.ApprovalType);
    }

    private SignedEnvelope Sign<T>(T payload, string type)
    {
        var input = RemoteJws.SigningInput(payload, Key.Fingerprint, type);
        var signature = _key.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return RemoteJws.Compose(input, Base64Url.EncodeToString(signature));
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// A fila do servidor em memória, com as regras que importam ao agente: o
/// ciclo de vida (repetir o estado é nada, pular é recusa), o aceite depois do
/// prazo vira expirada, e o ack é o último <c>Seq</c> sem buraco.
/// </summary>
internal sealed class FakeOperationQueue : IRemoteOperationsApi
{
    private readonly Lock _gate = new();

    public Dictionary<Guid, (SignedEnvelope Envelope, RemoteOperationStatus Status, DateTimeOffset? CancelRequestedAt)> Operations { get; } = [];

    public Dictionary<Guid, List<OperationStatusUpdate>> Updates { get; } = [];

    public Dictionary<Guid, SortedDictionary<long, RemoteOperationEvent>> Events { get; } = [];

    /// <summary>Sem rede: toda chamada falha como falharia o HTTP.</summary>
    public bool Offline { get; set; }

    /// <summary>O servidor expirou estas antes do aceite chegar.</summary>
    public HashSet<Guid> ExpireOnAccept { get; } = [];

    public void Enqueue(Guid id, SignedEnvelope envelope)
    {
        lock (_gate)
        {
            Operations[id] = (envelope, RemoteOperationStatus.Queued, null);
        }
    }

    public void RequestCancel(Guid id)
    {
        lock (_gate)
        {
            Operations[id] = Operations[id] with { CancelRequestedAt = DateTimeOffset.UtcNow };
        }
    }

    public RemoteOperationStatus StatusOf(Guid id)
    {
        lock (_gate)
        {
            return Operations[id].Status;
        }
    }

    public IReadOnlyList<RemoteOperationStatus> StatusesOf(Guid id)
    {
        lock (_gate)
        {
            return Updates.TryGetValue(id, out var updates) ? [.. updates.Select(update => update.Status)] : [];
        }
    }

    public IReadOnlyList<RemoteOperationEvent> EventsOf(Guid id)
    {
        lock (_gate)
        {
            return Events.TryGetValue(id, out var events) ? [.. events.Values] : [];
        }
    }

    public Task<IReadOnlyList<AgentOperation>> ListAgentOperationsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfOffline();

            foreach (var id in Operations.Where(pair => pair.Value.Status == RemoteOperationStatus.Queued).Select(pair => pair.Key).ToList())
            {
                Operations[id] = Operations[id] with { Status = RemoteOperationStatus.Dispatched };
            }

            IReadOnlyList<AgentOperation> listed =
            [
                .. Operations.Values
                    .Where(operation => !RemoteOperationLifecycle.IsFinal(operation.Status))
                    .Select(operation => new AgentOperation(operation.Envelope, operation.Status, operation.CancelRequestedAt)),
            ];

            return Task.FromResult(listed);
        }
    }

    public Task<RemoteOperationView> UpdateStatusAsync(Guid operationId, OperationStatusUpdate update, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfOffline();

            if (!Operations.TryGetValue(operationId, out var operation))
            {
                throw new SyncApiException(SyncFailureKind.NotFound, "Não encontrado.");
            }

            if (update.Status == RemoteOperationStatus.Accepted && ExpireOnAccept.Contains(operationId))
            {
                Operations[operationId] = operation with { Status = RemoteOperationStatus.Expired };
                throw new SyncApiException(SyncFailureKind.Rejected, "Expirada.", RemoteErrorCodes.AlreadyExpired);
            }

            // Como o de verdade: aceitar ou recusar da fila (a entrega voltou por tempo) conta como da entregue.
            var from = operation.Status == RemoteOperationStatus.Queued ? RemoteOperationStatus.Dispatched : operation.Status;

            if (operation.Status != update.Status)
            {
                if (!RemoteOperationLifecycle.CanMove(from, update.Status, StatusAuthority.Agent))
                {
                    throw new SyncApiException(SyncFailureKind.Rejected, "Transição inválida.", RemoteErrorCodes.InvalidTransition);
                }

                Operations[operationId] = operation with { Status = update.Status };
                (Updates.TryGetValue(operationId, out var updates) ? updates : Updates[operationId] = []).Add(update);
            }

            return Task.FromResult(View(operationId));
        }
    }

    public Task<EventBatchAck> AppendEventsAsync(EventBatch batch, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfOffline();

            var events = Events.TryGetValue(batch.OperationId, out var stored) ? stored : Events[batch.OperationId] = [];

            foreach (var item in batch.Events)
            {
                events.TryAdd(item.Seq, item);
            }

            return Task.FromResult(new EventBatchAck(RemoteEventSequence.LastContiguous(0, events.Keys)));
        }
    }

    /// <summary>Põe na fila como veio; quem confere a assinatura é o agente.</summary>
    public Task<RemoteOperationView> CreateAsync(SignedEnvelope request, CancellationToken cancellationToken)
    {
        RemoteJws.TryParse(request, out var parts).Should().BeTrue();
        var id = JsonSerializer.Deserialize<RemoteOperationRequest>(parts.Payload, RemoteJson.Options)!.OperationId;
        Enqueue(id, request);

        lock (_gate)
        {
            return Task.FromResult(View(id));
        }
    }

    public Task<RemoteOperationView> GetAsync(Guid operationId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Task<RemoteEventPage> ListEventsAsync(Guid operationId, long after, int? limit, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<RemoteOperationView> CancelAsync(Guid operationId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public List<AgentCatalog> Catalogs { get; } = [];

    public Task<AgentCatalog> PublishCatalogAsync(AgentCatalog catalog, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            Catalogs.Add(catalog);
            return Task.FromResult(catalog);
        }
    }

    public Task<AgentCatalog> GetCatalogAsync(Guid deviceId, CancellationToken cancellationToken) => throw new NotSupportedException();

    public Dictionary<Guid, ApprovalView> Approvals { get; } = [];

    /// <summary>Quantas vezes o agente perguntou por uma aprovação.</summary>
    public int ApprovalReads { get; private set; }

    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ApprovalRequest>> _asked = new();

    /// <summary>A aprovação que o agente pediu para a operação.</summary>
    public Task<ApprovalRequest> AskedAsync(Guid operationId) =>
        AskedOf(operationId).Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);

    private TaskCompletionSource<ApprovalRequest> AskedOf(Guid operationId) =>
        _asked.GetOrAdd(operationId, _ => new TaskCompletionSource<ApprovalRequest>(TaskCreationOptions.RunContinuationsAsynchronously));

    public Task<ApprovalView> RequestApprovalAsync(ApprovalRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfOffline();

            // Como o de verdade: só selado, só de operação viva, e o mesmo pedido de novo devolve o mesmo.
            if (!ApprovalHash.IsSealed(request) || RemoteOperationLifecycle.IsFinal(Operations[request.OperationId].Status))
            {
                throw new SyncApiException(SyncFailureKind.Rejected, "Recusado.", RemoteErrorCodes.HashMismatch);
            }

            if (!Approvals.TryGetValue(request.ApprovalId, out var view))
            {
                view = Approvals[request.ApprovalId] = new ApprovalView(request, Guid.Empty, ApprovalState.Pending, null, null, null);
            }

            AskedOf(request.OperationId).TrySetResult(request);
            return Task.FromResult(view);
        }
    }

    public Task<ApprovalView> GetApprovalAsync(Guid approvalId, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            ApprovalReads++;
            return Task.FromResult(Approvals[approvalId]);
        }
    }

    public Task<IReadOnlyList<ApprovalView>> ListPendingApprovalsAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            IReadOnlyList<ApprovalView> pending = [.. Approvals.Values.Where(view => view.State == ApprovalState.Pending)];
            return Task.FromResult(pending);
        }
    }

    /// <summary>Guarda a resposta com o estado da decisão que vem nela (o servidor de verdade confere a assinatura; aqui, quem confere é o agente).</summary>
    public Task<ApprovalView> RespondApprovalAsync(Guid approvalId, SignedEnvelope response, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ThrowIfOffline();
            RemoteJws.TryParse(response, out var parts);
            var decision = JsonSerializer.Deserialize<ApprovalResponse>(parts.Payload, RemoteJson.Options)!.Decision;
            var view = Approvals[approvalId] = Approvals[approvalId] with
            {
                State = decision == ApprovalDecision.Allow ? ApprovalState.Allowed : ApprovalState.Denied,
                Response = response,
                RespondedAt = DateTimeOffset.UtcNow,
            };
            return Task.FromResult(view);
        }
    }

    /// <summary>O prazo passou no servidor, sem resposta.</summary>
    public void ExpireApproval(Guid approvalId)
    {
        lock (_gate)
        {
            Approvals[approvalId] = Approvals[approvalId] with { State = ApprovalState.Expired };
        }
    }

    private RemoteOperationView View(Guid id) =>
        new(id, Guid.Empty, Guid.Empty, Guid.Empty, RemoteOperationKinds.EnvironmentInspect, null, Operations[id].Status,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null, null, null, 0);

    private void ThrowIfOffline()
    {
        if (Offline)
        {
            throw new SyncApiException(SyncFailureKind.Transient, "Sem conexão com o servidor de sincronização.");
        }
    }
}

/// <summary>Um tipo de operação que o teste programa.</summary>
internal sealed class FakeExecutor : IRemoteOperationExecutor
{
    private int _runs;

    public string Kind { get; set; } = RemoteOperationKinds.EnvironmentInspect;

    public string? Refusal { get; set; }

    public Func<RemoteExecution, CancellationToken, Task<RemoteOperationResult>> Behavior { get; set; } =
        (_, _) => Task.FromResult(RemoteOperationResult.Success("Tudo certo."));

    public int Runs => _runs;

    public string? SeenOrigin { get; private set; }

    public Task<string?> CheckAsync(RemoteOperationRequest request, CancellationToken cancellationToken) => Task.FromResult(Refusal);

    public Task<RemoteOperationResult> ExecuteAsync(RemoteExecution execution, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _runs);
        SeenOrigin = OperationOrigin.Current;
        return Behavior(execution, cancellationToken);
    }
}

/// <summary>A campainha que o teste toca.</summary>
internal sealed class FakeRemoteHub : IRemoteHub
{
    public event Action<Guid>? OperationAvailable;

    public event Action<Guid>? CancelRequested;

    public event Action<Guid>? ApprovalResolved;

    public bool IsConnected { get; private set; }

    public int Connects { get; private set; }

    public void Ring(Guid id) => OperationAvailable?.Invoke(id);

    public void RingCancel(Guid id) => CancelRequested?.Invoke(id);

    public void RingApproval(Guid approvalId) => ApprovalResolved?.Invoke(approvalId);

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (!IsConnected)
        {
            Connects++;
        }

        IsConnected = true;
        return Task.CompletedTask;
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        IsConnected = false;
        return Task.CompletedTask;
    }
}

/// <summary>O catálogo que o teste monta, como o banco o leria.</summary>
internal sealed class FakeCatalogQuery : IRemoteCatalogQuery
{
    public List<CatalogDirectory> Directories { get; } = [];

    public List<CatalogCommand> Commands { get; } = [];

    public Task<AgentCatalog> ReadAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new AgentCatalog(string.Empty, [.. Directories], [.. Commands], [], []));
}

internal sealed class FakeEntityProbe : IRemoteEntityProbe
{
    public HashSet<Guid> Known { get; } = [];

    public Task<IReadOnlyList<Guid>> MissingAsync(IReadOnlyCollection<Guid> entityIds, CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<Guid>>([.. entityIds.Where(id => !Known.Contains(id))]);
}

internal sealed class CountingSyncTrigger : ISyncTrigger
{
    private int _signals;

    public int Signals => _signals;

    public event Action? Signaled;

    public void Signal()
    {
        Interlocked.Increment(ref _signals);
        Signaled?.Invoke();
    }

    public Task WaitAsync(CancellationToken cancellationToken) => Task.Delay(Timeout.Infinite, cancellationToken);
}

/// <summary>O diário em memória, com as mesmas regras do SQLite: escrita por campo e ack que não volta.</summary>
internal sealed class InMemoryRemoteJournal : IRemoteOperationJournal
{
    private readonly Lock _gate = new();
    private readonly Dictionary<Guid, RemoteJournalEntry> _entries = [];
    private readonly Dictionary<Guid, SortedDictionary<long, RemoteOperationEvent>> _events = [];

    public IReadOnlyList<RemoteJournalEntry> All
    {
        get
        {
            lock (_gate)
            {
                return [.. _entries.Values];
            }
        }
    }

    /// <summary>Põe uma entrada como estava antes de uma queda.</summary>
    public void Seed(RemoteJournalEntry entry)
    {
        lock (_gate)
        {
            _entries[entry.OperationId] = entry;
        }
    }

    public Task<RemoteJournalEntry?> FindAsync(Guid operationId, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_entries.GetValueOrDefault(operationId));
        }
    }

    public Task<bool> TryAddAsync(RemoteJournalEntry entry, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_entries.TryAdd(entry.OperationId, entry));
        }
    }

    public Task SetStatusAsync(Guid operationId, RemoteOperationStatus status, string? code = null, string? summary = null, CancellationToken cancellationToken = default) =>
        Change(operationId, entry => entry with { Status = status, ResultCode = code, ResultSummary = summary });

    public Task MarkReportedAsync(Guid operationId, RemoteOperationStatus status, CancellationToken cancellationToken = default) =>
        Change(operationId, entry => entry with { ReportedStatus = status });

    public Task<RemoteOperationEvent> AppendEventAsync(RemoteOperationEvent draft, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            var entry = _entries[draft.OperationId];
            var stored = draft with { Seq = entry.NextSeq };
            _entries[draft.OperationId] = entry with
            {
                NextSeq = entry.NextSeq + 1,
                Step = draft.Kind == RemoteEventKind.StepStarted ? draft.Step : entry.Step,
            };
            (_events.TryGetValue(draft.OperationId, out var events) ? events : _events[draft.OperationId] = [])[stored.Seq] = stored;
            return Task.FromResult(stored);
        }
    }

    public Task<IReadOnlyList<RemoteOperationEvent>> PendingEventsAsync(Guid operationId, int limit, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<RemoteOperationEvent>>(
                _events.TryGetValue(operationId, out var events) ? [.. events.Values.Take(limit)] : []);
        }
    }

    public Task AcknowledgeAsync(Guid operationId, long lastContiguousSeq, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            if (_events.TryGetValue(operationId, out var events))
            {
                foreach (var seq in events.Keys.Where(seq => seq <= lastContiguousSeq).ToList())
                {
                    events.Remove(seq);
                }
            }

            var entry = _entries[operationId];
            _entries[operationId] = entry with { AckedSeq = Math.Max(entry.AckedSeq, Math.Min(lastContiguousSeq, entry.NextSeq - 1)) };
            return Task.CompletedTask;
        }
    }

    public Task<IReadOnlyList<RemoteJournalEntry>> ListUnfinishedAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<RemoteJournalEntry>>([.. _entries.Values.Where(entry => !entry.IsFinal)]);
        }
    }

    public Task<IReadOnlyList<RemoteJournalEntry>> ListUnreportedAsync(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<RemoteJournalEntry>>([.. _entries.Values.Where(entry => entry.HasUnreported)]);
        }
    }

    public Task<int> CountFromControllerSinceAsync(Guid controllerDeviceId, DateTimeOffset since, CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            return Task.FromResult(_entries.Values.Count(entry => entry.ControllerDeviceId == controllerDeviceId && entry.ReceivedAt >= since));
        }
    }

    public Task<int> PruneAsync(DateTimeOffset before, CancellationToken cancellationToken = default) => Task.FromResult(0);

    private Task Change(Guid id, Func<RemoteJournalEntry, RemoteJournalEntry> change)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(id, out var entry))
            {
                _entries[id] = change(entry);
            }

            return Task.CompletedTask;
        }
    }
}
