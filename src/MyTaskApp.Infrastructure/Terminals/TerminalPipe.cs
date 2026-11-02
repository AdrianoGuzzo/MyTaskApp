using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;

namespace MyTaskApp.Infrastructure.Terminals;

/// <summary>
/// O canal local do terminal do agente (ADR-098): um named pipe só do usuário
/// (<see cref="PipeOptions.CurrentUserOnly"/>; no Linux, Unix socket com a
/// mesma restrição), separado do canal de avisos. O nome sai da pasta do
/// banco, como o do canal de avisos: instalações isoladas não se misturam.
/// </summary>
public static class TerminalPipe
{
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);

    public static string NameFor(string databasePath)
    {
        var identity = $"{Environment.UserName}|{Path.GetFullPath(databasePath).ToUpperInvariant()}";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)))[..16];

        return $"MyTaskApp.Terminal.{hash}";
    }

    /// <summary>Conecta ao agente; sem ele, <see cref="DomainException"/> com o que fazer.</summary>
    public static async Task<Stream> ConnectAsync(string pipeName, CancellationToken cancellationToken = default)
    {
        var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

        try
        {
            await client.ConnectAsync(ConnectTimeout, cancellationToken).ConfigureAwait(false);
            return client;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await client.DisposeAsync().ConfigureAwait(false);
            throw new DomainException("O MyTaskApp Agent, que hospeda o terminal, não respondeu nesta máquina. Tente de novo em instantes.");
        }
    }
}

/// <summary>O agente ouvindo o canal local do terminal: cada conexão vai ao <see cref="LocalTerminalServer"/>.</summary>
public sealed partial class TerminalPipeListener(
    string pipeName,
    LocalTerminalServer server,
    ILogger<TerminalPipeListener> logger) : IAsyncDisposable
{
    /// <summary>Janelas e vistas ao mesmo tempo. Cada sessão tem poucas; isto é o teto do processo.</summary>
    public const int MaxConnections = 32;

    private readonly CancellationTokenSource _stopping = new();

    private readonly SemaphoreSlim _slots = new(MaxConnections, MaxConnections);

    private Task? _loop;

    private int _disposed;

    public string PipeName => pipeName;

    public void Start()
    {
        if (_loop is not null || Volatile.Read(ref _disposed) == 1)
        {
            return;
        }

        _loop = Task.Run(() => ListenAsync(_stopping.Token));
        LogListening(pipeName);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _stopping.CancelAsync().ConfigureAwait(false);

        if (_loop is not null)
        {
            try
            {
                await _loop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _stopping.Dispose();
        _slots.Dispose();
    }

    private async Task ListenAsync(CancellationToken stopping)
    {
        var connections = new List<Task>();

        while (!stopping.IsCancellationRequested)
        {
            try
            {
                await _slots.WaitAsync(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

            try
            {
                await pipe.WaitForConnectionAsync(stopping).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                _slots.Release();
                break;
            }
            catch (IOException exception)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                _slots.Release();
                LogListenFailed(exception);
                await Task.Delay(TimeSpan.FromSeconds(1), stopping).ConfigureAwait(false);
                continue;
            }

            connections.RemoveAll(connection => connection.IsCompleted);
            connections.Add(ServeAsync(pipe, stopping));
        }

        await Task.WhenAll(connections).ConfigureAwait(false);
    }

    private async Task ServeAsync(NamedPipeServerStream pipe, CancellationToken stopping)
    {
        try
        {
            await server.ServeAsync(pipe, stopping).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            LogServeFailed(exception);
        }
        finally
        {
            _slots.Release();
        }
    }

    [LoggerMessage(EventId = 9831, Level = LogLevel.Information, Message = "TerminalPipeListening {PipeName}")]
    private partial void LogListening(string pipeName);

    [LoggerMessage(EventId = 9832, Level = LogLevel.Warning, Message = "TerminalPipeListenFailed")]
    private partial void LogListenFailed(Exception exception);

    [LoggerMessage(EventId = 9833, Level = LogLevel.Error, Message = "TerminalPipeServeFailed")]
    private partial void LogServeFailed(Exception exception);
}
