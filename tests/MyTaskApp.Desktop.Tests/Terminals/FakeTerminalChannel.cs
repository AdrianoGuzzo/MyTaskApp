using System.Text;
using System.Threading.Channels;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain.Agents;

namespace MyTaskApp.Desktop.Tests.Terminals;

/// <summary>Um canal de terminal de mentira: o teste manda o que o agente mandaria e lê o que a vista pediu.</summary>
internal sealed class FakeTerminalChannel : ITerminalChannel
{
    private readonly Channel<TerminalMessage?> _incoming = Channel.CreateUnbounded<TerminalMessage?>();

    private long _offset;

    public List<TerminalInput> Sent { get; } = [];

    public List<bool> Stops { get; } = [];

    public bool Disposed { get; private set; }

    /// <summary>Faz o próximo envio falhar, como um pipe quebrado.</summary>
    public bool BreakOnSend { get; set; }

    public void Snapshot(string text)
    {
        _incoming.Writer.TryWrite(new TerminalOutput(_offset, Encoding.UTF8.GetBytes(text), Reset: true));
    }

    public void Print(string text)
    {
        var data = Encoding.UTF8.GetBytes(text);
        _incoming.Writer.TryWrite(new TerminalOutput(_offset, data, Reset: false));
        _offset += data.Length;
    }

    /// <summary>Um pedaço que pula bytes: a vista perdeu saída.</summary>
    public void PrintAfterAGap(string text)
    {
        _offset += 100;
        Print(text);
    }

    public void Send(TerminalMessage message) => _incoming.Writer.TryWrite(message);

    public void Close() => _incoming.Writer.TryComplete();

    public static TerminalSessionStatus Status(
        TerminalSize? size = null,
        AgentActivity activity = AgentActivity.Unknown,
        TerminalDialog? dialog = null,
        bool running = true,
        bool bypassed = false,
        bool monitored = true,
        TerminalLease? lease = null,
        IReadOnlyList<TerminalClientView>? clients = null) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            @"C:\wt\eco",
            4242,
            DateTimeOffset.UnixEpoch,
            size ?? new TerminalSize(80, 24),
            running ? TerminalProcessState.Running : TerminalProcessState.Exited,
            running ? null : 0,
            activity,
            null,
            dialog,
            lease,
            clients ?? [],
            monitored,
            bypassed,
            null,
            null);

    public async ValueTask<TerminalMessage?> ReadAsync(CancellationToken cancellationToken = default)
    {
        if (await _incoming.Reader.WaitToReadAsync(cancellationToken) && _incoming.Reader.TryRead(out var message))
        {
            return message;
        }

        return null;
    }

    public Task SendAsync(TerminalInput input, CancellationToken cancellationToken = default)
    {
        if (BreakOnSend)
        {
            throw new IOException("O pipe quebrou.");
        }

        lock (Sent)
        {
            Sent.Add(input);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(bool force, CancellationToken cancellationToken = default)
    {
        Stops.Add(force);
        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
