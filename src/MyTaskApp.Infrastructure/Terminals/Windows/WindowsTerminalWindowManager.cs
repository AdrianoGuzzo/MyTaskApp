using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using MyTaskApp.Application.Agents;

namespace MyTaskApp.Infrastructure.Terminals.Windows;

/// <summary>
/// Acha a janela de terminal de um processo pelo <b>PID</b> e a traz para a
/// frente (ADR-030). Nunca pelo título: vários Claude abertos têm todos o mesmo.
/// </summary>
/// <remarks>
/// <para>
/// Um programa de console não é dono da janela em que aparece — quem desenha é
/// o hospedeiro do console. O caminho confiável é perguntar ao próprio console:
/// o app se anexa por um instante ao console do processo
/// (<c>AttachConsole</c>), lê a janela dele (<c>GetConsoleWindow</c>) e se
/// solta (<c>FreeConsole</c>).
/// </para>
/// <para>
/// No console clássico, essa já é a janela visível. No Windows Terminal, é uma
/// pseudo-janela invisível cuja <b>dona</b> é a janela do Terminal — por isso o
/// <c>GetAncestor(GA_ROOTOWNER)</c>. Limitação conhecida: se o Terminal juntar
/// vários consoles como abas da mesma janela, a janela vem para a frente, mas a
/// aba certa pode não ser selecionada.
/// </para>
/// <para>
/// Se o console não responder, sobra a janela principal do processo
/// (<see cref="Process.MainWindowHandle"/>), para agentes que abram janela
/// própria.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsTerminalWindowManager(ILogger<WindowsTerminalWindowManager> logger)
    : ITerminalWindowManager
{
    private const uint GaRootOwner = 3;
    private const int SwRestore = 9;
    private const uint AttachParentProcess = unchecked((uint)-1);
    private const uint FlashwStop = 0;
    private const uint FlashwAll = 0x3;
    private const uint FlashwTimerNoForeground = 0xC;

    // O console é um só por processo: dois pedidos simultâneos se anexariam um
    // por cima do outro.
    private static readonly Lock ConsoleLock = new();

    public Task<bool> FocusAsync(int processId)
    {
        try
        {
            var target = VisibleWindowOf(processId);

            if (target == 0)
            {
                return Task.FromResult(false);
            }

            if (IsIconic(target))
            {
                ShowWindow(target, SwRestore);
            }

            if (SetForegroundWindow(target) || ForceForeground(target))
            {
                return Task.FromResult(true);
            }

            logger.LogWarning("TerminalFocusRefused {ProcessId}", processId);
            return Task.FromResult(false);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogWarning(exception, "TerminalFocusUnavailable {ProcessId}", processId);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Compara a janela do terminal com a que está em primeiro plano (ADR-036).
    /// Com vários consoles como abas da mesma janela do Windows Terminal, basta
    /// a janela estar na frente — a aba não se descobre (a mesma limitação do foco).
    /// </summary>
    public Task<bool> IsInForegroundAsync(int processId)
    {
        try
        {
            var target = VisibleWindowOf(processId);

            return Task.FromResult(target != 0 && target == GetForegroundWindow());
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogWarning(exception, "TerminalForegroundUnavailable {ProcessId}", processId);
            return Task.FromResult(false);
        }
    }

    /// <summary>
    /// Pisca botão e moldura até a janela vir para a frente (<c>FLASHW_TIMERNOFG</c>):
    /// quem para de piscar é o próprio Windows, quando o usuário chega ao
    /// terminal — o app não precisa vigiar o foco.
    /// </summary>
    public Task<bool> FlashAsync(int processId) =>
        Task.FromResult(Flash(processId, FlashwAll | FlashwTimerNoForeground));

    public Task StopFlashingAsync(int processId)
    {
        Flash(processId, FlashwStop);
        return Task.CompletedTask;
    }

    private bool Flash(int processId, uint flags)
    {
        try
        {
            var target = VisibleWindowOf(processId);

            if (target == 0)
            {
                return false;
            }

            var info = new FlashWindowInfo
            {
                Size = (uint)Marshal.SizeOf<FlashWindowInfo>(),
                Window = target,
                Flags = flags,
            };

            FlashWindowEx(in info);
            return true;
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            logger.LogWarning(exception, "TerminalFlashUnavailable {ProcessId}", processId);
            return false;
        }
    }

    /// <summary>
    /// A janela que o usuário vê: a do console (ou a principal do processo) e,
    /// no Windows Terminal, a dona dela. <c>0</c> quando não há.
    /// </summary>
    private static nint VisibleWindowOf(int processId)
    {
        var window = ConsoleWindowOf(processId);

        if (window == 0)
        {
            window = MainWindowOf(processId);
        }

        if (window == 0)
        {
            return 0;
        }

        var owner = GetAncestor(window, GaRootOwner);

        return owner == 0 ? window : owner;
    }

    /// <summary>
    /// O Windows só deixa trazer janela para a frente quem está na frente. O
    /// clique no botão normalmente garante isso; quando não (um menu roubou o
    /// foco, o app estava atrás), a saída conhecida é se juntar por um instante
    /// à fila de entrada da janela que está na frente.
    /// </summary>
    private static bool ForceForeground(nint target)
    {
        var foreground = GetForegroundWindow();
        var foregroundThread = foreground == 0 ? 0 : GetWindowThreadProcessId(foreground, out _);
        var currentThread = GetCurrentThreadId();

        if (foregroundThread == 0 || foregroundThread == currentThread)
        {
            return false;
        }

        if (!AttachThreadInput(currentThread, foregroundThread, true))
        {
            return false;
        }

        try
        {
            BringWindowToTop(target);
            return SetForegroundWindow(target);
        }
        finally
        {
            AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    private static nint ConsoleWindowOf(int processId)
    {
        lock (ConsoleLock)
        {
            // Um app gráfico não tem console. Se por acaso tiver (rodando de um
            // terminal em depuração), anexar a outro falharia — e soltar o
            // próprio seria pior. Nesse caso, nem tenta.
            if (GetConsoleWindow() != 0)
            {
                return 0;
            }

            if (processId <= 0 || (uint)processId == AttachParentProcess || !AttachConsole((uint)processId))
            {
                return 0;
            }

            try
            {
                return GetConsoleWindow();
            }
            finally
            {
                FreeConsole();
            }
        }
    }

    private static nint MainWindowOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.MainWindowHandle;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return 0;
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachConsole(uint processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint window, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(nint window);

    // O retorno diz se a janela estava ativa antes, não se deu certo: ignorado.
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(in FlashWindowInfo info);

    /// <summary><c>FLASHWINFO</c>. Contagem e intervalo zerados: o ritmo do cursor, sem fim.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct FlashWindowInfo
    {
        public uint Size;
        public nint Window;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }
}
