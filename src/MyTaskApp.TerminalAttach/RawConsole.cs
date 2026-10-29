using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace MyTaskApp.TerminalAttach;

/// <summary>
/// O console desta janela em modo cru: cada tecla vai como o terminal a
/// codifica (setas, Ctrl+C, colar), sem eco nem edição de linha, e a saída
/// do Claude é interpretada como VT. Ao sair, tudo volta como estava.
/// </summary>
internal sealed partial class RawConsole : IDisposable
{
    private readonly Action _restore;

    private RawConsole(Action restore)
    {
        _restore = restore;
    }

    public static RawConsole Enter()
    {
        Console.InputEncoding = Encoding.UTF8;
        Console.OutputEncoding = Encoding.UTF8;

        if (OperatingSystem.IsWindows())
        {
            return EnterWindows();
        }

        return EnterUnix();
    }

    /// <summary>O tamanho da janela agora; <c>null</c> se o sistema não diz.</summary>
    public static (int Columns, int Rows)? Size()
    {
        try
        {
            return Console.WindowWidth > 0 && Console.WindowHeight > 0 ? (Console.WindowWidth, Console.WindowHeight) : null;
        }
        catch (Exception exception) when (exception is IOException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public void Dispose() => _restore();

    [SupportedOSPlatform("windows")]
    private static RawConsole EnterWindows()
    {
        const uint EnableProcessedOutput = 0x0001;
        const uint EnableVirtualTerminalProcessing = 0x0004;
        const uint DisableNewlineAutoReturn = 0x0008;
        const uint EnableExtendedFlags = 0x0080;
        const uint EnableVirtualTerminalInput = 0x0200;

        var input = GetStdHandle(-10);
        var output = GetStdHandle(-11);
        var hasInput = GetConsoleMode(input, out var inputMode);
        var hasOutput = GetConsoleMode(output, out var outputMode);

        // Sem eco, sem linha, sem Ctrl+C virando sinal, sem a seleção rápida
        // (que roubaria o mouse do Claude): as teclas chegam como VT.
        if (hasInput)
        {
            SetConsoleMode(input, EnableVirtualTerminalInput | EnableExtendedFlags);
        }

        if (hasOutput)
        {
            SetConsoleMode(output, EnableProcessedOutput | EnableVirtualTerminalProcessing | DisableNewlineAutoReturn);
        }

        return new RawConsole(() =>
        {
            if (hasInput)
            {
                SetConsoleMode(input, inputMode);
            }

            if (hasOutput)
            {
                SetConsoleMode(output, outputMode);
            }
        });
    }

    private static RawConsole EnterUnix()
    {
        var saved = Stty("-g")?.Trim();
        Stty("raw", "-echo");

        return new RawConsole(() =>
        {
            if (!string.IsNullOrEmpty(saved))
            {
                Stty(saved);
            }
            else
            {
                Stty("sane");
            }
        });
    }

    /// <summary>O <c>stty</c> com a entrada desta janela: é nela que ele mexe.</summary>
    private static string? Stty(params string[] arguments)
    {
        var startInfo = new ProcessStartInfo("stty") { UseShellExecute = false, RedirectStandardOutput = true };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        try
        {
            using var process = Process.Start(startInfo);

            if (process is null)
            {
                return null;
            }

            var text = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return text;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [SupportedOSPlatform("windows")]
    private static partial nint GetStdHandle(int handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool GetConsoleMode(nint handle, out uint mode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    [SupportedOSPlatform("windows")]
    private static partial bool SetConsoleMode(nint handle, uint mode);
}
