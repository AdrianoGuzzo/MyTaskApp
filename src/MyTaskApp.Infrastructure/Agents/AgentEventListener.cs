using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.Agents;
using MyTaskApp.Application.Configuration;
using MyTaskApp.Infrastructure.Agents.ClaudeCode;

namespace MyTaskApp.Infrastructure.Agents;

/// <summary>Os cabeçalhos que o hook manda com o id da sessão e da tarefa (ADR-036).</summary>
internal static class AgentEventHeaders
{
    public const string Session = "X-MyTaskApp-Session";

    public const string Task = "X-MyTaskApp-Task";
}

/// <summary>
/// A porta local por onde os agentes avisam o que estão fazendo (ADR-036):
/// <c>POST http://127.0.0.1:{porta}/api/claude/events</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>Só loopback.</b> O socket é aberto em <see cref="IPAddress.Loopback"/>:
/// nada de fora do computador alcança a porta. Dentro dele, qualquer processo
/// alcança — por isso o aviso só vale com o segredo da sessão (conferido no
/// caso de uso), e pedido com <c>Origin</c> (navegador) ou com <c>Host</c> que
/// não seja o próprio endereço é recusado antes de ler o corpo.
/// </para>
/// <para>
/// <b>HTTP mínimo sobre <see cref="TcpListener"/>.</b> O <c>HttpListener</c>
/// passa pelo http.sys, que pede reserva de URL para portas fora do padrão, e
/// o Kestrel traria o ASP.NET inteiro para um app de bandeja. Aqui só se
/// entende o que o hook manda: um POST com JSON, com <c>Content-Length</c> ou
/// em pedaços, uma requisição por conexão.
/// </para>
/// <para>
/// <b>Responde antes de processar.</b> O hook HTTP é síncrono: o Claude espera
/// a resposta para continuar. O corpo é validado e enfileirado, a resposta
/// (<c>{}</c>, "sem decisão") sai na hora, e uma fila de um consumidor só
/// aplica os avisos <b>na ordem em que chegaram</b> — "pergunta" e "voltou a
/// trabalhar" não podem trocar de lugar.
/// </para>
/// <para>
/// <b>Porta fixa, com plano B.</b> Um Claude aberto guarda a URL dos hooks
/// até sair; reabrir o app na mesma porta faz os Claude que ficaram abertos
/// continuarem avisando. Se a porta estiver ocupada, vale qualquer livre — as
/// sessões novas usam a nova.
/// </para>
/// </remarks>
internal sealed class AgentEventListener : IAgentEventEndpoint, IAsyncDisposable, IDisposable
{
    public const string EventsPath = "/api/claude/events";

    public const int MaxBodyBytes = 1024 * 1024;

    private const int MaxHeaderBytes = 16 * 1024;

    private const string EmptyDecision = "{}";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    private readonly Func<RecordAgentEvent, CancellationToken, Task> _deliver;
    private readonly int _preferredPort;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AgentEventListener> _logger;

    private readonly CancellationTokenSource _stopping = new();
    private readonly Channel<RecordAgentEvent> _queue = Channel.CreateBounded<RecordAgentEvent>(
        new BoundedChannelOptions(1000) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite });

    private readonly Lock _lock = new();
    private TcpListener? _listener;
    private Task _accepting = Task.CompletedTask;
    private Task _consuming = Task.CompletedTask;
    private bool _disposed;

    public AgentEventListener(
        IUseCaseRunner runner,
        IOptions<ApplicationOptions> options,
        TimeProvider timeProvider,
        ILogger<AgentEventListener> logger)
        : this(
            (command, token) => runner.RunAsync<RecordAgentEventHandler, AgentEventOutcome>(
                (handler, cancellation) => handler.HandleAsync(command, cancellation),
                token),
            options.Value.AgentEventsPort,
            timeProvider,
            logger)
    {
    }

    /// <summary>Para os testes: entrega sem contêiner, porta escolhida por eles.</summary>
    internal AgentEventListener(
        Func<RecordAgentEvent, CancellationToken, Task> deliver,
        int preferredPort,
        TimeProvider timeProvider,
        ILogger<AgentEventListener> logger)
    {
        _deliver = deliver;
        _preferredPort = preferredPort;
        _timeProvider = timeProvider;
        _logger = logger;
    }

    public Uri? Address { get; private set; }

    /// <summary>Quantos avisos a fila recusou por estar cheia — para os testes.</summary>
    public int Dropped { get; private set; }

    public void Start()
    {
        lock (_lock)
        {
            if (_listener is not null || _disposed)
            {
                return;
            }

            _listener = Bind();

            if (_listener is null)
            {
                return;
            }

            var port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            Address = new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}{EventsPath}");

            _accepting = AcceptLoopAsync(_listener, _stopping.Token);
            _consuming = ConsumeAsync(_stopping.Token);
        }

        _logger.LogInformation("AgentEventListenerStarted {Address}", Address);
    }

    private TcpListener? Bind()
    {
        foreach (var port in _preferredPort is > 0 and <= IPEndPoint.MaxPort ? [_preferredPort, 0] : new[] { 0 })
        {
            var listener = new TcpListener(IPAddress.Loopback, port);

            try
            {
                // Sem reaproveitar a porta de outro processo: dois donos
                // dividiriam os avisos.
                listener.ExclusiveAddressUse = true;
                listener.Start();
                return listener;
            }
            catch (SocketException exception)
            {
                listener.Dispose();
                _logger.LogWarning(exception, "AgentEventPortUnavailable {Port}", port);
            }
        }

        _logger.LogError("AgentEventListenerUnavailable");
        return null;
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient client;

            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                // Desligando. O socket só deixa de ouvir no descarte, e o accept
                // pendente sai com InvalidOperationException ("Not listening").
                return;
            }
            catch (SocketException exception)
            {
                _logger.LogWarning(exception, "AgentEventAcceptFailed");
                continue;
            }

            _ = ServeAsync(client, cancellationToken);
        }
    }

    private async Task ServeAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using var _ = client;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            var stream = client.GetStream();
            var (status, body) = await HandleAsync(stream, timeout.Token);
            await RespondAsync(stream, status, body, timeout.Token);
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
            // Conexão que não terminou a tempo, ou app fechando: nada a fazer.
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "AgentEventRequestFailed");
        }
    }

    private async Task<(HttpStatusCode Status, string Body)> HandleAsync(Stream stream, CancellationToken cancellationToken)
    {
        var request = await HookRequest.ReadAsync(stream, MaxHeaderBytes, MaxBodyBytes, cancellationToken);

        if (request.Error is { } error)
        {
            return (error, EmptyDecision);
        }

        if (!string.Equals(request.Path, EventsPath, StringComparison.Ordinal))
        {
            return (HttpStatusCode.NotFound, EmptyDecision);
        }

        if (!string.Equals(request.Method, "POST", StringComparison.Ordinal))
        {
            return (HttpStatusCode.MethodNotAllowed, EmptyDecision);
        }

        // Navegador (Origin) e DNS rebinding (Host de outro nome): nunca é o hook.
        if (request.Header("Origin") is not null || !IsOwnHost(request.Header("Host")))
        {
            _logger.LogWarning("AgentEventForbidden {Host} {Origin}", request.Header("Host"), request.Header("Origin"));
            return (HttpStatusCode.Forbidden, EmptyDecision);
        }

        if (BearerToken(request.Header("Authorization")) is not { } token
            || !Guid.TryParse(request.Header(AgentEventHeaders.Session), out var sessionId))
        {
            return (HttpStatusCode.Unauthorized, EmptyDecision);
        }

        Guid? taskId = Guid.TryParse(request.Header(AgentEventHeaders.Task), out var parsedTask) ? parsedTask : null;

        AgentEvent? agentEvent;

        try
        {
            using var document = JsonDocument.Parse(request.Body);
            agentEvent = ClaudeCodeHookEvents.Translate(document.RootElement, sessionId, taskId, _timeProvider.GetUtcNow());
        }
        catch (JsonException)
        {
            return (HttpStatusCode.BadRequest, EmptyDecision);
        }

        if (agentEvent is null)
        {
            return (HttpStatusCode.BadRequest, EmptyDecision);
        }

        if (!_queue.Writer.TryWrite(new RecordAgentEvent(agentEvent, token)))
        {
            Dropped++;
            _logger.LogWarning("AgentEventDropped {AgentSessionId} {SourceEvent}", sessionId, agentEvent.SourceEvent);
        }

        return (HttpStatusCode.OK, EmptyDecision);
    }

    private bool IsOwnHost(string? host)
    {
        if (Address is not { } address || string.IsNullOrEmpty(host))
        {
            return false;
        }

        var port = address.Port.ToString(CultureInfo.InvariantCulture);

        return string.Equals(host, $"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
            || string.Equals(host, $"localhost:{port}", StringComparison.OrdinalIgnoreCase);
    }

    private static string? BearerToken(string? authorization)
    {
        const string Scheme = "Bearer ";

        if (authorization is null || !authorization.StartsWith(Scheme, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var token = authorization[Scheme.Length..].Trim();

        // Variável não resolvida vira vazio: "Bearer " sozinho não é segredo.
        return token.Length == 0 ? null : token;
    }

    private static async Task RespondAsync(
        Stream stream,
        HttpStatusCode status,
        string body,
        CancellationToken cancellationToken)
    {
        var content = Encoding.UTF8.GetBytes(body);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)status} {status}\r\n"
            + "Content-Type: application/json\r\n"
            + $"Content-Length: {content.Length.ToString(CultureInfo.InvariantCulture)}\r\n"
            + "Connection: close\r\n\r\n");

        await stream.WriteAsync(head, cancellationToken);
        await stream.WriteAsync(content, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (var command in _queue.Reader.ReadAllAsync(cancellationToken))
            {
                try
                {
                    await _deliver(command, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogError(
                        exception,
                        "AgentEventDeliveryFailed {AgentSessionId} {SourceEvent}",
                        command.Event.AgentSessionId,
                        command.Event.SourceEvent);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Desligando.
        }
    }

    /// <summary>
    /// Para de ouvir. O Claude que continuar aberto recebe "conexão recusada"
    /// nos próximos avisos — o hook falha sem travar nada — e volta a ser ouvido
    /// quando o app reabrir na mesma porta.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (!BeginDispose())
        {
            return;
        }

        await _stopping.CancelAsync();
        await WaitAsync();
        _stopping.Dispose();
    }

    public void Dispose()
    {
        if (!BeginDispose())
        {
            return;
        }

        _stopping.Cancel();
        WaitAsync().GetAwaiter().GetResult();
        _stopping.Dispose();
    }

    private bool BeginDispose()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return false;
            }

            _disposed = true;
            _listener?.Stop();
            _listener?.Dispose();
            _queue.Writer.TryComplete();
            return true;
        }
    }

    private async Task WaitAsync()
    {
        try
        {
            await Task.WhenAll(_accepting, _consuming).WaitAsync(TimeSpan.FromSeconds(2));
        }
        catch (Exception exception) when (exception is TimeoutException or OperationCanceledException)
        {
            // Um aviso em voo não segura o fechamento do app.
        }
    }
}

/// <summary>Uma requisição HTTP/1.1 lida do socket — só o que o hook usa.</summary>
internal sealed class HookRequest
{
    private readonly Dictionary<string, string> _headers = new(StringComparer.OrdinalIgnoreCase);

    public string Method { get; private init; } = string.Empty;

    public string Path { get; private init; } = string.Empty;

    public ReadOnlyMemory<byte> Body { get; private set; }

    /// <summary>O motivo de a requisição não servir; <c>null</c> = bem formada.</summary>
    public HttpStatusCode? Error { get; private init; }

    public string? Header(string name) => _headers.GetValueOrDefault(name);

    private static HookRequest Failed(HttpStatusCode status) => new() { Error = status };

    public static async Task<HookRequest> ReadAsync(
        Stream stream,
        int maxHeaderBytes,
        int maxBodyBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new MemoryStream();
        var chunk = new byte[4096];
        var headerEnd = -1;

        while (headerEnd < 0)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);

            if (read == 0)
            {
                return Failed(HttpStatusCode.BadRequest);
            }

            buffer.Write(chunk, 0, read);
            headerEnd = IndexOfHeaderEnd(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));

            if (headerEnd < 0 && buffer.Length > maxHeaderBytes)
            {
                return Failed(HttpStatusCode.RequestHeaderFieldsTooLarge);
            }
        }

        var all = buffer.GetBuffer().AsMemory(0, (int)buffer.Length);
        var lines = Encoding.ASCII.GetString(all.Span[..headerEnd]).Split("\r\n");
        var requestLine = lines[0].Split(' ');

        if (requestLine.Length != 3 || !requestLine[2].StartsWith("HTTP/1.", StringComparison.Ordinal))
        {
            return Failed(HttpStatusCode.BadRequest);
        }

        var target = requestLine[1];
        var query = target.IndexOf('?', StringComparison.Ordinal);

        var request = new HookRequest
        {
            Method = requestLine[0],
            Path = query < 0 ? target : target[..query],
        };

        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);

            if (colon > 0)
            {
                request._headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        var rest = all[(headerEnd + 4)..];

        if (string.Equals(request.Header("Transfer-Encoding"), "chunked", StringComparison.OrdinalIgnoreCase))
        {
            var body = await ReadChunkedAsync(stream, rest, maxBodyBytes, cancellationToken);

            if (body is null)
            {
                return Failed(HttpStatusCode.RequestEntityTooLarge);
            }

            request.Body = body;
            return request;
        }

        if (request.Header("Content-Length") is not { } lengthText)
        {
            // GET e afins não têm corpo; POST sem tamanho é recusado adiante.
            return request.Method == "POST" ? Failed(HttpStatusCode.LengthRequired) : request;
        }

        if (!int.TryParse(lengthText, NumberStyles.None, CultureInfo.InvariantCulture, out var length))
        {
            return Failed(HttpStatusCode.BadRequest);
        }

        if (length > maxBodyBytes)
        {
            return Failed(HttpStatusCode.RequestEntityTooLarge);
        }

        var content = new byte[length];
        var copied = Math.Min(rest.Length, length);
        rest[..copied].CopyTo(content);

        await stream.ReadExactlyAsync(content.AsMemory(copied), cancellationToken);

        request.Body = content;
        return request;
    }

    private static int IndexOfHeaderEnd(ReadOnlySpan<byte> data) => data.IndexOf("\r\n\r\n"u8);

    /// <summary>
    /// <c>Transfer-Encoding: chunked</c>: tamanho em hexadecimal, CRLF, dados,
    /// CRLF — até o pedaço de tamanho zero. <c>null</c> se passar do limite.
    /// </summary>
    private static async Task<byte[]?> ReadChunkedAsync(
        Stream stream,
        ReadOnlyMemory<byte> alreadyRead,
        int maxBodyBytes,
        CancellationToken cancellationToken)
    {
        var reader = new BufferedReader(stream, alreadyRead);
        var body = new MemoryStream();

        while (true)
        {
            var sizeLine = await reader.ReadLineAsync(cancellationToken);
            var extension = sizeLine.IndexOf(';', StringComparison.Ordinal);
            var sizeText = (extension < 0 ? sizeLine : sizeLine[..extension]).Trim();

            if (!int.TryParse(sizeText, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var size) || size < 0)
            {
                throw new IOException("Pedaço inválido.");
            }

            if (size == 0)
            {
                // Trailers, se houver, até a linha vazia.
                while ((await reader.ReadLineAsync(cancellationToken)).Length > 0)
                {
                }

                return body.ToArray();
            }

            if (body.Length + size > maxBodyBytes)
            {
                return null;
            }

            body.Write(await reader.ReadExactlyAsync(size, cancellationToken));
            await reader.ReadLineAsync(cancellationToken);
        }
    }

    /// <summary>Lê linhas e blocos, começando pelo que já veio junto com o cabeçalho.</summary>
    private sealed class BufferedReader(Stream stream, ReadOnlyMemory<byte> pending)
    {
        private readonly Queue<byte> _pending = new(pending.ToArray());

        public async Task<string> ReadLineAsync(CancellationToken cancellationToken)
        {
            var line = new List<byte>();

            while (true)
            {
                var next = await ReadByteAsync(cancellationToken);

                if (next == '\n')
                {
                    return Encoding.ASCII.GetString([.. line]).TrimEnd('\r');
                }

                line.Add(next);

                if (line.Count > 1024)
                {
                    throw new IOException("Linha longa demais.");
                }
            }
        }

        public async Task<byte[]> ReadExactlyAsync(int count, CancellationToken cancellationToken)
        {
            var data = new byte[count];
            var offset = 0;

            while (offset < count && _pending.Count > 0)
            {
                data[offset++] = _pending.Dequeue();
            }

            if (offset < count)
            {
                await stream.ReadExactlyAsync(data.AsMemory(offset), cancellationToken);
            }

            return data;
        }

        private async Task<byte> ReadByteAsync(CancellationToken cancellationToken)
        {
            if (_pending.Count > 0)
            {
                return _pending.Dequeue();
            }

            var single = new byte[1];
            await stream.ReadExactlyAsync(single, cancellationToken);
            return single[0];
        }
    }
}
