using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using MyTaskApp.Domain;

namespace MyTaskApp.Infrastructure.OAuth;

/// <summary>
/// O provedor voltou sem código. A frase é a do provedor, para a tela; o
/// <see cref="Error"/> é o código OAuth, para quem precisa decidir por ele
/// (o <c>device_revoked</c> da sincronização).
/// </summary>
internal sealed class AuthorizationRefusedException(string? error, string message) : DomainException(message)
{
    public string? Error { get; } = error;
}

/// <summary>
/// As frases que o listener mostra no navegador e devolve ao app. Cada
/// provedor (Jira, sincronização) fala o próprio nome.
/// </summary>
internal sealed record LoopbackCallbackMessages(
    string PortInUse,
    string Connected,
    string NotCompleted,
    string Denied,
    string Failed,
    string ReceivedLogEvent);

/// <summary>
/// Espera o navegador voltar com o código de autorização em
/// <c>http://localhost:{porta}/callback</c> (ADR-045, ADR-060).
/// </summary>
/// <remarks>
/// <para>
/// Mesmo desenho do <c>AgentEventListener</c>: <see cref="TcpListener"/> só em
/// loopback, sem <c>HttpListener</c> (que pede reserva de URL ao http.sys) nem
/// Kestrel. Aqui é ainda menor — um <c>GET</c> com query e uma página de volta.
/// </para>
/// <para>
/// <b>IPv4 e IPv6.</b> O <c>localhost</c> do redirect pode resolver para
/// <c>::1</c> antes de <c>127.0.0.1</c>; o listener escuta os dois para o
/// navegador não esbarrar numa porta fechada.
/// </para>
/// <para>
/// <b>O <c>state</c> é conferido.</b> Uma volta com outro <c>state</c> não é a
/// autorização que este app pediu — pode ser outra aba, ou alguém tentando
/// enfiar um código — e é ignorada sem encerrar a espera.
/// </para>
/// </remarks>
internal sealed class LoopbackCallbackListener : IAsyncDisposable
{
    public const string CallbackPath = "/callback";

    private const int MaxRequestBytes = 16 * 1024;

    private readonly List<TcpListener> _listeners = [];

    private readonly Channel<TcpClient> _connections = Channel.CreateUnbounded<TcpClient>();

    private readonly CancellationTokenSource _stopping = new();

    private readonly LoopbackCallbackMessages _messages;

    private readonly ILogger _logger;

    private LoopbackCallbackListener(LoopbackCallbackMessages messages, ILogger logger)
    {
        _messages = messages;
        _logger = logger;
    }

    public int Port { get; private set; }

    /// <summary>Abre a porta. Ocupada, recusa com o que fazer — o provedor só volta para ela.</summary>
    public static LoopbackCallbackListener Start(int port, LoopbackCallbackMessages messages, ILogger logger)
    {
        var listener = new LoopbackCallbackListener(messages, logger);

        try
        {
            listener.Listen(IPAddress.Loopback, port);
        }
        catch (SocketException)
        {
            throw new DomainException(messages.PortInUse.Replace("{port}", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal));
        }

        try
        {
            if (Socket.OSSupportsIPv6)
            {
                listener.Listen(IPAddress.IPv6Loopback, listener.Port);
            }
        }
        catch (SocketException)
        {
            // Sem IPv6 de loopback, o IPv4 basta: o navegador tenta os dois.
        }

        return listener;
    }

    /// <summary>
    /// A primeira volta com o <paramref name="expectedState"/>: o código, ou a
    /// recusa do usuário como <see cref="DomainException"/>.
    /// </summary>
    public async Task<string> WaitForCodeAsync(string expectedState, CancellationToken cancellationToken)
    {
        while (true)
        {
            var connection = await _connections.Reader.ReadAsync(cancellationToken);

            using (connection)
            {
                var callback = await ReadCallbackAsync(connection, cancellationToken);

                if (callback is null || !string.Equals(callback.GetValueOrDefault("state"), expectedState, StringComparison.Ordinal))
                {
                    await RespondAsync(connection, HttpStatusCode.NotFound, "Endereço desconhecido.", cancellationToken);
                    continue;
                }

                if (callback.TryGetValue("code", out var code) && code.Length > 0)
                {
                    await RespondAsync(connection, HttpStatusCode.OK, _messages.Connected, cancellationToken);

                    return code;
                }

                await RespondAsync(connection, HttpStatusCode.OK, _messages.NotCompleted, cancellationToken);

                var error = callback.GetValueOrDefault("error");

                throw new AuthorizationRefusedException(error, error == "access_denied" ? _messages.Denied : _messages.Failed);
            }
        }
    }

    /// <summary>
    /// Solta a porta na hora, de forma síncrona. Uma autorização nova precisa
    /// dela, e não pode esperar a anterior terminar de se desfazer.
    /// </summary>
    public void StopListening()
    {
        foreach (var listener in _listeners)
        {
            listener.Stop();
        }
    }

    public async ValueTask DisposeAsync()
    {
        StopListening();
        await _stopping.CancelAsync();

        _connections.Writer.TryComplete();
        _stopping.Dispose();
    }

    private void Listen(IPAddress address, int port)
    {
        var listener = new TcpListener(address, port);
        listener.Start();
        _listeners.Add(listener);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;

        _ = AcceptAsync(listener);
    }

    private async Task AcceptAsync(TcpListener listener)
    {
        try
        {
            while (!_stopping.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(_stopping.Token);
                await _connections.Writer.WriteAsync(client, _stopping.Token);
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            // Parou de escutar: fim normal.
        }
    }

    /// <summary>Os parâmetros de <c>GET /callback?…</c>, ou <c>null</c> para qualquer outra coisa.</summary>
    private async Task<Dictionary<string, string>?> ReadCallbackAsync(TcpClient connection, CancellationToken cancellationToken)
    {
        var stream = connection.GetStream();
        var buffer = new byte[MaxRequestBytes];
        var read = 0;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));

        try
        {
            while (read < buffer.Length)
            {
                var count = await stream.ReadAsync(buffer.AsMemory(read), timeout.Token);

                if (count == 0)
                {
                    break;
                }

                read += count;

                if (buffer.AsSpan(0, read).IndexOf("\r\n"u8) >= 0)
                {
                    break;
                }
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return null;
        }

        var text = Encoding.ASCII.GetString(buffer, 0, read);
        var requestLine = text.Split("\r\n", 2)[0].Split(' ');

        if (requestLine.Length < 2 || requestLine[0] != "GET")
        {
            return null;
        }

        var target = requestLine[1];
        var question = target.IndexOf('?');
        var path = question < 0 ? target : target[..question];

        if (path != CallbackPath || question < 0)
        {
            return null;
        }

        _logger.LogInformation("{Event}", _messages.ReceivedLogEvent);

        return target[(question + 1)..]
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(pair => pair.Split('=', 2))
            .GroupBy(pair => Uri.UnescapeDataString(pair[0]), StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.First() is { Length: 2 } pair ? Uri.UnescapeDataString(pair[1].Replace('+', ' ')) : string.Empty,
                StringComparer.Ordinal);
    }

    private static async Task RespondAsync(
        TcpClient connection,
        HttpStatusCode status,
        string message,
        CancellationToken cancellationToken)
    {
        var html =
            "<!doctype html><html lang=\"pt-BR\"><head><meta charset=\"utf-8\"><title>MyTaskApp</title>"
            + "<style>body{font-family:Segoe UI,system-ui,sans-serif;background:#17181C;color:#E7E8EC;"
            + "display:flex;align-items:center;justify-content:center;height:100vh;margin:0}"
            + "p{max-width:28rem;line-height:1.5;font-size:1.05rem}</style></head>"
            + $"<body><p>{WebUtility.HtmlEncode(message)}</p></body></html>";

        var body = Encoding.UTF8.GetBytes(html);
        var head = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {(int)status} {status}\r\n"
            + "Content-Type: text/html; charset=utf-8\r\n"
            + $"Content-Length: {body.Length}\r\n"
            + "Cache-Control: no-store\r\n"
            + "Connection: close\r\n\r\n");

        try
        {
            var stream = connection.GetStream();
            await stream.WriteAsync(head, cancellationToken);
            await stream.WriteAsync(body, cancellationToken);
        }
        catch (IOException)
        {
            // O navegador fechou antes de ler: a autorização já veio, segue.
        }
    }
}
