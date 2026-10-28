using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Domain;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>
/// O despachante do agente (ADR-086, seções 4.3 e 4.5): busca a fila desta
/// máquina, decide cada pedido (<see cref="AdmitRemoteOperationHandler"/>),
/// roda o aceito no executor do tipo e conta ao servidor o estado e o
/// andamento — o diário primeiro, a rede depois.
/// </summary>
/// <remarks>
/// <para>
/// <b>Só roda depois do aceite confirmado.</b> O servidor pode ter expirado o
/// pedido entre a entrega e o aceite; rodar antes de ele concordar seria fazer
/// o que o controlador já vê como "expirado".
/// </para>
/// <para>
/// <b>O estado vai na ordem do ciclo de vida.</b> Sem rede, o diário anda
/// sozinho (aceita, rodando, terminada); quando a rede volta, o despachante
/// conta cada passo que falta, porque o servidor recusa pular.
/// </para>
/// <para>
/// <b>Nada se repete sozinho.</b> O que rodava quando o agente caiu vira
/// <see cref="RemoteOperationStatus.Interrupted"/> ao voltar; "Repetir" é
/// outra operação, pedida pela pessoa.
/// </para>
/// </remarks>
public sealed class RemoteOperationDispatcher(
    IRemoteOperationsApi remote,
    IUseCaseRunner runner,
    ISyncTrigger sync,
    IRemoteHub hub,
    IEnumerable<IRemoteStartupRecovery> recoveries,
    TimeProvider timeProvider,
    ILogger<RemoteOperationDispatcher> logger)
{
    /// <summary>De quanto em quanto o agente pergunta por uma aprovação, quando o hub não toca.</summary>
    public static readonly TimeSpan ApprovalPollInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// O menor intervalo entre dois envios de eventos da mesma operação: com
    /// saída contínua, um lote a cada meio segundo cabe na cota por dispositivo
    /// do servidor e ainda chega em menos de 1 s.
    /// </summary>
    public static readonly TimeSpan MinEventSendInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>A saída junta em lotes por este tempo antes de ir: uma linha não vira um POST.</summary>
    public static readonly TimeSpan EventFlushDelay = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// O lote também tem teto em bytes: o servidor recusa corpo acima de 1 MiB,
    /// e 200 eventos de texto cheio (com o escape do JSON) passam disso.
    /// </summary>
    public const int MaxEventBatchBytes = 512 * 1024;

    private readonly ConcurrentDictionary<Guid, RunningOperation> _running = new();

    /// <summary>Uma conversa com o servidor por operação de cada vez: os passos não se cruzam.</summary>
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _reporting = new();

    /// <summary>As operações com um envio de eventos já marcado.</summary>
    private readonly ConcurrentDictionary<Guid, byte> _flushQueued = new();

    /// <summary>Quando cada operação mandou eventos pela última vez: o piso de <see cref="MinEventSendInterval"/>.</summary>
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastEventSend = new();

    private readonly SemaphoreSlim _polling = new(1, 1);

    public int RunningCount => _running.Count;

    /// <summary>Uma operação terminou aqui e o servidor foi avisado (ou vai ser, quando a rede voltar).</summary>
    public event Action<Guid, RemoteOperationStatus>? Finished;

    /// <summary>
    /// Ao subir (4.5): o que rodava quando o agente caiu não tem como
    /// continuar a saída de onde parou, e vira <see cref="RemoteOperationStatus.Interrupted"/>,
    /// com a etapa. A aceita que não chegou a rodar fica: a próxima busca a começa.
    /// Antes, cada <see cref="IRemoteStartupRecovery"/> arruma o seu estado local.
    /// </summary>
    public async Task RecoverAsync(CancellationToken cancellationToken)
    {
        foreach (var recovery in recoveries)
        {
            await recovery.RecoverAsync(cancellationToken);
        }

        var unfinished = await JournalAsync((journal, token) => journal.ListUnfinishedAsync(token), cancellationToken);

        foreach (var entry in unfinished.Where(entry =>
            entry.Status is RemoteOperationStatus.Running or RemoteOperationStatus.WaitingApproval
            && !_running.ContainsKey(entry.OperationId)))
        {
            var summary = entry.Step is { } step
                ? $"O agente parou durante \"{step}\"."
                : "O agente parou antes de terminar.";

            await JournalAsync(
                (journal, token) => journal.SetStatusAsync(entry.OperationId, RemoteOperationStatus.Interrupted, RemoteErrorCodes.Interrupted, summary, token),
                cancellationToken);
            await AuditAsync(entry.OperationId, cancellationToken);

            logger.LogWarning("RemoteOperationInterrupted {OperationId} {Kind} {Step}", entry.OperationId, entry.Kind, entry.Step);
        }

        var before = timeProvider.GetUtcNow() - RemoteLimits.ReplayWindow;
        await JournalAsync((journal, token) => journal.PruneAsync(before, token), cancellationToken);

        await FlushAsync(cancellationToken);
    }

    /// <summary>Uma volta: conta o que falta, busca a fila e decide cada pedido.</summary>
    public async Task PollAsync(CancellationToken cancellationToken)
    {
        await _polling.WaitAsync(cancellationToken);

        try
        {
            await FlushAsync(cancellationToken);

            foreach (var operation in await remote.ListAgentOperationsAsync(cancellationToken))
            {
                await ConsiderAsync(operation, cancellationToken);
            }
        }
        finally
        {
            _polling.Release();
        }
    }

    /// <summary>Tudo o que o servidor ainda não sabe: eventos sem ack e estados não contados.</summary>
    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var unreported = await JournalAsync((journal, token) => journal.ListUnreportedAsync(token), cancellationToken);

        foreach (var entry in unreported)
        {
            await ReportAsync(entry.OperationId, cancellationToken);
        }
    }

    /// <summary>
    /// O agente fechando: cancela o que roda e espera a folga. O que não
    /// terminou a tempo fica Rodando no diário, e a próxima subida o encerra.
    /// </summary>
    public async Task StopAsync(TimeSpan grace)
    {
        var running = _running.Values.ToList();

        foreach (var operation in running)
        {
            await operation.Cancellation.CancelAsync();
        }

        try
        {
            await Task.WhenAll(running.Select(operation => operation.Completion)).WaitAsync(grace);
        }
        catch (TimeoutException)
        {
            logger.LogWarning("RemoteOperationsStillRunning {Count}", _running.Count);
        }
    }

    private async Task ConsiderAsync(AgentOperation operation, CancellationToken cancellationToken)
    {
        // O id sem conferir a assinatura: só para achar o que já roda aqui e para recusar.
        Guid? id = RemoteJws.TryParse(operation.Request, out var parts) && RemoteJws.TryRead<RemoteOperationRequest>(parts, out var unverified)
            ? unverified.OperationId
            : null;

        if (id is { } runningId && _running.TryGetValue(runningId, out var running))
        {
            if (operation.CancelRequestedAt is not null && !running.Cancellation.IsCancellationRequested)
            {
                logger.LogInformation("RemoteOperationCancelRequested {OperationId}", runningId);
                running.CancelRequested = true;
                await running.Cancellation.CancelAsync();
            }

            return;
        }

        var admission = await runner.RunAsync<AdmitRemoteOperationHandler, RemoteAdmission>(
            (handler, token) => handler.HandleAsync(new AdmitRemoteOperation(operation.Request, _running.Count), token),
            cancellationToken);

        switch (admission)
        {
            case RemoteAdmission.Admitted admitted when operation.CancelRequestedAt is not null:
                // Cancelada enquanto esperava: aceita e encerrada, sem rodar.
                await CancelUnstartedAsync(admitted.Entry.OperationId, cancellationToken);
                break;

            case RemoteAdmission.Admitted admitted:
                logger.LogInformation("RemoteOperationAccepted {OperationId} {Kind} {Controller}", admitted.Entry.OperationId, admitted.Entry.Kind, admitted.Entry.ControllerNickname);
                await StartWhenAcceptedAsync(admitted.Entry.OperationId, admitted.Request, cancellationToken);
                break;

            case RemoteAdmission.Refused { Entry: { } entry } refused:
                logger.LogWarning("RemoteOperationRejected {OperationId} {Code}", entry.OperationId, refused.Code);
                await AuditAsync(entry.OperationId, cancellationToken);
                await ReportAsync(entry.OperationId, cancellationToken);
                break;

            case RemoteAdmission.Refused refused when id is { } unsignedId:
                // Sem assinatura que confira, nada vai para o diário; o servidor só fica sabendo que não.
                logger.LogWarning("RemoteOperationRejected {OperationId} {Code}", unsignedId, refused.Code);
                await TrySendAsync(() => remote.UpdateStatusAsync(unsignedId, new OperationStatusUpdate(RemoteOperationStatus.Rejected, refused.Code), cancellationToken));
                break;

            case RemoteAdmission.Deferred deferred:
                // A tarefa criada no celular ainda vem pelo sync: acorda o laço e fica para a próxima busca.
                logger.LogInformation("RemoteOperationWaitingEntities {OperationId} {Missing}", id, deferred.Missing.Count);
                sync.Signal();
                break;

            case RemoteAdmission.Known { Entry.Status: RemoteOperationStatus.Accepted } known when operation.CancelRequestedAt is not null:
                await CancelUnstartedAsync(known.Entry.OperationId, cancellationToken);
                break;

            case RemoteAdmission.Known { Entry.Status: RemoteOperationStatus.Accepted } known:
                // Aceita aqui e ainda não rodou: a rede caiu no meio do aceite, ou o agente antes de começar.
                await StartWhenAcceptedAsync(known.Entry.OperationId, ReadRequest(known.Entry), cancellationToken);
                break;

            case RemoteAdmission.Known { Entry.HasUnreported: true } known:
                await ReportAsync(known.Entry.OperationId, cancellationToken);
                break;
        }
    }

    private async Task CancelUnstartedAsync(Guid id, CancellationToken cancellationToken)
    {
        await JournalAsync(
            (journal, token) => journal.SetStatusAsync(id, RemoteOperationStatus.Canceled, cancellationToken: token),
            cancellationToken);
        await ReportAsync(id, cancellationToken);
        Finished?.Invoke(id, RemoteOperationStatus.Canceled);
    }

    private async Task StartWhenAcceptedAsync(Guid id, RemoteOperationRequest request, CancellationToken cancellationToken)
    {
        // Sem a confirmação, não roda: a próxima busca tenta o aceite de novo.
        if (!await ReportAsync(id, cancellationToken)
            || await JournalAsync((journal, token) => journal.FindAsync(id, token), cancellationToken)
                is not { Status: RemoteOperationStatus.Accepted } entry)
        {
            return;
        }

        var running = new RunningOperation(new CancellationTokenSource());

        if (!_running.TryAdd(id, running))
        {
            running.Cancellation.Dispose();
            return;
        }

        running.Completion = Task.Run(() => RunAsync(entry, request, running), CancellationToken.None);
    }

    private async Task RunAsync(RemoteJournalEntry entry, RemoteOperationRequest request, RunningOperation running)
    {
        var id = entry.OperationId;
        var cancellation = running.Cancellation;
        RemoteOperationResult result;

        try
        {
            await JournalAsync((journal, token) => journal.SetStatusAsync(id, RemoteOperationStatus.Running, cancellationToken: token), CancellationToken.None);
            await ReportAsync(id, CancellationToken.None);

            var execution = new RemoteExecution(
                request,
                entry.ControllerNickname,
                EmitAsync,
                timeProvider,
                (ask, token) => RequestApprovalAsync(id, ask, token));

            // A auditoria grava "adria (Remoto: Pixel 8)" (ADR-059): quem lê sabe que não foi a pessoa aqui.
            using (OperationOrigin.Enter($"Remoto: {entry.ControllerNickname}"))
            {
                result = await runner.RunAsync<IEnumerable<IRemoteOperationExecutor>, RemoteOperationResult>(
                    (executors, token) => executors.FirstOrDefault(executor => executor.Kind == entry.Kind) is { } executor
                        ? executor.ExecuteAsync(execution, token)
                        : Task.FromResult(RemoteOperationResult.Failure(RemoteErrorCodes.UnknownKind, "Esta versão do agente não roda este tipo.")),
                    cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            result = new RemoteOperationResult(false, "Cancelada.");
        }
        catch (DomainException exception)
        {
            result = RemoteOperationResult.Failure(RemoteErrorCodes.PolicyRefused, exception.Message);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "RemoteOperationFailed {OperationId} {Kind}", id, entry.Kind);
            result = RemoteOperationResult.Failure(RemoteErrorCodes.ExecutionFailed, "Falhou nesta máquina; o detalhe ficou no log dela.");
        }

        // Cancelada é quem controla pedindo; o agente fechando no meio é interrompida.
        var status = (result.Succeeded, cancellation.IsCancellationRequested, running.CancelRequested) switch
        {
            (true, _, _) => RemoteOperationStatus.Succeeded,
            (false, true, true) => RemoteOperationStatus.Canceled,
            (false, true, false) => RemoteOperationStatus.Interrupted,
            _ => RemoteOperationStatus.Failed,
        };

        if (status == RemoteOperationStatus.Interrupted)
        {
            result = RemoteOperationResult.Failure(RemoteErrorCodes.Interrupted, "O agente fechou no meio.");
        }

        try
        {
            if (result.Summary is not null || result.Data is not null)
            {
                await JournalAsync(
                    (journal, token) => journal.AppendEventAsync(
                        new RemoteOperationEvent(id, 0, timeProvider.GetUtcNow(), RemoteEventKind.Result, Text: Clip(result.Summary, RemoteLimits.MaxEventTextLength), Data: result.Data),
                        token),
                    CancellationToken.None);
            }

            await JournalAsync(
                (journal, token) => journal.SetStatusAsync(id, status, result.Code, Clip(result.Summary, RemoteLimits.MaxResultSummaryLength), token),
                CancellationToken.None);
            await AuditAsync(id, CancellationToken.None);
            await ReportAsync(id, CancellationToken.None);

            logger.LogInformation("RemoteOperationFinished {OperationId} {Status} {Code}", id, status, result.Code);
        }
        catch (Exception exception)
        {
            // O diário guarda o que der; a rede conta na próxima volta, e uma queda daqui a recuperação encerra.
            logger.LogError(exception, "RemoteOperationFinishNotRecorded {OperationId}", id);
        }
        finally
        {
            _running.TryRemove(id, out _);
            cancellation.Dispose();
        }

        Finished?.Invoke(id, status);
    }

    /// <summary>
    /// Para a operação esperando um humano (seção 4.3): Esperando aprovação no
    /// servidor, o pedido selado na fila de aprovações, e a resposta conferida
    /// aqui (<see cref="RemoteApprovalVerifier"/>). Sem resposta até o prazo,
    /// ou com uma que não confere, é negar. Depois, a operação volta a Rodando
    /// e quem a pediu decide o que fazer com a negativa.
    /// </summary>
    private async Task<RemoteApprovalResult> RequestApprovalAsync(Guid operationId, RemoteApprovalAsk ask, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var lifetime = ask.Lifetime is { } wanted && wanted > TimeSpan.Zero && wanted <= RemoteLimits.MaxApprovalLifetime
            ? wanted
            : RemoteLimits.DefaultApprovalLifetime;
        var request = ApprovalHash.Seal(new ApprovalRequest(
            Guid.CreateVersion7(now),
            operationId,
            ask.Kind,
            Clip(ask.Summary, RemoteLimits.MaxApprovalSummaryLength)!,
            ask.Details,
            ask.TypedConfirmationHint,
            now + lifetime,
            string.Empty));

        using var resolved = new SemaphoreSlim(0);
        hub.ApprovalResolved += OnResolved;

        try
        {
            await JournalAsync((journal, token) => journal.SetStatusAsync(operationId, RemoteOperationStatus.WaitingApproval, cancellationToken: token), cancellationToken);
            await EmitAsync(
                new RemoteOperationEvent(operationId, 0, now, RemoteEventKind.ApprovalRequested, Text: request.Summary, Data: Describe(request.ApprovalId, request.Kind.ToString())),
                cancellationToken);
            await ReportAsync(operationId, cancellationToken);

            logger.LogInformation("RemoteApprovalRequested {OperationId} {ApprovalId} {Kind}", operationId, request.ApprovalId, request.Kind);
            var result = await WaitForAnswerAsync(request, resolved, cancellationToken);
            logger.LogInformation("RemoteApprovalResolved {OperationId} {ApprovalId} {Outcome}", operationId, request.ApprovalId, result.Outcome);

            await JournalAsync((journal, token) => journal.SetStatusAsync(operationId, RemoteOperationStatus.Running, cancellationToken: token), cancellationToken);
            await EmitAsync(
                new RemoteOperationEvent(
                    operationId,
                    0,
                    timeProvider.GetUtcNow(),
                    RemoteEventKind.ApprovalResolved,
                    Text: Explain(result),
                    Data: Describe(request.ApprovalId, result.Outcome.ToString())),
                cancellationToken);
            await ReportAsync(operationId, cancellationToken);

            return result;
        }
        finally
        {
            hub.ApprovalResolved -= OnResolved;
        }

        void OnResolved(Guid approvalId)
        {
            if (approvalId == request.ApprovalId)
            {
                resolved.Release();
            }
        }
    }

    private async Task<RemoteApprovalResult> WaitForAnswerAsync(ApprovalRequest request, SemaphoreSlim resolved, CancellationToken cancellationToken)
    {
        var posted = false;

        while (true)
        {
            try
            {
                // Pedir de novo o mesmo é seguro (o servidor devolve a mesma): uma falha de rede no primeiro não perde nada.
                var view = posted
                    ? await remote.GetApprovalAsync(request.ApprovalId, cancellationToken)
                    : await remote.RequestApprovalAsync(request, cancellationToken);
                posted = true;

                var trusted = await runner.RunAsync<ITrustedControllerRepository, IReadOnlyList<Domain.Remote.TrustedController>>(
                    (repository, token) => repository.ListAllActiveAsync(token),
                    cancellationToken);

                if (RemoteApprovalVerifier.Verify(request, view, trusted) is { } result)
                {
                    return result;
                }
            }
            catch (SyncApiException exception) when (exception.Kind is SyncFailureKind.Rejected or SyncFailureKind.NotFound or SyncFailureKind.Forbidden)
            {
                // O servidor não aceita o pedido (a operação acabou lá, ou o pedido não fecha): ninguém vai poder permitir.
                logger.LogWarning("RemoteApprovalRefused {ApprovalId} {Code}", request.ApprovalId, exception.Code);
                return new RemoteApprovalResult(RemoteApprovalOutcome.Invalid);
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                logger.LogDebug(exception, "RemoteApprovalDeferred {ApprovalId}", request.ApprovalId);
            }

            var left = request.ExpiresAt - timeProvider.GetUtcNow();

            if (left <= TimeSpan.Zero)
            {
                return new RemoteApprovalResult(RemoteApprovalOutcome.Expired);
            }

            await WaitAsync(resolved, left < ApprovalPollInterval ? left : ApprovalPollInterval, cancellationToken);
        }
    }

    /// <summary>O hub tocar ou o intervalo passar, no relógio injetado; o que sobrar é cancelado.</summary>
    private async Task WaitAsync(SemaphoreSlim resolved, TimeSpan delay, CancellationToken cancellationToken)
    {
        using var either = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var signal = resolved.WaitAsync(either.Token);
        var timeout = Task.Delay(delay, timeProvider, either.Token);

        await Task.WhenAny(signal, timeout);
        await either.CancelAsync();
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            await Task.WhenAll(signal, timeout);
        }
        catch (OperationCanceledException)
        {
            // O que perdeu a corrida.
        }
    }

    private static JsonElement Describe(Guid approvalId, string value) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["approvalId"] = approvalId.ToString("D"), ["value"] = value }, RemoteJson.Options);

    private static string Explain(RemoteApprovalResult result) => result.Outcome switch
    {
        RemoteApprovalOutcome.Allowed => $"Permitido por {result.ControllerNickname}.",
        RemoteApprovalOutcome.Denied => $"Negado por {result.ControllerNickname}.",
        RemoteApprovalOutcome.Expired => "Ninguém respondeu a tempo: negado.",
        _ => "A resposta não conferiu: negado.",
    };

    /// <summary>O diário numera e guarda; o envio junta o que chegar em <see cref="EventFlushDelay"/>.</summary>
    private async Task EmitAsync(RemoteOperationEvent draft, CancellationToken cancellationToken)
    {
        var bounded = draft with { Text = Clip(draft.Text, RemoteLimits.MaxEventTextLength) };
        await JournalAsync((journal, token) => journal.AppendEventAsync(bounded, token), cancellationToken);

        if (_flushQueued.TryAdd(draft.OperationId, 0))
        {
            _ = Task.Run(() => FlushLaterAsync(draft.OperationId), CancellationToken.None);
        }
    }

    /// <summary>
    /// A linha na trilha da tarefa do pedido (T-21), quando há tarefa e ela
    /// está aqui. Não derruba nada: sem a linha, a operação ainda conta ao servidor.
    /// </summary>
    private async Task AuditAsync(Guid operationId, CancellationToken cancellationToken)
    {
        try
        {
            if (await JournalAsync((journal, token) => journal.FindAsync(operationId, token), cancellationToken) is not { TaskId: { } taskId } entry)
            {
                return;
            }

            await runner.RunAsync<RecordRemoteOperationHandler, bool>(
                (handler, token) => handler.HandleAsync(
                    new RecordRemoteOperation(taskId, entry.Kind, entry.ControllerNickname, entry.Status, entry.ResultSummary),
                    token),
                cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            logger.LogWarning(exception, "RemoteOperationAuditFailed {OperationId}", operationId);
        }
    }

    /// <summary>Quanto esperar para mandar: junta o que vier em <see cref="EventFlushDelay"/>, e nunca antes do piso desde o último envio.</summary>
    internal static TimeSpan EventSendDelay(DateTimeOffset? lastSend, DateTimeOffset now) =>
        lastSend is { } last && last + MinEventSendInterval - now is var floor && floor > EventFlushDelay
            ? floor
            : EventFlushDelay;

    private async Task FlushLaterAsync(Guid id)
    {
        try
        {
            await Task.Delay(
                EventSendDelay(_lastEventSend.TryGetValue(id, out var last) ? last : null, timeProvider.GetUtcNow()),
                timeProvider);
            _flushQueued.TryRemove(id, out _);
            _lastEventSend[id] = timeProvider.GetUtcNow();
            await ReportAsync(id, CancellationToken.None);
        }
        catch (Exception exception)
        {
            _flushQueued.TryRemove(id, out _);
            logger.LogDebug(exception, "RemoteEventsDeferred {OperationId}", id);
        }
    }

    /// <summary>
    /// Conta ao servidor o que o diário tem e ele não: os eventos sem ack, e
    /// cada passo de estado que falta, na ordem do ciclo de vida. Os eventos
    /// vão antes do estado final: depois dele, o servidor não aceita mais nada.
    /// </summary>
    /// <returns><c>false</c> se não deu para contar tudo agora.</returns>
    private async Task<bool> ReportAsync(Guid operationId, CancellationToken cancellationToken)
    {
        var gate = _reporting.GetOrAdd(operationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);

        try
        {
            if (await JournalAsync((journal, token) => journal.FindAsync(operationId, token), cancellationToken) is not { } entry)
            {
                return true;
            }

            foreach (var step in StatusPath(entry.ReportedStatus, entry.Status))
            {
                if (RemoteOperationLifecycle.IsFinal(step) && !await SendEventsAsync(operationId, cancellationToken))
                {
                    return false;
                }

                if (!await SendStatusAsync(entry, step, cancellationToken))
                {
                    return false;
                }
            }

            return RemoteOperationLifecycle.IsFinal(entry.Status) || await SendEventsAsync(operationId, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<bool> SendStatusAsync(RemoteJournalEntry entry, RemoteOperationStatus step, CancellationToken cancellationToken)
    {
        var final = RemoteOperationLifecycle.IsFinal(step);
        var update = new OperationStatusUpdate(step, final ? entry.ResultCode : null, final ? entry.ResultSummary : null);

        try
        {
            await remote.UpdateStatusAsync(entry.OperationId, update, cancellationToken);
            await JournalAsync((journal, token) => journal.MarkReportedAsync(entry.OperationId, step, token), cancellationToken);
            return true;
        }
        catch (SyncApiException exception) when (exception.Kind is SyncFailureKind.Rejected or SyncFailureKind.NotFound)
        {
            // O servidor não aceita o passo: expirou antes do aceite, ou já está encerrada lá. Vale o dele,
            // e não se insiste — o que ficou de evento não tem mais para onde ir.
            logger.LogWarning("RemoteStatusRefused {OperationId} {Status} {Code}", entry.OperationId, step, exception.Code);

            var settled = entry.Status == RemoteOperationStatus.Accepted ? RemoteOperationStatus.Expired : entry.Status;

            await JournalAsync(async (journal, token) =>
            {
                if (settled != entry.Status)
                {
                    await journal.SetStatusAsync(entry.OperationId, settled, exception.Code ?? RemoteErrorCodes.AlreadyExpired, cancellationToken: token);
                }

                await journal.MarkReportedAsync(entry.OperationId, settled, token);
                await journal.AcknowledgeAsync(entry.OperationId, long.MaxValue, token);
            }, cancellationToken);

            return false;
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            logger.LogDebug(exception, "RemoteStatusDeferred {OperationId} {Status}", entry.OperationId, step);
            return false;
        }
    }

    private async Task<bool> SendEventsAsync(Guid operationId, CancellationToken cancellationToken)
    {
        while (true)
        {
            var pending = Fitting(await JournalAsync(
                (journal, token) => journal.PendingEventsAsync(operationId, RemoteLimits.MaxEventsPerBatch, token),
                cancellationToken));

            if (pending.Count == 0)
            {
                return true;
            }

            EventBatchAck ack;

            try
            {
                ack = await remote.AppendEventsAsync(new EventBatch(operationId, pending), cancellationToken);
            }
            catch (Exception exception) when (IsTransient(exception))
            {
                logger.LogDebug(exception, "RemoteEventsDeferred {OperationId}", operationId);
                return false;
            }

            await JournalAsync((journal, token) => journal.AcknowledgeAsync(operationId, ack.LastContiguousSeq, token), cancellationToken);

            if (ack.LastContiguousSeq < pending[^1].Seq)
            {
                // O servidor tem um buraco antes do lote (perdeu um envio): o próximo recomeça dele.
                logger.LogWarning("RemoteEventsGap {OperationId} {Acked} {Sent}", operationId, ack.LastContiguousSeq, pending[^1].Seq);
                return false;
            }
        }
    }

    /// <summary>
    /// Os passos de <paramref name="reported"/> (nulo = entregue) até
    /// <paramref name="target"/>, pelo caminho mais curto que o ciclo de vida
    /// deixa ao agente: aceita e terminada sem rede vira aceita, rodando, terminada.
    /// </summary>
    public static IReadOnlyList<RemoteOperationStatus> StatusPath(RemoteOperationStatus? reported, RemoteOperationStatus target)
    {
        var from = reported ?? RemoteOperationStatus.Dispatched;
        var previous = new Dictionary<RemoteOperationStatus, RemoteOperationStatus>();
        var queue = new Queue<RemoteOperationStatus>([from]);

        while (from != target && queue.TryDequeue(out var current))
        {
            foreach (var next in Enum.GetValues<RemoteOperationStatus>())
            {
                if (next == from || previous.ContainsKey(next) || !RemoteOperationLifecycle.CanMove(current, next, StatusAuthority.Agent))
                {
                    continue;
                }

                previous[next] = current;

                if (next == target)
                {
                    var path = new List<RemoteOperationStatus>();

                    for (var step = next; step != from; step = previous[step])
                    {
                        path.Insert(0, step);
                    }

                    return path;
                }

                queue.Enqueue(next);
            }
        }

        return [];
    }

    private Task<T> JournalAsync<T>(Func<IRemoteOperationJournal, CancellationToken, Task<T>> operation, CancellationToken cancellationToken) =>
        runner.RunAsync(operation, cancellationToken);

    private Task JournalAsync(Func<IRemoteOperationJournal, CancellationToken, Task> operation, CancellationToken cancellationToken) =>
        runner.RunAsync(operation, cancellationToken);

    private static RemoteOperationRequest ReadRequest(RemoteJournalEntry entry) =>
        RemoteJws.TryParse(new SignedEnvelope(entry.Envelope), out var parts) && RemoteJws.TryRead<RemoteOperationRequest>(parts, out var request)
            ? request
            : throw new InvalidOperationException($"O diário guarda um envelope ilegível para {entry.OperationId}.");

    /// <summary>Os primeiros eventos que cabem em <see cref="MaxEventBatchBytes"/>; pelo menos um.</summary>
    internal static IReadOnlyList<RemoteOperationEvent> Fitting(IReadOnlyList<RemoteOperationEvent> pending)
    {
        var bytes = 0;

        for (var count = 0; count < pending.Count; count++)
        {
            bytes += JsonSerializer.SerializeToUtf8Bytes(pending[count], RemoteJson.Options).Length + 1;

            if (bytes > MaxEventBatchBytes && count > 0)
            {
                return [.. pending.Take(count)];
            }
        }

        return pending;
    }

    private static string? Clip(string? text, int max) => text is { Length: var length } && length > max ? text[..max] : text;

    private static bool IsTransient(Exception exception) =>
        exception is SyncApiException or HttpRequestException or TimeoutException;

    private async Task TrySendAsync(Func<Task> call)
    {
        try
        {
            await call();
        }
        catch (Exception exception) when (IsTransient(exception))
        {
            logger.LogDebug(exception, "RemoteRejectionNotSent");
        }
    }

    private sealed class RunningOperation(CancellationTokenSource cancellation)
    {
        public CancellationTokenSource Cancellation { get; } = cancellation;

        /// <summary>Quem controla pediu; sem isto, o cancelamento é o agente fechando.</summary>
        public bool CancelRequested { get; set; }

        public Task Completion { get; set; } = Task.CompletedTask;
    }
}

/// <summary>
/// O que o agente arruma ao subir, antes de buscar a fila: o estado local que
/// ficou pela metade quando ele caiu (a conversa do Claude que dizia "respondendo").
/// </summary>
public interface IRemoteStartupRecovery
{
    Task RecoverAsync(CancellationToken cancellationToken);
}
