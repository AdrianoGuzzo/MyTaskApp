using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Server.Application;

/// <summary>Manda uma mensagem do hub para uma conexão só: a de quem segue um terminal.</summary>
public interface IRemoteTerminalTransport
{
    Task ToConnectionAsync(string connectionId, string message, object?[] args, CancellationToken cancellationToken);
}

/// <summary>Sem hub, ninguém recebe.</summary>
internal sealed class NullRemoteTerminalTransport : IRemoteTerminalTransport
{
    public static readonly NullRemoteTerminalTransport Instance = new();

    public Task ToConnectionAsync(string connectionId, string message, object?[] args, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// O terminal remoto pelo hub (ADR-099). O servidor só repassa: autoriza pela
/// operação <c>terminal.attach</c> gravada (quem a pediu segue e digita; a
/// máquina alvo publica), segura uma fila limitada por conexão e não grava
/// nada da saída nem da entrada. Quem confere a entrada é o agente.
/// </summary>
public sealed partial class RemoteTerminalUseCases(
    IRemoteDeviceStore remote,
    IRemoteOperationStore operations,
    RemoteTerminalRelay relay,
    RemoteTerminalDirectory directory,
    IRemoteNotifier notifier,
    TimeProvider timeProvider,
    ILogger<RemoteTerminalUseCases> logger)
{
    /// <summary>Quem pediu a conexão passa a receber a saída dela nesta conexão do hub.</summary>
    public async Task SubscribeAsync(SyncCaller caller, string connectionId, Guid attachId, CancellationToken cancellationToken)
    {
        var attach = await AuthorizeAsync(caller, attachId, asTarget: false, cancellationToken);
        relay.Subscribe(attach, connectionId);
        LogSubscribed(caller.UserId, attachId);
    }

    /// <summary>Um lote de entrada vai à máquina alvo como chegou: assinado pela chave da conexão, que só o agente confere.</summary>
    public async Task SendAsync(SyncCaller caller, Guid attachId, SignedEnvelope input, CancellationToken cancellationToken)
    {
        if (input.Jws is not { Length: > 0 and <= RemoteJws.MaxLength })
        {
            throw SyncServerException.BadRequest("Entrada de terminal sem forma.");
        }

        var attach = await AuthorizeAsync(caller, attachId, asTarget: false, cancellationToken);
        await notifier.ToDeviceAsync(caller.UserId, attach.TargetDeviceId, RemoteHubMessages.TerminalInput, attachId, input);
    }

    /// <summary>A máquina alvo manda saída, estado ou aviso para quem segue a conexão.</summary>
    public async Task PublishAsync(SyncCaller caller, string connectionId, RemoteTerminalFrame frame, CancellationToken cancellationToken)
    {
        if (frame.Data is { Length: > RemoteLimits.MaxTerminalFrameData } || !IsKnownKind(frame.Kind))
        {
            throw SyncServerException.BadRequest("Quadro de terminal sem forma.");
        }

        var attach = await AuthorizeAsync(caller, frame.AttachId, asTarget: true, cancellationToken);
        relay.Publish(attach, connectionId, frame);
    }

    /// <summary>A máquina alvo encerrou a conexão: quem seguia fica sabendo, e a fila acaba.</summary>
    public async Task CloseAsync(SyncCaller caller, Guid attachId, string? reason, CancellationToken cancellationToken)
    {
        var attach = await AuthorizeAsync(caller, attachId, asTarget: true, cancellationToken);
        var text = ReceivedText.Clean(reason)?.Trim();
        relay.Close(attach, text is { Length: > 0 and <= RemoteLimits.MaxResultSummaryLength } ? text : "A conexão com o terminal foi encerrada.");
    }

    /// <summary>
    /// O agente da máquina relata todas as sessões do terminal dele: guardadas
    /// em memória, e a conta é avisada se algo mudou.
    /// </summary>
    public async Task<RemoteTerminalSessionAck> ReportAsync(SyncCaller caller, RemoteTerminalSessionBatch batch, CancellationToken cancellationToken)
    {
        Validate(batch);

        var state = (await remote.ListAsync(caller.UserId, cancellationToken)).SingleOrDefault(device => device.DeviceId == caller.DeviceId)
            ?? throw new SyncServerException(RemoteErrorCodes.NotRegistered, 409, "Registre este dispositivo na orquestração antes de relatar terminais.");

        if (!state.Roles.HasFlag(DeviceRoles.Executor))
        {
            throw new SyncServerException(RemoteErrorCodes.NotAnExecutor, 409, "Só uma máquina que aceita controle remoto relata terminais.");
        }

        if (directory.Report(caller.UserId, caller.DeviceId, batch.Sessions, timeProvider.GetUtcNow()))
        {
            await notifier.ToAccountAsync(caller.UserId, RemoteHubMessages.TerminalsChanged, caller.DeviceId);
        }

        return new RemoteTerminalSessionAck(batch.Sessions.Count);
    }

    /// <summary>As sessões do terminal da conta, das máquinas que deram notícias há pouco.</summary>
    public RemoteTerminalSessionList List(SyncCaller caller) =>
        new(directory.List(caller.UserId, timeProvider.GetUtcNow()));

    private static bool IsKnownKind(string kind) =>
        kind is RemoteTerminalFrameKinds.Output or RemoteTerminalFrameKinds.State or RemoteTerminalFrameKinds.Notice or RemoteTerminalFrameKinds.Refusal;

    private static void Validate(RemoteTerminalSessionBatch batch)
    {
        if (batch.Sessions is null || batch.Sessions.Count > RemoteLimits.MaxTerminalSessionsPerBatch)
        {
            throw SyncServerException.BadRequest($"No máximo {RemoteLimits.MaxTerminalSessionsPerBatch} sessões do terminal por relato.");
        }

        foreach (var session in batch.Sessions)
        {
            if (session.SessionId == Guid.Empty || session.TaskId == Guid.Empty
                || session.ActivityMessage is { Length: > RemoteLimits.MaxSessionActivityLength }
                || ReceivedText.HasControlOrFormat(session.ActivityMessage))
            {
                throw SyncServerException.BadRequest("Sessão do terminal sem forma.");
            }
        }

        if (batch.Sessions.Select(session => session.SessionId).Distinct().Count() != batch.Sessions.Count)
        {
            throw SyncServerException.BadRequest("Sessão do terminal repetida no relato.");
        }
    }

    /// <summary>
    /// A conexão é uma operação <c>terminal.attach</c> desta conta, ainda viva.
    /// Quem segue e digita é a máquina que a pediu; quem publica, a alvo.
    /// </summary>
    private async Task<RemoteTerminalAttach> AuthorizeAsync(SyncCaller caller, Guid attachId, bool asTarget, CancellationToken cancellationToken)
    {
        var attach = relay.Find(attachId);

        if (attach is null)
        {
            // Uma conexão nova só nasce no prazo do pedido assinado; a encerrada
            // fica lembrada até esse prazo passar, para não renascer.
            var operation = await operations.FindAsync(caller.UserId, attachId, cancellationToken);
            var now = timeProvider.GetUtcNow();

            if (operation is not { Kind: RemoteOperationKinds.TerminalAttach }
                || operation.NotAfter < now
                || operation.Status is RemoteOperationStatus.Failed or RemoteOperationStatus.Canceled
                    or RemoteOperationStatus.Rejected or RemoteOperationStatus.Expired or RemoteOperationStatus.Interrupted)
            {
                LogRefused(caller.UserId, attachId, "operation");
                throw SyncServerException.Forbidden("Esta conexão de terminal não existe ou já acabou.");
            }

            attach = relay.Remember(
                new RemoteTerminalAttach(attachId, operation.UserId, operation.TargetDeviceId, operation.RequestedByDeviceId, operation.NotAfter),
                now);
        }

        var allowed = attach.UserId == caller.UserId
            && (asTarget ? attach.TargetDeviceId == caller.DeviceId : attach.RequestedByDeviceId == caller.DeviceId);

        if (!allowed || attach.IsClosed)
        {
            LogRefused(caller.UserId, attachId, asTarget ? "target" : "requester");
            throw SyncServerException.Forbidden("Esta conexão de terminal não é desta máquina.");
        }

        return attach;
    }

    [LoggerMessage(EventId = 4801, Level = LogLevel.Information, Message = "RemoteTerminalSubscribed {UserId} {AttachId}")]
    private partial void LogSubscribed(Guid userId, Guid attachId);

    [LoggerMessage(EventId = 4802, Level = LogLevel.Warning, Message = "RemoteTerminalRefused {UserId} {AttachId} {Role}")]
    private partial void LogRefused(Guid userId, Guid attachId, string role);
}

/// <summary>Uma conexão de terminal que o servidor repassa, e quem pode o quê nela.</summary>
public sealed class RemoteTerminalAttach(Guid attachId, Guid userId, Guid targetDeviceId, Guid requestedByDeviceId, DateTimeOffset notAfter)
{
    private readonly Lock _gate = new();

    private readonly Channel<(string Message, object?[] Args, int Bytes)> _queue =
        Channel.CreateUnbounded<(string, object?[], int)>(new UnboundedChannelOptions { SingleReader = true });

    private long _pendingBytes;

    private bool _gapped;

    public Guid AttachId { get; } = attachId;

    public Guid UserId { get; } = userId;

    public Guid TargetDeviceId { get; } = targetDeviceId;

    public Guid RequestedByDeviceId { get; } = requestedByDeviceId;

    /// <summary>O prazo do pedido assinado: depois dele, a conexão sem quem siga, ou encerrada, é esquecida.</summary>
    public DateTimeOffset NotAfter { get; } = notAfter;

    /// <summary>A conexão do hub de quem segue; nula até ele se inscrever.</summary>
    public string? SubscriberConnectionId { get; private set; }

    /// <summary>A conexão do hub do agente que publica.</summary>
    public string? PublisherConnectionId { get; private set; }

    public bool IsClosed { get; private set; }

    public long PendingBytes => Interlocked.Read(ref _pendingBytes);

    internal ChannelReader<(string Message, object?[] Args, int Bytes)> Queue => _queue.Reader;

    internal bool SetSubscriber(string connectionId)
    {
        lock (_gate)
        {
            var first = SubscriberConnectionId is null;
            SubscriberConnectionId = connectionId;
            return first;
        }
    }

    internal void SetPublisher(string connectionId)
    {
        lock (_gate)
        {
            PublisherConnectionId = connectionId;
        }
    }

    /// <summary>
    /// Põe na fila, contando bytes. Passou do teto, a saída na fila é jogada
    /// fora e vai um aviso de lacuna; até chegar um retrato, a saída comum
    /// também é descartada (desenhada sem o que faltou, a tela ficaria errada).
    /// </summary>
    internal void Enqueue(RemoteTerminalFrame frame)
    {
        var bytes = (frame.Data?.Length ?? 0) + (frame.Text?.Length ?? 0) + 256;

        lock (_gate)
        {
            if (IsClosed)
            {
                return;
            }

            var output = frame.Kind == RemoteTerminalFrameKinds.Output;

            if (output && frame.Reset)
            {
                _gapped = false;
            }
            else if (output && _gapped)
            {
                return;
            }

            if (PendingBytes + bytes > RemoteLimits.MaxTerminalPendingBytes)
            {
                Drain();
                _gapped = true;
                Write(RemoteHubMessages.TerminalGap, [AttachId], 64);

                if (output)
                {
                    return;
                }
            }

            Write(RemoteHubMessages.TerminalFrame, [frame], bytes);
        }
    }

    internal void Close(string reason)
    {
        lock (_gate)
        {
            if (IsClosed)
            {
                return;
            }

            Write(RemoteHubMessages.TerminalClosed, [AttachId, reason], 64);
            IsClosed = true;
            _queue.Writer.TryComplete();
        }
    }

    internal void Sent(int bytes) => Interlocked.Add(ref _pendingBytes, -bytes);

    private void Write(string message, object?[] args, int bytes)
    {
        Interlocked.Add(ref _pendingBytes, bytes);
        _queue.Writer.TryWrite((message, args, bytes));
    }

    private void Drain()
    {
        while (_queue.Reader.TryRead(out var item))
        {
            Interlocked.Add(ref _pendingBytes, -item.Bytes);
        }
    }
}

/// <summary>
/// As conexões de terminal abertas neste servidor (uma instância só, sem
/// backplane). Para cada uma, uma fila e um envio de cada vez para quem segue:
/// um controlador lento não segura o agente nem os outros.
/// </summary>
public sealed partial class RemoteTerminalRelay(
    IRemoteTerminalTransport transport,
    IRemoteNotifier notifier,
    ILogger<RemoteTerminalRelay> logger)
{
    private readonly ConcurrentDictionary<Guid, RemoteTerminalAttach> _attaches = new();

    public int Count => _attaches.Count;

    public RemoteTerminalAttach? Find(Guid attachId) => _attaches.GetValueOrDefault(attachId);

    /// <summary>Lembra a conexão nova, e esquece as encerradas ou nunca seguidas cujo prazo passou.</summary>
    public RemoteTerminalAttach Remember(RemoteTerminalAttach attach, DateTimeOffset now)
    {
        foreach (var old in _attaches.Values)
        {
            if (old.NotAfter < now && (old.IsClosed || old.SubscriberConnectionId is null) && _attaches.TryRemove(old.AttachId, out _))
            {
                old.Close("O prazo da conexão com o terminal passou.");
            }
        }

        return _attaches.GetOrAdd(attach.AttachId, attach);
    }

    public void Subscribe(RemoteTerminalAttach attach, string connectionId)
    {
        if (attach.SetSubscriber(connectionId))
        {
            _ = PumpAsync(attach);
        }
    }

    public void Publish(RemoteTerminalAttach attach, string connectionId, RemoteTerminalFrame frame)
    {
        attach.SetPublisher(connectionId);
        attach.Enqueue(frame);
    }

    /// <summary>Encerra; a conexão fica lembrada como encerrada até o prazo dela.</summary>
    public void Close(RemoteTerminalAttach attach, string reason) => attach.Close(reason);

    /// <summary>
    /// Uma conexão do hub caiu. Se era de quem seguia, o agente fica sabendo
    /// (a sessão continua; voltar é outra conexão); se era do agente, quem
    /// seguia fica sabendo.
    /// </summary>
    public async Task DisconnectedAsync(string connectionId)
    {
        foreach (var attach in _attaches.Values)
        {
            if (attach.IsClosed)
            {
                continue;
            }

            if (attach.SubscriberConnectionId == connectionId)
            {
                attach.Close("Quem seguia o terminal saiu.");
                await notifier.ToDeviceAsync(attach.UserId, attach.TargetDeviceId, RemoteHubMessages.TerminalDetached, attach.AttachId);
            }
            else if (attach.PublisherConnectionId == connectionId)
            {
                Close(attach, "O agente da máquina saiu do ar. O Claude pode seguir rodando lá.");
            }
        }
    }

    private async Task PumpAsync(RemoteTerminalAttach attach)
    {
        try
        {
            await foreach (var (message, args, bytes) in attach.Queue.ReadAllAsync())
            {
                attach.Sent(bytes);

                if (attach.SubscriberConnectionId is { } connectionId)
                {
                    await transport.ToConnectionAsync(connectionId, message, args, CancellationToken.None);
                }
            }
        }
        catch (Exception exception)
        {
            // O conteúdo nunca vai para o log: só o tipo do erro. Sem como entregar, a conexão acaba.
            LogPumpFailed(attach.AttachId, exception.GetType().Name);
            attach.Close("O servidor não conseguiu entregar a saída do terminal.");
        }
    }

    [LoggerMessage(EventId = 4803, Level = LogLevel.Warning, Message = "RemoteTerminalPumpFailed {AttachId} {Error}")]
    private partial void LogPumpFailed(Guid attachId, string error);
}

/// <summary>
/// As sessões do terminal que cada máquina relatou, só em memória: some com o
/// servidor e volta no próximo relato do agente.
/// </summary>
public sealed class RemoteTerminalDirectory
{
    private readonly ConcurrentDictionary<(Guid UserId, Guid DeviceId), (IReadOnlyList<RemoteTerminalSessionReport> Sessions, DateTimeOffset ReportedAt)> _reports = new();

    /// <summary>Troca o relato da máquina; verdadeiro se as sessões mudaram.</summary>
    public bool Report(Guid userId, Guid deviceId, IReadOnlyList<RemoteTerminalSessionReport> sessions, DateTimeOffset at)
    {
        var changed = true;

        _reports.AddOrUpdate(
            (userId, deviceId),
            _ => (sessions, at),
            (_, previous) =>
            {
                changed = !previous.Sessions.SequenceEqual(sessions);
                return (sessions, at);
            });

        return changed;
    }

    public IReadOnlyList<RemoteTerminalSessionView> List(Guid userId, DateTimeOffset now) =>
        [.. _reports
            .Where(pair => pair.Key.UserId == userId && now - pair.Value.ReportedAt <= RemoteLimits.OfflineAfter)
            .SelectMany(pair => pair.Value.Sessions.Select(session => new RemoteTerminalSessionView(pair.Key.DeviceId, session, pair.Value.ReportedAt)))
            .OrderByDescending(view => view.Session.StartedAt)];
}
