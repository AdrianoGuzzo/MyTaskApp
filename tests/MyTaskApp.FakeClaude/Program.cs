using System.Diagnostics;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace MyTaskApp.FakeClaude;

/// <summary>
/// Um Claude Code interativo de mentira (ADR-098). Fala por um TTY como o de
/// verdade: liga o bracketed paste, lê o teclado cru, Esc interrompe, a
/// permissão e a pergunta se respondem por tecla, e cada passo manda o hook
/// HTTP para o endereço do ambiente (<c>MYTASKAPP_EVENTS_URL</c>).
/// </summary>
/// <remarks>
/// Linhas que os testes procuram (sempre no começo da linha):
/// <c>READY session=… size=CxL</c>, <c>ECHO: …</c>, <c>WORKING</c>,
/// <c>INTERRUPTED</c>, <c>PERMISSION …</c>, <c>ALLOWED</c>/<c>DENIED</c>,
/// <c>QUESTION …</c>, <c>ANSWER …</c>, <c>SIZE CxL</c>, <c>RESIZED CxL</c>,
/// <c>CHILD pid</c>, <c>ENV nome=valor</c>, <c>BYE</c>. Comandos: <c>work</c>,
/// <c>approve</c>, <c>ask</c>, <c>size</c>, <c>env NOME</c>, <c>spawn</c>,
/// <c>crash</c>, <c>exit</c>; o resto é eco.
/// </remarks>
internal static class Program
{
    private const char Escape = '\u001b';

    private static readonly Channel<Key> Keys = Channel.CreateUnbounded<Key>();

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    private static string _sessionId = Guid.NewGuid().ToString();

    private static string _cwd = Environment.CurrentDirectory;

    public static async Task<int> Main(string[] args)
    {
        if (args is ["--child"])
        {
            // O neto do teste de encerramento forçado: só fica vivo.
            await Task.Delay(Timeout.Infinite);
            return 0;
        }

        if (args is ["--error-mode"])
        {
            // O teste do ProcessRunner: o modo de erro do loader que o filho herdou.
            Console.WriteLine(OperatingSystem.IsWindows() ? RawInput.ErrorMode() : 0);
            return 0;
        }

        _sessionId = ValueOf(args, "--session-id") ?? ValueOf(args, "--resume") ?? _sessionId;
        Console.OutputEncoding = new UTF8Encoding(false);
        Console.InputEncoding = new UTF8Encoding(false);
        RawInput.Enable();
        var reader = new Thread(ReadKeys) { IsBackground = true };
        reader.Start();
        _ = WatchSizeAsync();

        // Como o Claude: bracketed paste ligado enquanto roda.
        Write($"{Escape}[?2004h");
        WriteLine($"READY session={_sessionId} size={SizeText()} args={string.Join(' ', args)}");
        await HookAsync("SessionStart", new { source = ValueOf(args, "--resume") is null ? "startup" : "resume" });
        Prompt();

        var line = new StringBuilder();

        while (await Keys.Reader.WaitToReadAsync())
        {
            if (!Keys.Reader.TryRead(out var key))
            {
                continue;
            }

            switch (key.Kind)
            {
                case KeyKind.Text:
                    line.Append(key.Text);
                    Write(key.Text);
                    break;
                case KeyKind.Paste:
                    line.Append(key.Text);
                    Write(key.Text);
                    break;
                case KeyKind.Backspace when line.Length > 0:
                    line.Length--;
                    Write("\b \b");
                    break;
                case KeyKind.Enter:
                    var text = line.ToString().Trim();
                    line.Clear();
                    WriteLine(string.Empty);

                    if (await RunAsync(text) is { } exitCode)
                    {
                        Write($"{Escape}[?2004l");
                        return exitCode;
                    }

                    Prompt();
                    break;
            }
        }

        return 0;
    }

    private static async Task<int?> RunAsync(string text)
    {
        switch (text)
        {
            case "":
                return null;
            case "crash":
                Environment.Exit(3);
                return 3;
            case "exit":
                WriteLine("BYE");
                await HookAsync("SessionEnd", new { reason = "prompt_input_exit" });
                return 0;
            case "size":
                WriteLine($"SIZE {SizeText()}");
                return null;
            case var env when env.StartsWith("env ", StringComparison.Ordinal):
                var name = env[4..].Trim();
                WriteLine($"ENV {name}={Environment.GetEnvironmentVariable(name) ?? "(unset)"}");
                return null;
            case "spawn":
                using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--child") { UseShellExecute = false }))
                {
                    WriteLine($"CHILD {child?.Id}");
                }

                return null;
            case "work":
                await HookAsync("UserPromptSubmit", new { prompt = text });
                await WorkUntilEscapeAsync();
                return null;
            case "approve":
                await ApproveAsync();
                return null;
            case "ask":
                await AskAsync();
                return null;
            default:
                await HookAsync("UserPromptSubmit", new { prompt = text });
                WriteLine($"ECHO: {text}");
                await HookAsync("Stop", new { last_assistant_message = $"ECHO: {text}" });
                return null;
        }
    }

    private static async Task WorkUntilEscapeAsync()
    {
        WriteLine("WORKING");
        var frames = "|/-\\";
        var tick = 0;
        var next = Keys.Reader.ReadAsync().AsTask();

        while (true)
        {
            var winner = await Task.WhenAny(next, Task.Delay(100));

            if (winner == next)
            {
                if (next.Result.Kind == KeyKind.Escape)
                {
                    WriteLine($"\r{Escape}[2KINTERRUPTED");
                    await HookAsync("Stop", new { last_assistant_message = "Interrupted by user" });
                    return;
                }

                next = Keys.Reader.ReadAsync().AsTask();
            }

            Write($"\r{Escape}[2K{frames[tick++ % frames.Length]} working {tick}");
        }
    }

    private static async Task ApproveAsync()
    {
        var input = new { command = "echo hi" };
        await HookAsync("PreToolUse", new { tool_name = "Bash", tool_input = input });
        await HookAsync("PermissionRequest", new { tool_name = "Bash", tool_input = input, permission_mode = "default" });
        await HookAsync("Notification", new { notification_type = "permission_prompt", message = "Claude needs your permission to use Bash" });
        WriteLine("PERMISSION Bash(echo hi) 1=Yes 2=No Esc=No");

        while (true)
        {
            var key = await Keys.Reader.ReadAsync();

            if (key.Kind == KeyKind.Enter || key is { Kind: KeyKind.Text, Text: "1" })
            {
                WriteLine("ALLOWED");
                await HookAsync("PostToolUse", new { tool_name = "Bash", tool_input = input });
                await HookAsync("Stop", new { last_assistant_message = "hi" });
                return;
            }

            if (key.Kind == KeyKind.Escape || key is { Kind: KeyKind.Text, Text: "2" })
            {
                WriteLine("DENIED");
                await HookAsync("Stop", new { last_assistant_message = "Negado." });
                return;
            }
        }
    }

    private static async Task AskAsync()
    {
        var questions = new[]
        {
            new { question = "Qual cor?", header = "Cor", multiSelect = false, options = new[] { new { label = "Azul" }, new { label = "Verde" } } },
        };
        await HookAsync("PreToolUse", new { tool_name = "AskUserQuestion", tool_input = new { questions } });
        WriteLine("QUESTION Qual cor? 1=Azul 2=Verde");

        while (true)
        {
            var key = await Keys.Reader.ReadAsync();

            if (key is { Kind: KeyKind.Text, Text: "1" or "2" })
            {
                WriteLine($"ANSWER {(key.Text == "1" ? "Azul" : "Verde")}");
                await HookAsync("PostToolUse", new { tool_name = "AskUserQuestion" });
                await HookAsync("Stop", new { last_assistant_message = "Anotado." });
                return;
            }

            if (key.Kind == KeyKind.Escape)
            {
                WriteLine("ANSWER (nenhuma)");
                await HookAsync("Stop", new { last_assistant_message = "Sem resposta." });
                return;
            }
        }
    }

    private static async Task HookAsync(string name, object fields)
    {
        if (Environment.GetEnvironmentVariable("MYTASKAPP_EVENTS_URL") is not { Length: > 0 } url)
        {
            return;
        }

        var payload = JsonSerializer.SerializeToNode(fields)!.AsObject();
        payload["hook_event_name"] = name;
        payload["session_id"] = _sessionId;
        payload["cwd"] = _cwd;

        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", Environment.GetEnvironmentVariable("MYTASKAPP_HOOK_TOKEN") ?? string.Empty);
        request.Headers.Add("X-MyTaskApp-Session", Environment.GetEnvironmentVariable("MYTASKAPP_AGENT_SESSION_ID") ?? string.Empty);
        request.Headers.Add("X-MyTaskApp-Task", Environment.GetEnvironmentVariable("MYTASKAPP_TASK_ID") ?? string.Empty);

        try
        {
            using var response = await Http.SendAsync(request);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // Como o Claude: hook que falha não para a sessão.
        }
    }

    private static async Task WatchSizeAsync()
    {
        var last = SizeText();

        while (true)
        {
            await Task.Delay(100);
            var now = SizeText();

            if (now != last)
            {
                last = now;
                WriteLine($"RESIZED {now}");
            }
        }
    }

    private static void ReadKeys()
    {
        // No Unix, o OpenStandardInput de um TTY passa pelo editor de linha do
        // .NET (eco próprio, e o Esc some): lê o descritor 0 direto.
        using var input = OperatingSystem.IsWindows()
            ? Console.OpenStandardInput()
            : new FileStream(new SafeFileHandle(0, ownsHandle: false), FileAccess.Read, bufferSize: 0);
        var buffer = new byte[4096];
        var decoder = new UTF8Encoding(false).GetDecoder();
        var chars = new char[8192];
        var pending = new StringBuilder();
        var paste = (StringBuilder?)null;

        while (true)
        {
            var count = input.Read(buffer, 0, buffer.Length);

            if (count <= 0)
            {
                Keys.Writer.TryComplete();
                return;
            }

            var decoded = decoder.GetChars(buffer, 0, count, chars, 0);
            pending.Append(chars, 0, decoded);
            var text = pending.ToString();
            pending.Clear();
            var i = 0;

            while (i < text.Length)
            {
                var c = text[i];

                if (c == Escape)
                {
                    // Sequência CSI: ESC [ … final. Sozinho, é a tecla Esc.
                    if (i + 1 < text.Length && text[i + 1] == '[')
                    {
                        var end = i + 2;

                        while (end < text.Length && text[end] is < '@' or > '~')
                        {
                            end++;
                        }

                        if (end >= text.Length)
                        {
                            pending.Append(text, i, text.Length - i);
                            break;
                        }

                        var sequence = text[(i + 2)..(end + 1)];

                        if (sequence == "200~")
                        {
                            paste = new StringBuilder();
                        }
                        else if (sequence == "201~" && paste is not null)
                        {
                            Keys.Writer.TryWrite(new Key(KeyKind.Paste, paste.ToString()));
                            paste = null;
                        }

                        i = end + 1;
                        continue;
                    }

                    if (paste is null)
                    {
                        Keys.Writer.TryWrite(new Key(KeyKind.Escape, string.Empty));
                    }

                    i++;
                    continue;
                }

                if (paste is not null)
                {
                    paste.Append(c);
                }
                else if (c is '\r' or '\n')
                {
                    Keys.Writer.TryWrite(new Key(KeyKind.Enter, string.Empty));
                }
                else if (c is '\b' or '\u007f')
                {
                    Keys.Writer.TryWrite(new Key(KeyKind.Backspace, string.Empty));
                }
                else if (c == '\u0003')
                {
                    // Ctrl+C com a linha vazia sai, como o Claude na segunda vez.
                    Environment.Exit(130);
                }
                else if (!char.IsControl(c))
                {
                    Keys.Writer.TryWrite(new Key(KeyKind.Text, c.ToString()));
                }

                i++;
            }
        }
    }

    private static string SizeText()
    {
        try
        {
            return $"{Console.WindowWidth}x{Console.WindowHeight}";
        }
        catch (IOException)
        {
            return "?x?";
        }
    }

    private static void Prompt() => Write("> ");

    private static void Write(string text)
    {
        Console.Out.Write(text);
        Console.Out.Flush();
    }

    private static void WriteLine(string text) => Write(text + "\r\n");

    private static string? ValueOf(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private enum KeyKind
    {
        Text,
        Paste,
        Enter,
        Escape,
        Backspace,
    }

    private sealed record Key(KeyKind Kind, string Text);
}

/// <summary>O teclado cru, como o Claude o põe: sem eco, sem esperar o Enter, com as teclas em VT.</summary>
internal static partial class RawInput
{
    private const uint EnableProcessedInput = 0x0001;

    private const uint EnableLineInput = 0x0002;

    private const uint EnableEchoInput = 0x0004;

    private const uint EnableVirtualTerminalInput = 0x0200;

    public static void Enable()
    {
        if (OperatingSystem.IsWindows())
        {
            var handle = GetStdHandle(-10);

            if (GetConsoleMode(handle, out var mode))
            {
                SetConsoleMode(handle, (mode & ~(EnableProcessedInput | EnableLineInput | EnableEchoInput)) | EnableVirtualTerminalInput);
            }

            return;
        }

        // Direto na libc: um stty filho não serve, porque o .NET devolve o modo
        // de linha ao TTY quando um filho termina (e sem PATH nem acha o stty).
        var termios = Marshal.AllocHGlobal(256);

        try
        {
            if (tcgetattr(0, termios) == 0)
            {
                cfmakeraw(termios);
                _ = tcsetattr(0, 0, termios);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(termios);
        }
    }

    [LibraryImport("libc", SetLastError = true)]
    private static partial int tcgetattr(int fd, nint termios);

    [LibraryImport("libc")]
    private static partial void cfmakeraw(nint termios);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int tcsetattr(int fd, int optionalActions, nint termios);

    public static uint ErrorMode() => GetErrorMode();

    [LibraryImport("kernel32.dll")]
    private static partial uint GetErrorMode();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetStdHandle(int handle);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetConsoleMode(nint handle, out uint mode);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetConsoleMode(nint handle, uint mode);
}
