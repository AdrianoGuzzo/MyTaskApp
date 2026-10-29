using System.IO.Pipes;
using System.Text;
using MyTaskApp.Application.Terminals;

namespace MyTaskApp.TerminalAttach;

/// <summary>
/// <c>MyTaskApp.TerminalAttach --pipe &lt;nome&gt; --session &lt;id&gt; [--title &lt;texto&gt;]</c>:
/// mostra nesta janela a sessão que o agente hospeda (ADR-098). O que se
/// digita aqui vai para ela; fechar a janela só desliga a vista.
/// </summary>
internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        if (!TryParse(args, out var pipe, out var session, out var title))
        {
            await Console.Error.WriteLineAsync("Uso: MyTaskApp.TerminalAttach --pipe <nome> --session <id> [--title <texto>]");
            return 2;
        }

        var stdout = Console.OpenStandardOutput();
        Stream connection;

        try
        {
            var client = new NamedPipeClientStream(".", pipe, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            await client.ConnectAsync(5000);
            connection = client;
        }
        catch (Exception exception) when (exception is TimeoutException or IOException or UnauthorizedAccessException)
        {
            await WriteAsync(stdout, "O MyTaskApp Agent não respondeu nesta máquina. Feche esta janela e abra o Claude de novo pelo MyTaskApp.\r\n");
            WaitForKey();
            return 1;
        }

        using var console = RawConsole.Enter();
        await using var channel = await LocalTerminalChannel.AttachAsync(
            connection,
            session,
            TerminalClientKind.NativeWindow,
            "Janela do terminal",
            RawConsole.Size() is var (columns, rows) ? new TerminalSize(columns, rows).Clamp() : null);

        await WriteAsync(stdout, Title(title));

        using var stop = new CancellationTokenSource();
        var output = ShowAsync(channel, stdout, title, stop.Token);
        var input = TypeAsync(channel, stop.Token);
        var resize = FollowSizeAsync(channel, stop.Token);

        var first = await Task.WhenAny(output, input);
        await stop.CancelAsync();

        if (first == output)
        {
            // O agente fechou: a sessão saiu, ou o agente parou. A tela final fica até a pessoa fechar.
            var ending = await output;
            await WriteAsync(stdout, $"\u001b[?1049l\u001b[0m\r\n\r\n{ending}\r\nPressione uma tecla para fechar.\r\n");
            console.Dispose();
            WaitForKey();
        }

        await Task.WhenAll(Quietly(input), Quietly(resize));
        return 0;
    }

    /// <summary>A saída da sessão nesta janela, até o fim. Devolve a frase do fim.</summary>
    private static async Task<string> ShowAsync(LocalTerminalChannel channel, Stream stdout, string title, CancellationToken cancellationToken)
    {
        var ending = "O agente fechou a conexão com esta sessão.";

        try
        {
            while (await channel.ReadAsync(cancellationToken) is { } message)
            {
                switch (message)
                {
                    case TerminalOutput output:
                        await stdout.WriteAsync(output.Data, cancellationToken);
                        await stdout.FlushAsync(cancellationToken);
                        break;
                    case TerminalStateMessage { Status: { IsRunning: false } status }:
                        ending = status.ExitCode is { } code
                            ? $"O Claude saiu (código {code})."
                            : "O Claude saiu.";
                        break;
                    case TerminalStateMessage { Status.Lease: { Kind: TerminalClientKind.Remote } lease }:
                        await WriteAsync(stdout, Title($"{title} · {lease.Label} está no controle"));
                        break;
                    case TerminalStateMessage:
                        await WriteAsync(stdout, Title(title));
                        break;
                    case TerminalNotice notice:
                        await WriteAsync(stdout, Title($"{title} · {notice.Text}"));
                        break;
                    case TerminalRefusal refusal:
                        await WriteAsync(stdout, Title($"{title} · {refusal.Text}"));
                        break;
                }
            }
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException or EndOfStreamException or InvalidDataException)
        {
        }

        return ending;
    }

    /// <summary>O teclado desta janela, cru, para a sessão. Termina quando a janela fecha.</summary>
    private static async Task TypeAsync(LocalTerminalChannel channel, CancellationToken cancellationToken)
    {
        var stdin = Console.OpenStandardInput();
        var buffer = new byte[4096];

        while (!cancellationToken.IsCancellationRequested)
        {
            // A leitura do console não se cancela: ela vai numa thread, e a janela fechando a termina.
            var count = await Task.Run(() => stdin.Read(buffer, 0, buffer.Length), CancellationToken.None).WaitAsync(cancellationToken);

            if (count <= 0)
            {
                return;
            }

            await channel.SendKeysAsync(buffer.AsMemory(0, count), cancellationToken);
        }
    }

    /// <summary>Redimensionar a janela redimensiona a sessão: ela é quem dita o tamanho.</summary>
    private static async Task FollowSizeAsync(LocalTerminalChannel channel, CancellationToken cancellationToken)
    {
        var last = RawConsole.Size();

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(200, cancellationToken);

            if (RawConsole.Size() is { } size && size != last)
            {
                last = size;
                await channel.SendAsync(new TerminalResize(new TerminalSize(size.Columns, size.Rows).Clamp()), cancellationToken);
            }
        }
    }

    private static string Title(string title) => $"\u001b]0;{title.Replace('\u0007', ' ').Replace('\u001b', ' ')}\u0007";

    private static async Task WriteAsync(Stream stdout, string text)
    {
        await stdout.WriteAsync(Encoding.UTF8.GetBytes(text));
        await stdout.FlushAsync();
    }

    private static void WaitForKey()
    {
        try
        {
            Console.OpenStandardInput().ReadByte();
        }
        catch (IOException)
        {
        }
    }

    private static async Task Quietly(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception) when (exception is OperationCanceledException or IOException or ObjectDisposedException)
        {
        }
    }

    private static bool TryParse(string[] args, out string pipe, out Guid session, out string title)
    {
        pipe = string.Empty;
        session = Guid.Empty;
        title = "Claude Code · MyTaskApp";

        for (var i = 0; i + 1 < args.Length; i += 2)
        {
            switch (args[i])
            {
                case "--pipe":
                    pipe = args[i + 1];
                    break;
                case "--session" when Guid.TryParse(args[i + 1], out var id):
                    session = id;
                    break;
                case "--title":
                    title = args[i + 1];
                    break;
                default:
                    return false;
            }
        }

        return pipe.Length > 0 && pipe.StartsWith("MyTaskApp.Terminal.", StringComparison.Ordinal) && session != Guid.Empty;
    }
}
