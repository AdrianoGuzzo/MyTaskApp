using System.Threading.Channels;

namespace MyTaskApp.Infrastructure.Terminals;

/// <summary>
/// Lê a saída de um pseudoterminal numa thread própria e a entrega por um canal
/// limitado (ADR-098). A leitura do pipe é bloqueante e não se cancela; o canal
/// deixa quem consome cancelar sem perder o que já veio. Cheio, ele segura a
/// thread de leitura, e o programa do outro lado espera para escrever: é a
/// contrapressão, em vez de memória sem fim.
/// </summary>
internal sealed class PumpedOutput
{
    /// <summary>Pedaços de até 16 KiB: 64 deles fazem 1 MiB parado, no máximo.</summary>
    private const int ChunkSize = 16 * 1024;

    private const int MaxPendingChunks = 64;

    private readonly Channel<byte[]> _chunks = Channel.CreateBounded<byte[]>(
        new BoundedChannelOptions(MaxPendingChunks) { SingleReader = true, SingleWriter = true });

    private byte[]? _current;

    private int _offset;

    /// <param name="read">Lê do sistema; <c>0</c> = fim. Exceção também é fim.</param>
    public PumpedOutput(Func<byte[], int> read, string name)
    {
        var thread = new Thread(() => Pump(read)) { IsBackground = true, Name = name };
        thread.Start();
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_current is null || _offset >= _current.Length)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false)
                || !_chunks.Reader.TryRead(out _current))
            {
                return 0;
            }

            _offset = 0;
        }

        var count = Math.Min(buffer.Length, _current.Length - _offset);
        _current.AsMemory(_offset, count).CopyTo(buffer);
        _offset += count;
        return count;
    }

    private void Pump(Func<byte[], int> read)
    {
        var buffer = new byte[ChunkSize];

        try
        {
            while (true)
            {
                var count = read(buffer);

                if (count <= 0)
                {
                    break;
                }

                // Espera vaga no canal: é a contrapressão. Síncrono de propósito,
                // a thread é só desta leitura.
                _chunks.Writer.WriteAsync(buffer.AsSpan(0, count).ToArray()).AsTask().GetAwaiter().GetResult();
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or ChannelClosedException)
        {
            // Pipe fechado no meio da leitura: é o fim do terminal.
        }
        finally
        {
            _chunks.Writer.TryComplete();
        }
    }
}
