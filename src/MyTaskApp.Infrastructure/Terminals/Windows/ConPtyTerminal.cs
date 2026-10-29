using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;
using MyTaskApp.Infrastructure.Processes;

namespace MyTaskApp.Infrastructure.Terminals.Windows;

/// <summary>
/// Pseudoterminais do Windows (ConPTY, Windows 10 1809 em diante) para o
/// terminal do agente (ADR-098).
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class ConPtyTerminalFactory(ILogger<ConPtyTerminal> logger) : IPseudoTerminalFactory
{
    public PseudoTerminalSupport Support { get; } = ConPtyTerminal.DetectSupport();

    public IPseudoTerminal Start(PseudoTerminalStart start)
    {
        if (!Support.IsSupported)
        {
            throw new DomainException(Support.Reason ?? "Este Windows não tem pseudoterminal (ConPTY).");
        }

        PseudoTerminalValidation.Validate(start);
        return ConPtyTerminal.Start(start, logger);
    }
}

/// <summary>
/// Um processo num pseudoconsole do Windows. O programa vê um console de
/// verdade (o <c>conhost</c> do ConPTY por trás) e o app lê e escreve os bytes
/// VT que uma janela de terminal leria e escreveria.
/// </summary>
/// <remarks>
/// <para>
/// O processo nasce <b>suspenso</b>, entra num job só dele com
/// <c>KILL_ON_JOB_CLOSE</c> e só então começa: nenhum neto escapa do
/// encerramento forçado, e o encerramento forçado não alcança nada de fora
/// (ADR-098). O job aninha no do próprio agente, que mata tudo se o agente cair.
/// </para>
/// <para>
/// O <c>STARTF_USESTDHANDLES</c> com handles vazios é de propósito: sem ele, um
/// filho de um processo com a saída redirecionada (o agente, um teste) herdaria
/// esses handles em vez do pseudoconsole.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class ConPtyTerminal : IPseudoTerminal
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;

    private const uint CreateUnicodeEnvironment = 0x00000400;

    private const uint CreateSuspended = 0x00000004;

    private const int StartfUseStdHandles = 0x00000100;

    private const nint ProcThreadAttributePseudoConsole = 0x00020016;

    private readonly KillOnCloseJob _job;

    private readonly nint _process;

    private readonly FileStream _input;

    private readonly PumpedOutput _output;

    private readonly SemaphoreSlim _writing = new(1, 1);

    private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly RegisteredWaitHandle _exitWait;

    private readonly ILogger _logger;

    private nint _console;

    private int _disposed;

    private ConPtyTerminal(
        nint console,
        nint process,
        int processId,
        KillOnCloseJob job,
        SafeFileHandle input,
        SafeFileHandle output,
        TerminalSize size,
        ILogger logger)
    {
        _console = console;
        _process = process;
        _job = job;
        _logger = logger;
        ProcessId = processId;
        Size = size;
        ProcessStartedAt = StartTimeOf(processId);
        _input = new FileStream(input, FileAccess.Write, bufferSize: 0, isAsync: false);
        var reader = new FileStream(output, FileAccess.Read, bufferSize: 0, isAsync: false);
        _output = new PumpedOutput(buffer => reader.Read(buffer), $"conpty-{processId}");
        _exitWait = ThreadPool.RegisterWaitForSingleObject(
            new ProcessWaitHandle(process), (_, _) => OnExited(), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public int ProcessId { get; }

    public DateTimeOffset ProcessStartedAt { get; }

    public TerminalSize Size { get; private set; }

    public Task<int?> Exited => _exited.Task;

    public static PseudoTerminalSupport DetectSupport()
    {
        // O ConPTY chegou no Windows 10 1809 (build 17763).
        return OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763)
            ? new PseudoTerminalSupport(true, "Windows (ConPTY)", null)
            : PseudoTerminalSupport.Unsupported(
                "Windows",
                "O terminal do agente pede o Windows 10 versão 1809 ou mais novo (ConPTY).");
    }

    public static ConPtyTerminal Start(PseudoTerminalStart start, ILogger logger)
    {
        CreatePipeOrThrow(out var inputRead, out var inputWrite);
        CreatePipeOrThrow(out var outputRead, out var outputWrite);
        var console = nint.Zero;
        var attributes = nint.Zero;
        var commandLine = nint.Zero;
        var environment = nint.Zero;
        KillOnCloseJob? job = null;
        var started = false;

        try
        {
            var result = CreatePseudoConsole(Coord.Of(start.Size), inputRead, outputWrite, 0, out console);

            if (result != 0)
            {
                throw new DomainException($"O Windows não abriu o pseudoterminal (HRESULT 0x{result:X8}).");
            }

            // O pseudoconsole duplicou as pontas dele; as nossas cópias saem já.
            inputRead.Dispose();
            outputWrite.Dispose();

            attributes = AttributeListWith(console);
            commandLine = Marshal.StringToHGlobalUni(WindowsCommandLine.Build(start.Executable, start.Arguments));
            environment = Marshal.StringToHGlobalUni(WindowsCommandLine.EnvironmentBlock(start.Environment, start.RemovedVariables));

            var startup = new StartupInfoEx
            {
                StartupInfo = new StartupInfo
                {
                    cb = Marshal.SizeOf<StartupInfoEx>(),
                    dwFlags = StartfUseStdHandles,
                },
                lpAttributeList = attributes,
            };

            if (!CreateProcessW(
                    null,
                    commandLine,
                    0,
                    0,
                    false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment | CreateSuspended,
                    environment,
                    start.WorkingDirectory,
                    ref startup,
                    out var information))
            {
                var error = new Win32Exception(Marshal.GetLastPInvokeError());
                logger.LogWarning(error, "PseudoTerminalStartFailed {Executable}", start.Executable);
                throw new DomainException($"Não foi possível iniciar {Path.GetFileName(start.Executable)}: {error.Message}");
            }

            try
            {
                job = KillOnCloseJob.Create();
                job.Assign(information.hProcess);
            }
            catch (Win32Exception exception)
            {
                // Sem o job, o encerramento forçado não alcançaria os netos:
                // melhor não começar.
                TerminateProcess(information.hProcess, 1);
                CloseHandle(information.hThread);
                CloseHandle(information.hProcess);
                job?.Dispose();
                throw new DomainException($"Não foi possível isolar o processo do terminal: {exception.Message}");
            }

            ResumeThread(information.hThread);
            CloseHandle(information.hThread);

            var terminal = new ConPtyTerminal(
                console,
                information.hProcess,
                information.dwProcessId,
                job,
                inputWrite,
                outputRead,
                start.Size,
                logger);
            started = true;
            logger.LogInformation(
                "PseudoTerminalStarted {ProcessId} {Size} {Executable}",
                information.dwProcessId,
                start.Size,
                Path.GetFileName(start.Executable));
            return terminal;
        }
        finally
        {
            if (attributes != 0)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            if (commandLine != 0)
            {
                Marshal.FreeHGlobal(commandLine);
            }

            if (environment != 0)
            {
                Marshal.FreeHGlobal(environment);
            }

            if (!started)
            {
                if (console != 0)
                {
                    ClosePseudoConsole(console);
                }

                inputRead.Dispose();
                inputWrite.Dispose();
                outputRead.Dispose();
                outputWrite.Dispose();
            }
        }
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _output.ReadAsync(buffer, cancellationToken);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // A escrita no pipe é bloqueante: fora da thread de quem chamou.
            await Task.Run(() => _input.Write(data.Span), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or ObjectDisposedException)
        {
            throw new DomainException("O terminal já fechou: a entrada não foi entregue.");
        }
        finally
        {
            _writing.Release();
        }
    }

    public void Resize(TerminalSize size)
    {
        var console = _console;

        if (console == 0 || size == Size)
        {
            return;
        }

        var result = ResizePseudoConsole(console, Coord.Of(size));

        if (result == 0)
        {
            Size = size;
        }
        else
        {
            _logger.LogWarning("PseudoTerminalResizeFailed {ProcessId} {Size} {Result}", ProcessId, size, result);
        }
    }

    public void Close()
    {
        var console = Interlocked.Exchange(ref _console, 0);

        if (console == 0)
        {
            return;
        }

        // Fechar o pseudoconsole avisa o programa (CTRL_CLOSE_EVENT) e pode
        // esperar a saída escoar: em outra thread, com a leitura correndo.
        _ = Task.Run(() => ClosePseudoConsole(console));
    }

    public void Kill()
    {
        if (Volatile.Read(ref _disposed) != 0 || _exited.Task.IsCompleted)
        {
            return;
        }

        try
        {
            _job.Terminate(1);
            _logger.LogInformation("PseudoTerminalKilled {ProcessId}", ProcessId);
        }
        catch (Win32Exception exception)
        {
            _logger.LogWarning(exception, "PseudoTerminalKillFailed {ProcessId}", ProcessId);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Close();

        // O job fechado mata o que sobrou: soltar a sessão não deixa órfão.
        _job.Dispose();

        try
        {
            await _exited.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("PseudoTerminalExitTimeout {ProcessId}", ProcessId);
        }

        _exitWait.Unregister(null);
        await _input.DisposeAsync().ConfigureAwait(false);
        _writing.Dispose();
        CloseHandle(_process);
    }

    private void OnExited()
    {
        int? exitCode = GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : null;
        _logger.LogInformation("PseudoTerminalExited {ProcessId} {ExitCode}", ProcessId, exitCode);
        _exited.TrySetResult(exitCode);

        // A saída do pipe só termina quando o pseudoconsole fecha: com o
        // processo morto, fechar é o que entrega o fim a quem lê.
        Close();
    }

    private static DateTimeOffset StartTimeOf(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return new DateTimeOffset(process.StartTime);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return DateTimeOffset.Now;
        }
    }

    private static void CreatePipeOrThrow(out SafeFileHandle read, out SafeFileHandle write)
    {
        if (!CreatePipe(out read, out write, 0, 0))
        {
            throw new DomainException($"Não foi possível abrir o pseudoterminal: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }
    }

    private static nint AttributeListWith(nint console)
    {
        nint size = 0;
        InitializeProcThreadAttributeList(0, 1, 0, ref size);
        var list = Marshal.AllocHGlobal(size);

        if (!InitializeProcThreadAttributeList(list, 1, 0, ref size))
        {
            var error = Marshal.GetLastPInvokeError();
            Marshal.FreeHGlobal(list);
            throw new DomainException($"Não foi possível preparar o pseudoterminal: {new Win32Exception(error).Message}");
        }

        if (!UpdateProcThreadAttribute(list, 0, ProcThreadAttributePseudoConsole, console, nint.Size, 0, 0))
        {
            var error = Marshal.GetLastPInvokeError();
            DeleteProcThreadAttributeList(list);
            Marshal.FreeHGlobal(list);
            throw new DomainException($"Não foi possível preparar o pseudoterminal: {new Win32Exception(error).Message}");
        }

        return list;
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        public ProcessWaitHandle(nint process) => SafeWaitHandle = new SafeWaitHandle(process, ownsHandle: false);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Coord
    {
        public short X;
        public short Y;

        public static Coord Of(TerminalSize size) => new() { X = (short)size.Columns, Y = (short)size.Rows };
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [LibraryImport("kernel32.dll")]
    private static partial int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);

    [LibraryImport("kernel32.dll")]
    private static partial int ResizePseudoConsole(nint console, Coord size);

    [LibraryImport("kernel32.dll")]
    private static partial void ClosePseudoConsole(nint console);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [LibraryImport("kernel32.dll")]
    private static partial void DeleteProcThreadAttributeList(nint list);

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CreateProcessW(
        string? applicationName,
        nint commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        nint environment,
        string? currentDirectory,
        ref StartupInfoEx startupInfo,
        out ProcessInformation processInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint ResumeThread(nint thread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(nint process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetExitCodeProcess(nint process, out uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
