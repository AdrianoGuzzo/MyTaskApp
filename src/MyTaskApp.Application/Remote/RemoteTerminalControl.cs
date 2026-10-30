using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Sync;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;
using MyTaskApp.Domain.Tasks;
using MyTaskApp.Remote.Contracts;

namespace MyTaskApp.Application.Remote;

/// <summary>
/// O lado de quem controla no relay do terminal (ADR-099): uma conexão do
/// hub só desta vista. Entra pela operação <c>terminal.attach</c>; a entrada
/// vai assinada pela chave da conexão, e o servidor só repassa.
/// </summary>
public interface IRemoteTerminalWire : IAsyncDisposable
{
    event Action<RemoteTerminalFrame>? FrameReceived;

    /// <summary>O servidor jogou saída fora: a tela precisa do retrato.</summary>
    event Action<Guid>? GapReceived;

    /// <summary>A conexão acabou (attachId, motivo).</summary>
    event Action<Guid, string>? ClosedReceived;

    /// <summary>A ligação com o servidor caiu.</summary>
    event Action? Dropped;

    Task SubscribeAsync(Guid attachId, CancellationToken cancellationToken);

    Task SendAsync(Guid attachId, SignedEnvelope input, CancellationToken cancellationToken);
}

public interface IRemoteTerminalWires
{
    /// <summary>Uma conexão nova ao hub, já ligada.</summary>
    Task<IRemoteTerminalWire> OpenAsync(CancellationToken cancellationToken);
}

/// <summary>Sem servidor (o agente, os testes): não há terminal de longe.</summary>
public sealed class NoRemoteTerminalWires : IRemoteTerminalWires
{
    public static readonly NoRemoteTerminalWires Instance = new();

    public Task<IRemoteTerminalWire> OpenAsync(CancellationToken cancellationToken) =>
        throw new DomainException("Este app não está ligado a um servidor para o terminal remoto.");
}

/// <summary>Qual sessão seguir, e em que máquina.</summary>
public sealed record RemoteTerminalTarget(Guid DeviceId, Guid SessionId, Guid TaskId, Guid DevelopmentId);

/// <summary>Os pedidos do terminal remoto, como os outros: só ids e números (ADR-088).</summary>
public static class RemoteTerminalRequests
{
    public static SendRemoteOperation Start(Guid deviceId, Guid taskId, Guid developmentId) =>
        new(deviceId, RemoteOperationKinds.TerminalStart, Payload(new RemoteTerminalStartRequest(developmentId)), taskId, [taskId]);

    public static SendRemoteOperation Attach(RemoteTerminalTarget target, DevicePublicKey viewerKey, TerminalSize size) =>
        new(
            target.DeviceId,
            RemoteOperationKinds.TerminalAttach,
            Payload(new RemoteTerminalAttachRequest(target.SessionId, target.DevelopmentId, viewerKey, size.Columns, size.Rows)),
            target.TaskId);

    public static SendRemoteOperation Stop(RemoteTerminalTarget target, bool force) =>
        new(
            target.DeviceId,
            RemoteOperationKinds.TerminalStop,
            Payload(new RemoteTerminalStopRequest(target.SessionId, force ? RemoteTerminalStopMode.Force : RemoteTerminalStopMode.Graceful)),
            target.TaskId);

    private static JsonElement Payload<T>(T payload) => JsonSerializer.SerializeToElement(payload, RemoteJson.Options);
}

/// <summary>
/// Liga uma vista desta máquina a uma sessão do terminal do agente de outra
/// (ADR-099): abre a conexão do hub, pede o <c>terminal.attach</c> com uma
/// chave nova, só desta conexão, e passa a seguir.
/// </summary>
public sealed class RemoteTerminalConnector(IUseCaseRunner runner, IRemoteTerminalWires wires, TimeProvider time, ILogger<RemoteTerminalChannel> logger)
{
    /// <summary>De quanto em quanto o estado da operação é lido, até o agente aceitar ou recusar.</summary>
    public static readonly TimeSpan AttachPoll = TimeSpan.FromSeconds(1);

    public async Task<ITerminalChannel> ConnectAsync(RemoteTerminalTarget target, TerminalSize size, CancellationToken cancellationToken)
    {
        var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        IRemoteTerminalWire? wire = null;

        try
        {
            // O hub antes do pedido: a saída que o agente manda logo depois de aceitar já tem quem a espere.
            wire = await wires.OpenAsync(cancellationToken);
            var viewerKey = DevicePublicKey.Es256Of(key.ExportSubjectPublicKeyInfo());
            var operation = await runner.RunAsync<SendRemoteOperationHandler, RemoteOperationView>(
                (handler, token) => handler.HandleAsync(RemoteTerminalRequests.Attach(target, viewerKey, size.Clamp()), token),
                cancellationToken);

            var channel = new RemoteTerminalChannel(target, operation.OperationId, key, wire, runner, time, logger);
            await wire.SubscribeAsync(operation.OperationId, cancellationToken);
            channel.WatchAttach(AttachPoll);
            return channel;
        }
        catch
        {
            if (wire is not null)
            {
                await wire.DisposeAsync();
            }

            key.Dispose();
            throw;
        }
    }
}

/// <summary>
/// Uma vista seguindo uma sessão do terminal do agente de outra máquina pelo
/// servidor. Para quem desenha, é igual ao canal local: o retrato chega em
/// pedaços e sai daqui inteiro, a lacuna do servidor vira pedido de retrato, e
/// cada ação vira um lote assinado, numerado e com hora.
/// </summary>
public sealed partial class RemoteTerminalChannel : ITerminalChannel
{
    private readonly RemoteTerminalTarget _target;

    private readonly Guid _attachId;

    private readonly ECDsa _key;

    private readonly string _fingerprint;

    private readonly IRemoteTerminalWire _wire;

    private readonly IUseCaseRunner _runner;

    private readonly TimeProvider _time;

    private readonly ILogger<RemoteTerminalChannel> _logger;

    private readonly Channel<TerminalMessage> _messages = Channel.CreateBounded<TerminalMessage>(
        new BoundedChannelOptions(4096) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

    private readonly CancellationTokenSource _closing = new();

    private readonly Lock _gate = new();

    private MemoryStream? _pieces;

    private (long Offset, bool Reset) _first;

    private long _sequence;

    private bool _disposed;

    internal RemoteTerminalChannel(
        RemoteTerminalTarget target,
        Guid attachId,
        ECDsa key,
        IRemoteTerminalWire wire,
        IUseCaseRunner runner,
        TimeProvider time,
        ILogger<RemoteTerminalChannel> logger)
    {
        _target = target;
        _attachId = attachId;
        _key = key;
        _fingerprint = DevicePublicKey.Es256Of(key.ExportSubjectPublicKeyInfo()).Fingerprint;
        _wire = wire;
        _runner = runner;
        _time = time;
        _logger = logger;
        _wire.FrameReceived += OnFrame;
        _wire.GapReceived += OnGap;
        _wire.ClosedReceived += OnClosed;
        _wire.Dropped += OnDropped;
    }

    public Guid AttachId => _attachId;

    public ValueTask<TerminalMessage?> ReadAsync(CancellationToken cancellationToken = default) => ReadCoreAsync(cancellationToken);

    public async Task SendAsync(TerminalInput input, CancellationToken cancellationToken = default)
    {
        foreach (var action in ActionsOf(input))
        {
            var payload = new RemoteTerminalInput(_attachId, _target.DeviceId, _target.SessionId, Interlocked.Increment(ref _sequence), _time.GetUtcNow(), action);
            var signingInput = RemoteJws.SigningInput(payload, _fingerprint, RemoteJws.TerminalInputType);
            var signature = _key.SignData(Encoding.ASCII.GetBytes(signingInput), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);

            await _wire.SendAsync(_attachId, RemoteJws.Compose(signingInput, Base64Url.EncodeToString(signature)), cancellationToken);
        }
    }

    public async Task StopAsync(bool force, CancellationToken cancellationToken = default) =>
        await _runner.RunAsync<SendRemoteOperationHandler, RemoteOperationView>(
            (handler, token) => handler.HandleAsync(RemoteTerminalRequests.Stop(_target, force), token),
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        _wire.FrameReceived -= OnFrame;
        _wire.GapReceived -= OnGap;
        _wire.ClosedReceived -= OnClosed;
        _wire.Dropped -= OnDropped;
        await _closing.CancelAsync();
        _messages.Writer.TryComplete();

        // Fechar a conexão do hub é o "saí" para o agente: a sessão continua lá.
        await _wire.DisposeAsync();
        _key.Dispose();
        _closing.Dispose();
    }

    /// <summary>Lê o estado da operação até o agente aceitar; recusada, a vista fica sabendo por quê.</summary>
    internal void WatchAttach(TimeSpan every) => _ = WatchAttachAsync(every, _closing.Token);

    /// <summary>Uma ação da vista vira um ou mais lotes: o teclado grande vai em pedaços.</summary>
    private static IEnumerable<RemoteTerminalAction> ActionsOf(TerminalInput input)
    {
        switch (input)
        {
            case TerminalKeys keys:
                for (var start = 0; start < keys.Data.Length; start += RemoteLimits.MaxTerminalKeys)
                {
                    yield return new RemoteTerminalAction(RemoteTerminalActions.Keys, Keys: keys.Data.AsSpan(start, Math.Min(RemoteLimits.MaxTerminalKeys, keys.Data.Length - start)).ToArray());
                }

                break;
            case TerminalPrompt prompt:
                yield return new RemoteTerminalAction(RemoteTerminalActions.Prompt, Text: prompt.Text);
                break;
            case TerminalChoice choice:
                yield return new RemoteTerminalAction(RemoteTerminalActions.Choice, DialogId: choice.DialogId, Allow: choice.Allow);
                break;
            case TerminalInterrupt:
                yield return new RemoteTerminalAction(RemoteTerminalActions.Interrupt);
                break;
            case TerminalResize resize:
                yield return new RemoteTerminalAction(RemoteTerminalActions.Resize, Columns: resize.Size.Columns, Rows: resize.Size.Rows);
                break;
            case TerminalTakeControl:
                yield return new RemoteTerminalAction(RemoteTerminalActions.TakeControl);
                break;
            case TerminalReleaseControl:
                yield return new RemoteTerminalAction(RemoteTerminalActions.ReleaseControl);
                break;
            case TerminalResync:
                yield return new RemoteTerminalAction(RemoteTerminalActions.Resync);
                break;
            default:
                throw new DomainException("Esta ação não vai de longe.");
        }
    }

    private async ValueTask<TerminalMessage?> ReadCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _messages.Reader.WaitToReadAsync(cancellationToken) && _messages.Reader.TryRead(out var message) ? message : null;
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }

    private void OnFrame(RemoteTerminalFrame frame)
    {
        if (frame.AttachId != _attachId)
        {
            return;
        }

        // Quem chama é o cliente do hub: uma exceção aqui sumiria calada, e a tela pararia sem motivo.
        try
        {
            Apply(frame);
        }
        catch (Exception exception)
        {
            LogFrameFailed(_attachId, frame.Kind, exception.GetType().Name);
        }
    }

    private void Apply(RemoteTerminalFrame frame)
    {
        switch (frame.Kind)
        {
            case RemoteTerminalFrameKinds.Output when frame.Data is { } data:
                Assemble(frame, data);
                break;
            case RemoteTerminalFrameKinds.State when frame.State is { } state && state.SessionId == _target.SessionId:
                _messages.Writer.TryWrite(new TerminalStateMessage(RemoteTerminalMapping.FromRemote(state)));
                break;
            case RemoteTerminalFrameKinds.Notice when frame.Text is { } text:
                _messages.Writer.TryWrite(new TerminalNotice(Clean(text)));
                break;
            case RemoteTerminalFrameKinds.Refusal when frame.Text is { } text:
                _messages.Writer.TryWrite(new TerminalRefusal(Clean(text)));
                break;
        }
    }

    /// <summary>Os pedaços de uma mesma saída se juntam; um que não segue o anterior descarta o que estava pela metade.</summary>
    private void Assemble(RemoteTerminalFrame frame, byte[] data)
    {
        lock (_gate)
        {
            if (_pieces is not null)
            {
                var expected = _first.Reset ? _first.Offset : _first.Offset + _pieces.Length;

                if (frame.Reset || frame.Offset != expected)
                {
                    _pieces.Dispose();
                    _pieces = null;
                }
            }

            if (_pieces is null)
            {
                if (!frame.More)
                {
                    _messages.Writer.TryWrite(new TerminalOutput(frame.Offset, data, frame.Reset));
                    return;
                }

                _pieces = new MemoryStream();
                _first = (frame.Offset, frame.Reset);
            }

            _pieces.Write(data);

            if (!frame.More)
            {
                _messages.Writer.TryWrite(new TerminalOutput(_first.Offset, _pieces.ToArray(), _first.Reset));
                _pieces.Dispose();
                _pieces = null;
            }
        }
    }

    private void OnGap(Guid attachId)
    {
        if (attachId != _attachId)
        {
            return;
        }

        lock (_gate)
        {
            _pieces?.Dispose();
            _pieces = null;
        }

        LogGap(_attachId);
        _ = SendQuietlyAsync(new TerminalResync());
    }

    private void OnClosed(Guid attachId, string reason)
    {
        if (attachId == _attachId)
        {
            End(Clean(reason));
        }
    }

    private void OnDropped() => End("A ligação com o servidor caiu. O Claude segue rodando lá; conecte de novo.");

    private void End(string reason)
    {
        _messages.Writer.TryWrite(new TerminalNotice(reason));
        _messages.Writer.TryComplete();
    }

    private async Task SendQuietlyAsync(TerminalInput input)
    {
        try
        {
            await SendAsync(input, _closing.Token);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogSendFailed(_attachId, exception.GetType().Name);
        }
    }

    private async Task WatchAttachAsync(TimeSpan every, CancellationToken cancellationToken)
    {
        var waitingNoted = false;

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var operation = await _runner.RunAsync<IRemoteOperationsApi, RemoteOperationView>((api, token) => api.GetAsync(_attachId, token), cancellationToken);

                if (operation.Status == RemoteOperationStatus.Succeeded)
                {
                    return;
                }

                if (RemoteOperationLifecycle.IsFinal(operation.Status))
                {
                    _messages.Writer.TryWrite(new TerminalRefusal(Clean(operation.ResultSummary ?? "A outra máquina não aceitou a conexão com o terminal.")));
                    _messages.Writer.TryComplete();
                    return;
                }

                if (!waitingNoted && operation.Status is RemoteOperationStatus.Queued or RemoteOperationStatus.Dispatched)
                {
                    waitingNoted = true;
                    _messages.Writer.TryWrite(new TerminalNotice("Esperando o agente da outra máquina aceitar a conexão…"));
                }

                await Task.Delay(every, _time, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception) when (exception is SyncApiException or DomainException or HttpRequestException)
        {
            LogWatchFailed(_attachId, exception.GetType().Name);
        }
    }

    /// <summary>O texto que veio de lá aparece na tela daqui: sem controle nem formatação.</summary>
    private static string Clean(string text) =>
        new([.. text.Where(character => !char.IsControl(character) && char.GetUnicodeCategory(character) != System.Globalization.UnicodeCategory.Format)]);

    [LoggerMessage(EventId = 9871, Level = LogLevel.Information, Message = "RemoteTerminalGap {AttachId}")]
    private partial void LogGap(Guid attachId);

    [LoggerMessage(EventId = 9872, Level = LogLevel.Warning, Message = "RemoteTerminalSendFailed {AttachId} {Error}")]
    private partial void LogSendFailed(Guid attachId, string error);

    [LoggerMessage(EventId = 9873, Level = LogLevel.Warning, Message = "RemoteTerminalWatchFailed {AttachId} {Error}")]
    private partial void LogWatchFailed(Guid attachId, string error);

    [LoggerMessage(EventId = 9874, Level = LogLevel.Warning, Message = "RemoteTerminalFrameFailed {AttachId} {Kind} {Error}")]
    private partial void LogFrameFailed(Guid attachId, string kind, string error);
}

/// <summary>As sessões do terminal de uma máquina, para a tela de controle.</summary>
public sealed record ListRemoteTerminals(Guid DeviceId);

/// <summary>Uma sessão do terminal como a lista mostra: o título vem da tarefa sincronizada aqui.</summary>
public sealed record RemoteTerminalItem(RemoteTerminalSessionView View, string Title, string State)
{
    public RemoteTerminalSessionReport Session => View.Session;

    public bool IsRunning => Session.Process == RemoteTerminalProcess.Running;

    public string? BypassWarning => Session.PermissionsBypassed ? "Sem as aprovações do Claude Code" : null;

    public RemoteTerminalTarget? Target(Guid deviceId) =>
        Session.DevelopmentId is { } developmentId ? new RemoteTerminalTarget(deviceId, Session.SessionId, Session.TaskId, developmentId) : null;
}

/// <param name="Problem">Por que a lista não veio, em palavras.</param>
public sealed record RemoteTerminalList(IReadOnlyList<RemoteTerminalItem> Items, string? Problem);

public sealed class ListRemoteTerminalsHandler(IRemoteTerminalsApi api, ITaskItemRepository tasks)
{
    public const string ServerTooOld = "O servidor ainda não tem o terminal remoto. Atualize o servidor.";

    public async Task<RemoteTerminalList> HandleAsync(ListRemoteTerminals query, CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RemoteTerminalSessionView> sessions;

        try
        {
            sessions = [.. (await api.ListTerminalsAsync(cancellationToken)).Sessions.Where(view => view.DeviceId == query.DeviceId)];
        }
        catch (SyncApiException exception) when (exception is { Kind: SyncFailureKind.NotFound, Code: null })
        {
            return new RemoteTerminalList([], ServerTooOld);
        }

        var items = new List<RemoteTerminalItem>(sessions.Count);

        foreach (var view in sessions)
        {
            var task = await tasks.FindByIdAsync(view.Session.TaskId, cancellationToken);
            items.Add(new RemoteTerminalItem(view, task?.Title ?? "Tarefa", StateOf(view.Session)));
        }

        return new RemoteTerminalList(items, null);
    }

    public static string StateOf(RemoteTerminalSessionReport session) => session switch
    {
        { Process: not RemoteTerminalProcess.Running } => "○ Encerrado",
        { HasDialog: true } => "◐ Esperando resposta",
        { Activity: RemoteAgentActivity.Working } => "● Trabalhando",
        { Activity: RemoteAgentActivity.WaitingForUser } => "◐ Esperando você",
        { Activity: RemoteAgentActivity.WaitingReview } => "◐ Espera o próximo prompt",
        { Monitored: false } => "● Rodando, sem acompanhamento",
        _ => "● Rodando",
    } + (session.RemotelyControlled ? " · controlado de longe" : string.Empty);
}
