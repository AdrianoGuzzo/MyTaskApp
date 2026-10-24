using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Abstractions;
using MyTaskApp.Application.LocalPeers;

namespace MyTaskApp.Infrastructure.Processes;

/// <summary>Quem abre o canal e quem chega nele: o agente escuta, o desktop conecta.</summary>
public enum LocalPeerRole
{
    Listen,
    Connect,
}

/// <summary>
/// O canal local entre o desktop e o agente (ADR-085): um named pipe só do
/// usuário (<see cref="PipeOptions.CurrentUserOnly"/>; no Linux o .NET faz dele
/// um Unix socket com a mesma restrição). Uma linha de JSON por aviso.
/// </summary>
/// <remarks>
/// <para>
/// Só avisos: "dados mudaram", "sincronize". Nada que execute passa por aqui —
/// pedido de execução é assinado e vem pelo servidor (ADR-087), e um processo
/// que conseguisse falar no pipe só faria a tela recarregar.
/// </para>
/// <para>
/// O nome sai da pasta do banco: duas instalações isoladas (os testes, o
/// <c>MYTASKAPP_DATA_DIR</c>) nunca falam uma com a outra.
/// </para>
/// </remarks>
public sealed class PipeLocalPeerChannel(
    string pipeName,
    LocalPeerRole role,
    ILogger<PipeLocalPeerChannel> logger,
    TimeSpan? reconnectDelay = null) : ILocalPeerChannel
{
    /// <summary>Uma linha maior que isto não é aviso: o canal fecha a conexão.</summary>
    public const int MaxLineLength = 4 * 1024;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(1);

    /// <summary>Sem o agente, o desktop tenta de novo neste ritmo.</summary>
    public static readonly TimeSpan DefaultReconnectDelay = TimeSpan.FromSeconds(5);

    private readonly TimeSpan _reconnectDelay = reconnectDelay ?? DefaultReconnectDelay;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly ConcurrentDictionary<PipeStream, SemaphoreSlim> _connections = new();

    private readonly CancellationTokenSource _stopping = new();

    private Task? _loop;

    private int _disposed;

    public event Action<LocalPeerMessage>? Received;

    public bool IsConnected => !_connections.IsEmpty;

    public string PipeName => pipeName;

    /// <summary>O nome do pipe desta instalação: o usuário e o caminho do banco, sem expor nenhum dos dois.</summary>
    public static string NameFor(string databasePath)
    {
        var identity = $"{Environment.UserName}|{Path.GetFullPath(databasePath).ToUpperInvariant()}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];

        return $"MyTaskApp.Peer.{hash}";
    }

    public void Start()
    {
        if (_loop is not null || Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        _loop = role is LocalPeerRole.Listen
            ? Task.Run(() => ListenAsync(_stopping.Token))
            : Task.Run(() => ConnectLoopAsync(_stopping.Token));
    }

    public async Task SendAsync(LocalPeerMessage message, CancellationToken cancellationToken)
    {
        var line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message, Json) + "\n");

        foreach (var (stream, gate) in _connections)
        {
            await gate.WaitAsync(cancellationToken);

            try
            {
                await stream.WriteAsync(line, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                // O outro lado saiu: a leitura dele termina e tira a conexão da lista.
            }
            finally
            {
                gate.Release();
            }
        }
    }

    /// <summary>A ponte e o contêiner soltam o mesmo canal: a segunda vez não faz nada.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stopping.CancelAsync();

        foreach (var stream in _connections.Keys)
        {
            await stream.DisposeAsync();
        }

        if (_loop is not null)
        {
            try
            {
                await _loop;
            }
            catch (OperationCanceledException)
            {
                // Parou.
            }
        }

        _stopping.Dispose();
    }

    private async Task ListenAsync(CancellationToken stopping)
    {
        var sessions = new List<Task>();

        while (!stopping.IsCancellationRequested)
        {
            var server = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await server.WaitForConnectionAsync(stopping);
            }
            catch (OperationCanceledException)
            {
                await server.DisposeAsync();
                break;
            }
            catch (IOException exception)
            {
                await server.DisposeAsync();
                logger.LogWarning(exception, "LocalPeerListenFailed");
                await DelayAsync(_reconnectDelay, stopping);
                continue;
            }

            sessions.RemoveAll(session => session.IsCompleted);
            sessions.Add(ServeAsync(server, stopping));
        }

        await Task.WhenAll(sessions);
    }

    private async Task ConnectLoopAsync(CancellationToken stopping)
    {
        while (!stopping.IsCancellationRequested)
        {
            var client = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await client.ConnectAsync(ConnectTimeout, stopping);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested)
            {
                await client.DisposeAsync();
                break;
            }
            catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
            {
                // Sem agente rodando (ou de outro usuário, que o CurrentUserOnly recusa): tenta mais tarde.
                await client.DisposeAsync();
                await DelayAsync(_reconnectDelay, stopping);
                continue;
            }

            await ServeAsync(client, stopping);
            await DelayAsync(_reconnectDelay, stopping);
        }
    }

    /// <summary>Lê uma conexão até ela fechar, repassando cada aviso conhecido.</summary>
    private async Task ServeAsync(PipeStream stream, CancellationToken stopping)
    {
        _connections[stream] = new SemaphoreSlim(1, 1);
        logger.LogInformation("LocalPeerConnected {Role}", role);

        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

            while (!stopping.IsCancellationRequested)
            {
                var line = await ReadLineAsync(reader, stopping);

                if (line is null)
                {
                    break;
                }

                if (Parse(line) is { } message)
                {
                    Received?.Invoke(message);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or OperationCanceledException or InvalidDataException)
        {
            // O outro lado saiu, ou mandou o que não é aviso.
        }
        finally
        {
            if (_connections.TryRemove(stream, out var gate))
            {
                gate.Dispose();
            }

            await stream.DisposeAsync();
            logger.LogInformation("LocalPeerDisconnected {Role}", role);
        }
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken stopping)
    {
        var line = new StringBuilder();
        var buffer = new char[1];

        while (true)
        {
            var read = await reader.ReadAsync(buffer, stopping);

            if (read == 0)
            {
                return line.Length == 0 ? null : line.ToString();
            }

            if (buffer[0] == '\n')
            {
                return line.ToString();
            }

            if (line.Length >= MaxLineLength)
            {
                throw new InvalidDataException("Linha grande demais para um aviso local.");
            }

            line.Append(buffer[0]);
        }
    }

    internal static LocalPeerMessage? Parse(string line)
    {
        try
        {
            var message = JsonSerializer.Deserialize<LocalPeerMessage>(line, Json);

            if (message is null || !LocalPeerMessageKinds.IsKnown(message.Kind))
            {
                return null;
            }

            // Só as áreas que existem: o resto do número é descartado.
            const DataArea all = DataArea.Tasks | DataArea.Time | DataArea.Tags | DataArea.StickyNotes | DataArea.Databases;
            return message with { Areas = message.Areas & all };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task DelayAsync(TimeSpan delay, CancellationToken stopping)
    {
        try
        {
            await Task.Delay(delay, stopping);
        }
        catch (OperationCanceledException)
        {
            // Parou durante a espera.
        }
    }
}
