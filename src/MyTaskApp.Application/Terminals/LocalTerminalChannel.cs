using System.Text;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Domain;

namespace MyTaskApp.Application.Terminals;

/// <summary>Uma recusa do agente a uma ação, com o motivo que a pessoa lê.</summary>
public sealed record TerminalRefusal(string Text) : TerminalMessage;

/// <summary>
/// O lado do agente no canal local do terminal (ADR-098): cada conexão abre
/// uma sessão ou segue uma. Quem conecta é um processo do mesmo usuário — o
/// pipe só aceita esse —, que já poderia abrir o Claude sozinho; por isso o
/// canal pode executar, ao contrário do canal de avisos (T-11).
/// </summary>
/// <remarks>
/// O início usa o mesmo caso de uso do card (<see cref="StartAgentSessionHandler"/>):
/// pelo canal só vão ids e escolhas, e o comando é montado aqui, a partir do
/// banco.
/// </remarks>
public sealed partial class LocalTerminalServer(
    ITerminalSessions terminals,
    IUseCaseRunner runner,
    ILogger<LocalTerminalServer> logger)
{
    /// <summary>O primeiro quadro precisa chegar logo: conexão muda não segura nada.</summary>
    public static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    public async Task ServeAsync(Stream stream, CancellationToken cancellationToken)
    {
        await using var frames = new TerminalFrameStream(stream);

        try
        {
            var hello = await ReadHelloAsync(frames, cancellationToken).ConfigureAwait(false);

            if (hello is null)
            {
                return;
            }

            switch (hello.Operation)
            {
                case TerminalHello.StartOperation when hello.Start is { } start:
                    await frames.WriteJsonAsync(TerminalFrameType.Result, await StartAsync(start, cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
                    break;
                case TerminalHello.Attach when hello.SessionId is { } sessionId:
                    await AttachAsync(frames, hello, sessionId, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    await frames.WriteTextAsync(TerminalFrameType.Error, "Pedido de terminal desconhecido.", cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException or EndOfStreamException)
        {
            // O outro lado fechou, ou mandou o que não é quadro.
        }
    }

    private static async Task<TerminalHello?> ReadHelloAsync(TerminalFrameStream frames, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(HelloTimeout);

        if (await frames.ReadAsync(timeout.Token).ConfigureAwait(false) is not { Type: TerminalFrameType.Hello } frame)
        {
            return null;
        }

        var hello = TerminalFrameStream.ReadJson<TerminalHello>(frame.Payload);

        if (hello.Version != TerminalHello.CurrentVersion)
        {
            await frames.WriteTextAsync(TerminalFrameType.Error, "O app e o agente estão em versões diferentes. Atualize os dois.", cancellationToken).ConfigureAwait(false);
            return null;
        }

        return hello;
    }

    private async Task<TerminalStartResult> StartAsync(TerminalStartRequest request, CancellationToken cancellationToken)
    {
        if (request.Hosting == AgentHosting.Direct)
        {
            return new TerminalStartResult(false, null, "O terminal direto abre pelo app, não pelo agente.", null);
        }

        try
        {
            var view = await runner.RunAsync<StartAgentSessionHandler, AgentSessionView>(
                (handler, token) => handler.HandleAsync(
                    new StartAgentSession(
                        request.TaskId,
                        request.DevelopmentId,
                        Arguments: request.Arguments,
                        Prompt: request.Prompt,
                        RunDirectly: request.RunDirectly,
                        Monitor: request.Monitor,
                        Model: request.Model,
                        Effort: request.Effort,
                        Hosting: request.Hosting,
                        ResumeConversation: request.ResumeConversation),
                    token),
                cancellationToken).ConfigureAwait(false);

            return view.Status == Domain.Agents.AgentSessionStatus.Failed
                ? new TerminalStartResult(false, view.SessionId, view.FailureReason, view.MonitoringNote)
                : new TerminalStartResult(true, view.SessionId, null, view.MonitoringNote);
        }
        catch (DomainException exception)
        {
            return new TerminalStartResult(false, null, exception.Message, null);
        }
    }

    private async Task AttachAsync(TerminalFrameStream frames, TerminalHello hello, Guid sessionId, CancellationToken cancellationToken)
    {
        if (hello.Kind is not (TerminalClientKind.NativeWindow or TerminalClientKind.Embedded))
        {
            await frames.WriteTextAsync(TerminalFrameType.Error, "Quem controla de longe conecta pelo servidor.", cancellationToken).ConfigureAwait(false);
            return;
        }

        if (terminals.Find(sessionId) is not { } host)
        {
            await frames.WriteTextAsync(TerminalFrameType.Error, "Esta sessão não está no terminal do agente. Ela pode ter terminado.", cancellationToken).ConfigureAwait(false);
            return;
        }

        using var client = host.Attach(hello.Kind, hello.Label ?? (hello.Kind == TerminalClientKind.NativeWindow ? "Janela do terminal" : "MyTaskApp"));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        LogAttached(sessionId, hello.Kind);

        if (hello.Columns > 0 && hello.Rows > 0)
        {
            await SendQuietlyAsync(frames, client, new TerminalResize(new TerminalSize(hello.Columns, hello.Rows).Clamp()), stop.Token).ConfigureAwait(false);
        }

        var outbound = PumpOutAsync(frames, client, stop.Token);
        var inbound = PumpInAsync(frames, client, host, stop.Token);

        await Task.WhenAny(outbound, inbound).ConfigureAwait(false);
        await stop.CancelAsync().ConfigureAwait(false);

        try
        {
            await Task.WhenAll(outbound, inbound).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException or EndOfStreamException)
        {
        }
    }

    private static async Task PumpOutAsync(TerminalFrameStream frames, TerminalClient client, CancellationToken cancellationToken)
    {
        while (await client.ReadAsync(cancellationToken).ConfigureAwait(false) is { } message)
        {
            switch (message)
            {
                case TerminalOutput output:
                    await frames.WriteOutputAsync(output, cancellationToken).ConfigureAwait(false);
                    break;
                case TerminalStateMessage state:
                    await frames.WriteJsonAsync(TerminalFrameType.State, state.Status, cancellationToken).ConfigureAwait(false);
                    break;
                case TerminalNotice notice:
                    await frames.WriteTextAsync(TerminalFrameType.Notice, notice.Text, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    private static async Task PumpInAsync(TerminalFrameStream frames, TerminalClient client, TerminalSessionHost host, CancellationToken cancellationToken)
    {
        while (await frames.ReadAsync(cancellationToken).ConfigureAwait(false) is { } frame)
        {
            switch (frame.Type)
            {
                case TerminalFrameType.Keys:
                    await SendQuietlyAsync(frames, client, new TerminalKeys(frame.Payload), cancellationToken).ConfigureAwait(false);
                    break;
                case TerminalFrameType.Action:
                    var action = TerminalFrameStream.ReadJson<TerminalWireAction>(frame.Payload);

                    if (action.Type == TerminalWireAction.Stop)
                    {
                        host.Stop(action.Force);
                        break;
                    }

                    try
                    {
                        await SendQuietlyAsync(frames, client, action.ToInput()!, cancellationToken).ConfigureAwait(false);
                    }
                    catch (DomainException exception)
                    {
                        await frames.WriteTextAsync(TerminalFrameType.Error, exception.Message, cancellationToken).ConfigureAwait(false);
                    }

                    break;
                default:
                    throw new InvalidDataException("Quadro de terminal fora de lugar.");
            }
        }
    }

    /// <summary>A ação recusada volta como <see cref="TerminalFrameType.Error"/>; a conexão segue.</summary>
    private static async Task SendQuietlyAsync(TerminalFrameStream frames, TerminalClient client, TerminalInput input, CancellationToken cancellationToken)
    {
        try
        {
            await client.SendAsync(input, cancellationToken).ConfigureAwait(false);
        }
        catch (DomainException exception)
        {
            await frames.WriteTextAsync(TerminalFrameType.Error, exception.Message, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(EventId = 9821, Level = LogLevel.Information, Message = "LocalTerminalAttached {SessionId} {Kind}")]
    private partial void LogAttached(Guid sessionId, TerminalClientKind kind);
}

/// <summary>
/// O lado de quem olha no canal local do terminal (ADR-098): a janela nativa
/// e o app desta máquina. O retrato chega em pedaços e sai daqui inteiro.
/// </summary>
public sealed class LocalTerminalChannel : ITerminalChannel
{
    private readonly TerminalFrameStream _frames;

    private MemoryStream? _snapshot;

    private long _snapshotOffset;

    private LocalTerminalChannel(Stream stream)
    {
        _frames = new TerminalFrameStream(stream);
    }

    /// <summary>Segue a sessão <paramref name="sessionId"/> pelo fluxo já conectado.</summary>
    public static async Task<LocalTerminalChannel> AttachAsync(
        Stream stream,
        Guid sessionId,
        TerminalClientKind kind,
        string label,
        TerminalSize? size,
        CancellationToken cancellationToken = default)
    {
        var channel = new LocalTerminalChannel(stream);
        await channel._frames.WriteJsonAsync(
            TerminalFrameType.Hello,
            new TerminalHello(TerminalHello.CurrentVersion, TerminalHello.Attach, sessionId, kind, label, size?.Columns ?? 0, size?.Rows ?? 0),
            cancellationToken).ConfigureAwait(false);
        return channel;
    }

    /// <summary>Pede ao agente que abra a sessão e devolve o que ele respondeu.</summary>
    public static async Task<TerminalStartResult> StartAsync(Stream stream, TerminalStartRequest request, CancellationToken cancellationToken = default)
    {
        await using var channel = new LocalTerminalChannel(stream);
        await channel._frames.WriteJsonAsync(
            TerminalFrameType.Hello,
            new TerminalHello(TerminalHello.CurrentVersion, TerminalHello.StartOperation, Start: request),
            cancellationToken).ConfigureAwait(false);

        return await channel._frames.ReadAsync(cancellationToken).ConfigureAwait(false) switch
        {
            { Type: TerminalFrameType.Result } frame => TerminalFrameStream.ReadJson<TerminalStartResult>(frame.Payload),
            { Type: TerminalFrameType.Error } frame => new TerminalStartResult(false, null, Encoding.UTF8.GetString(frame.Payload), null),
            _ => new TerminalStartResult(false, null, "O agente fechou o canal sem responder.", null),
        };
    }

    /// <summary>A próxima mensagem; <c>null</c> quando o agente fechou.</summary>
    public async ValueTask<TerminalMessage?> ReadAsync(CancellationToken cancellationToken = default)
    {
        while (await _frames.ReadAsync(cancellationToken).ConfigureAwait(false) is { } frame)
        {
            switch (frame.Type)
            {
                case TerminalFrameType.Output:
                    if (Output(frame.Payload) is { } output)
                    {
                        return output;
                    }

                    continue;
                case TerminalFrameType.State:
                    return new TerminalStateMessage(TerminalFrameStream.ReadJson<TerminalSessionStatus>(frame.Payload));
                case TerminalFrameType.Notice:
                    return new TerminalNotice(Encoding.UTF8.GetString(frame.Payload));
                case TerminalFrameType.Error:
                    return new TerminalRefusal(Encoding.UTF8.GetString(frame.Payload));
                default:
                    throw new InvalidDataException("Quadro de terminal fora de lugar.");
            }
        }

        return null;
    }

    public Task SendKeysAsync(ReadOnlyMemory<byte> keys, CancellationToken cancellationToken = default) =>
        _frames.WriteAsync(TerminalFrameType.Keys, keys, cancellationToken);

    public Task SendAsync(TerminalInput input, CancellationToken cancellationToken = default) =>
        input is TerminalKeys keys
            ? SendKeysAsync(keys.Data, cancellationToken)
            : _frames.WriteJsonAsync(TerminalFrameType.Action, TerminalWireAction.From(input), cancellationToken);

    /// <summary>Encerrar a sessão: <paramref name="force"/> mata a árvore dela; sem, é como fechar a janela.</summary>
    public Task StopAsync(bool force, CancellationToken cancellationToken = default) =>
        _frames.WriteJsonAsync(TerminalFrameType.Action, new TerminalWireAction(TerminalWireAction.Stop, Force: force), cancellationToken);

    public ValueTask DisposeAsync() => _frames.DisposeAsync();

    private TerminalOutput? Output(byte[] payload)
    {
        var (offset, flags, data) = TerminalFrameStream.ReadOutput(payload);

        if (!flags.HasFlag(TerminalOutputFlags.Reset) && !flags.HasFlag(TerminalOutputFlags.Continuation))
        {
            return new TerminalOutput(offset, data.ToArray(), Reset: false);
        }

        if (flags.HasFlag(TerminalOutputFlags.Reset))
        {
            _snapshot = new MemoryStream();
            _snapshotOffset = offset;
        }

        if (_snapshot is null)
        {
            throw new InvalidDataException("Pedaço de retrato sem o começo.");
        }

        if (_snapshot.Length + data.Length > 64 * 1024 * 1024)
        {
            throw new InvalidDataException("Retrato de terminal grande demais.");
        }

        _snapshot.Write(data.Span);

        if (!flags.HasFlag(TerminalOutputFlags.Final))
        {
            return null;
        }

        var snapshot = new TerminalOutput(_snapshotOffset, _snapshot.ToArray(), Reset: true);
        _snapshot = null;
        return snapshot;
    }
}
