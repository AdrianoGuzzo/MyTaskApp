using System.Text;
using MyTaskApp.Application.Terminals;

namespace MyTaskApp.Infrastructure.Tests.Terminals;

/// <summary>
/// Liga um pseudoterminal a um <see cref="VtScreen"/> e espera texto na tela.
/// O ConPTY repinta com posição absoluta e troca espaço por movimento de
/// cursor, então só a tela montada diz o que o usuário veria.
/// </summary>
internal sealed class PtyHarness : IAsyncDisposable
{
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);

    private readonly VtScreen _screen;

    private readonly Task _reading;

    private readonly CancellationTokenSource _stop = new();

    public PtyHarness(IPseudoTerminal terminal)
    {
        Terminal = terminal;
        _screen = new VtScreen(terminal.Size, scrollback: 500);
        _reading = Task.Run(ReadAsync);
    }

    public IPseudoTerminal Terminal { get; }

    /// <summary>Completa quando a leitura chegou ao fim do terminal.</summary>
    public Task Reading => _reading;

    public string Text
    {
        get
        {
            lock (_screen)
            {
                var history = Enumerable.Range(0, _screen.ScrollbackCount).Select(_screen.ScrollbackText);
                return string.Join("\n", history.Append(_screen.ScreenText()));
            }
        }
    }

    public static string FakeClaude =>
        Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "MyTaskApp.FakeClaude.exe" : "MyTaskApp.FakeClaude");

    public static PseudoTerminalStart StartOf(params string[] arguments) =>
        new(FakeClaude, arguments, AppContext.BaseDirectory, null, new TerminalSize(100, 30));

    public Task SendAsync(string text) =>
        Terminal.WriteAsync(Encoding.UTF8.GetBytes(text), TestContext.Current.CancellationToken).AsTask();

    public async Task<string> WaitForAsync(string expected)
    {
        var until = DateTimeOffset.UtcNow + Patience;

        while (DateTimeOffset.UtcNow < until)
        {
            var text = Text;

            if (text.Contains(expected, StringComparison.Ordinal))
            {
                return text;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        throw new TimeoutException($"Não apareceu \"{expected}\". A tela:\n{Text}");
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync();
        await Terminal.DisposeAsync();
        await Task.WhenAny(_reading, Task.Delay(Patience));
        _stop.Dispose();
    }

    private async Task ReadAsync()
    {
        var buffer = new byte[8192];

        try
        {
            int count;

            while ((count = await Terminal.ReadAsync(buffer, _stop.Token)) > 0)
            {
                lock (_screen)
                {
                    if (_screen.Size != Terminal.Size)
                    {
                        _screen.Resize(Terminal.Size);
                    }

                    _screen.Feed(buffer.AsSpan(0, count));
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
