using System.Text;
using System.Threading.Channels;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;

namespace MyTaskApp.Application.Tests.Terminals;

/// <summary>Um pseudoterminal de mentira: o teste escreve a saída e lê o que chegou como entrada.</summary>
internal sealed class FakePseudoTerminal(TerminalSize size) : IPseudoTerminal
{
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>();

    private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly List<byte> _input = [];

    public FakePseudoTerminal()
        : this(new TerminalSize(80, 24))
    {
    }

    public int ProcessId => 4242;

    public DateTimeOffset ProcessStartedAt { get; } = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    public TerminalSize Size { get; private set; } = size;

    public Task<int?> Exited => _exited.Task;

    public List<TerminalSize> Resizes { get; } = [];

    public bool Closed { get; private set; }

    public bool Killed { get; private set; }

    public string Input
    {
        get
        {
            lock (_input)
            {
                return Encoding.UTF8.GetString([.. _input]);
            }
        }
    }

    public void Print(string text) => _output.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

    public void Exit(int? code)
    {
        _output.Writer.TryComplete();
        _exited.TrySetResult(code);
    }

    public void ClearInput()
    {
        lock (_input)
        {
            _input.Clear();
        }
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!await _output.Reader.WaitToReadAsync(cancellationToken))
        {
            return 0;
        }

        var chunk = await _output.Reader.ReadAsync(cancellationToken);
        chunk.CopyTo(buffer);
        return chunk.Length;
    }

    public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        if (_exited.Task.IsCompleted)
        {
            throw new DomainException("O terminal já fechou: a entrada não foi entregue.");
        }

        lock (_input)
        {
            _input.AddRange(data.ToArray());
        }

        return ValueTask.CompletedTask;
    }

    public void Resize(TerminalSize size)
    {
        Size = size;
        Resizes.Add(size);
    }

    public void Close()
    {
        Closed = true;
        Exit(0);
    }

    public void Kill()
    {
        Killed = true;
        Exit(1);
    }

    public ValueTask DisposeAsync()
    {
        Exit(0);
        return ValueTask.CompletedTask;
    }
}
