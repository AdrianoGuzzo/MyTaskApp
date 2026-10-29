using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using MyTaskApp.Application.Terminals;
using MyTaskApp.Domain;

namespace MyTaskApp.Infrastructure.Terminals.Linux;

/// <summary>Pseudoterminais do Linux (<c>openpty</c>) para o terminal do agente (ADR-098).</summary>
[SupportedOSPlatform("linux")]
internal sealed class UnixPtyTerminalFactory(ILogger<UnixPtyTerminal> logger) : IPseudoTerminalFactory
{
    public PseudoTerminalSupport Support { get; } = new(true, "Linux (openpty)", null);

    public IPseudoTerminal Start(PseudoTerminalStart start)
    {
        PseudoTerminalValidation.Validate(start);
        return UnixPtyTerminal.Start(start, logger);
    }
}

/// <summary>
/// Um processo num pseudoterminal do Linux. O filho nasce numa sessão nova
/// (<c>POSIX_SPAWN_SETSID</c>) e abre o lado escravo como entrada: é assim que
/// o TTY vira o terminal de controle dele, e o Ctrl+C, o redimensionar e o
/// "terminal fechou" chegam como chegariam numa janela de terminal.
/// </summary>
/// <remarks>
/// O grupo de processos é o da sessão (o PID do filho): o encerramento forçado
/// manda o sinal para o grupo, e só para ele (ADR-098).
/// </remarks>
[SupportedOSPlatform("linux")]
internal sealed partial class UnixPtyTerminal : IPseudoTerminal
{
    private const int PosixSpawnSetSigDef = 0x04;

    private const int PosixSpawnSetSigMask = 0x08;

    private const int PosixSpawnSetSid = 0x80;

    private const int OpenReadWrite = 0x02;

    private const int SigHangUp = 1;

    private const int SigKill = 9;

    private const int SigTerm = 15;

    private const ulong SetWindowSize = 0x5414;

    /// <summary>O <c>sigset_t</c> da glibc: 1024 bits.</summary>
    private const int SigSetSize = 128;

    /// <summary><c>posix_spawn_file_actions_t</c> e <c>posix_spawnattr_t</c> cabem com folga.</summary>
    private const int SpawnStructSize = 512;

    private readonly int _master;

    private readonly FileStream _input;

    private readonly PumpedOutput _output;

    private readonly SemaphoreSlim _writing = new(1, 1);

    private readonly TaskCompletionSource<int?> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private readonly ILogger _logger;

    private int _closed;

    private int _disposed;

    private UnixPtyTerminal(int master, int processId, TerminalSize size, ILogger logger)
    {
        _master = master;
        _logger = logger;
        ProcessId = processId;
        Size = size;
        ProcessStartedAt = StartTimeOf(processId);
        var handle = new SafeFileHandle(master, ownsHandle: true);
        _input = new FileStream(handle, FileAccess.ReadWrite, bufferSize: 0, isAsync: false);
        _output = new PumpedOutput(buffer => ReadMaster(buffer), $"pty-{processId}");
        var waiter = new Thread(WaitForExit) { IsBackground = true, Name = $"pty-wait-{processId}" };
        waiter.Start();
    }

    public int ProcessId { get; }

    public DateTimeOffset ProcessStartedAt { get; }

    public TerminalSize Size { get; private set; }

    public Task<int?> Exited => _exited.Task;

    public static unsafe UnixPtyTerminal Start(PseudoTerminalStart start, ILogger logger)
    {
        var size = new WindowSize { Rows = (ushort)start.Size.Rows, Columns = (ushort)start.Size.Columns };
        var name = stackalloc byte[4096];

        if (OpenPtyAnywhere(out var master, out var slave, name, ref size) != 0)
        {
            throw new DomainException($"Não foi possível abrir o pseudoterminal: {new Win32Exception(Marshal.GetLastPInvokeError()).Message}");
        }

        var slavePath = Marshal.PtrToStringUTF8((nint)name) ?? throw new DomainException("O pseudoterminal não informou o nome.");
        var actions = Marshal.AllocHGlobal(SpawnStructSize);
        var attributes = Marshal.AllocHGlobal(SpawnStructSize);
        var defaults = Marshal.AllocHGlobal(SigSetSize);
        var mask = Marshal.AllocHGlobal(SigSetSize);
        var argv = Strings([start.Executable, .. start.Arguments]);
        var envp = Strings(EnvironmentOf(start));

        try
        {
            Check(posix_spawn_file_actions_init(actions));
            Check(posix_spawnattr_init(attributes));

            // No filho: sem as pontas do pai, o escravo aberto como entrada (é o
            // que faz dele o terminal de controle da sessão nova) e copiado para
            // a saída e o erro.
            Check(posix_spawn_file_actions_addclose(actions, master));
            Check(posix_spawn_file_actions_addclose(actions, slave));
            Check(posix_spawn_file_actions_addopen(actions, 0, slavePath, OpenReadWrite, 0));
            Check(posix_spawn_file_actions_adddup2(actions, 0, 1));
            Check(posix_spawn_file_actions_adddup2(actions, 0, 2));
            Check(posix_spawn_file_actions_addchdir_np(actions, start.WorkingDirectory));

            // Os sinais que o .NET ignora (SIGPIPE) voltam ao padrão, e nada bloqueado.
            sigfillset(defaults);
            sigemptyset(mask);
            Check(posix_spawnattr_setsigdefault(attributes, defaults));
            Check(posix_spawnattr_setsigmask(attributes, mask));
            Check(posix_spawnattr_setflags(attributes, (short)(PosixSpawnSetSid | PosixSpawnSetSigDef | PosixSpawnSetSigMask)));

            var result = posix_spawn(out var pid, start.Executable, actions, attributes, argv, envp);

            if (result != 0)
            {
                close(master);
                throw new DomainException($"Não foi possível iniciar {Path.GetFileName(start.Executable)}: {new Win32Exception(result).Message}");
            }

            logger.LogInformation("PseudoTerminalStarted {ProcessId} {Size} {Executable}", pid, start.Size, Path.GetFileName(start.Executable));
            return new UnixPtyTerminal(master, pid, start.Size, logger);
        }
        catch (EntryPointNotFoundException)
        {
            close(master);
            throw new DomainException("Este Linux é antigo demais para o terminal do agente (pede a glibc 2.29 ou mais nova).");
        }
        finally
        {
            close(slave);
            posix_spawn_file_actions_destroy(actions);
            posix_spawnattr_destroy(attributes);
            Marshal.FreeHGlobal(actions);
            Marshal.FreeHGlobal(attributes);
            Marshal.FreeHGlobal(defaults);
            Marshal.FreeHGlobal(mask);
            Free(argv);
            Free(envp);
        }
    }

    public ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _output.ReadAsync(buffer, cancellationToken);

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken = default)
    {
        await _writing.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
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
        if (Volatile.Read(ref _closed) != 0 || size == Size)
        {
            return;
        }

        var window = new WindowSize { Rows = (ushort)size.Rows, Columns = (ushort)size.Columns };

        if (ioctl(_master, SetWindowSize, ref window) == 0)
        {
            Size = size;
        }
        else
        {
            _logger.LogWarning("PseudoTerminalResizeFailed {ProcessId} {Size} {Error}", ProcessId, size, Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>
    /// Como fechar a janela: o lado mestre fecha e a sessão recebe SIGHUP. Quem
    /// não sair por isso recebe SIGTERM, e depois SIGKILL.
    /// </summary>
    public void Close()
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0)
        {
            return;
        }

        kill(-ProcessId, SigHangUp);
        _ = EscalateAsync();
    }

    public void Kill()
    {
        if (_exited.Task.IsCompleted)
        {
            return;
        }

        kill(-ProcessId, SigKill);
        _logger.LogInformation("PseudoTerminalKilled {ProcessId}", ProcessId);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        Close();

        try
        {
            await _exited.Task.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            Kill();
        }

        await _input.DisposeAsync().ConfigureAwait(false);
        _writing.Dispose();
    }

    private async Task EscalateAsync()
    {
        foreach (var signal in (int[])[SigTerm, SigKill])
        {
            if (await Task.WhenAny(_exited.Task, Task.Delay(TimeSpan.FromSeconds(3))).ConfigureAwait(false) == _exited.Task)
            {
                return;
            }

            kill(-ProcessId, signal);
        }
    }

    /// <summary>Na glibc 2.34 em diante o <c>openpty</c> está na libc; antes, na libutil.</summary>
    private static unsafe int OpenPtyAnywhere(out int master, out int slave, byte* name, ref WindowSize size)
    {
        try
        {
            return OpenPty(out master, out slave, name, 0, ref size);
        }
        catch (EntryPointNotFoundException)
        {
            return OpenPtyFromLibUtil(out master, out slave, name, 0, ref size);
        }
    }

    private int ReadMaster(byte[] buffer)
    {
        var count = read(_master, buffer, buffer.Length);

        // EIO: o escravo fechou de vez (o processo e os netos saíram).
        return count <= 0 ? 0 : count;
    }

    private void WaitForExit()
    {
        int status;
        int result;

        do
        {
            result = waitpid(ProcessId, out status, 0);
        }
        while (result < 0 && Marshal.GetLastPInvokeError() == 4);

        int? exitCode = result == ProcessId
            ? (status & 0x7F) == 0 ? (status >> 8) & 0xFF : 128 + (status & 0x7F)
            : null;
        _logger.LogInformation("PseudoTerminalExited {ProcessId} {ExitCode}", ProcessId, exitCode);
        _exited.TrySetResult(exitCode);
    }

    private static DateTimeOffset StartTimeOf(int processId)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(processId);
            return new DateTimeOffset(process.StartTime);
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return DateTimeOffset.Now;
        }
    }

    private static IEnumerable<string> EnvironmentOf(PseudoTerminalStart start)
    {
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (System.Collections.DictionaryEntry entry in System.Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string name && entry.Value is string value)
            {
                variables[name] = value;
            }
        }

        foreach (var name in start.RemovedVariables ?? [])
        {
            variables.Remove(name);
        }

        // O que um emulador de terminal diria de si.
        variables["TERM"] = "xterm-256color";
        variables["COLORTERM"] = "truecolor";

        foreach (var (name, value) in start.Environment ?? new Dictionary<string, string>())
        {
            variables[name] = value;
        }

        return variables.Select(pair => $"{pair.Key}={pair.Value}");
    }

    private static nint Strings(IEnumerable<string> values)
    {
        var list = values.ToList();
        var array = Marshal.AllocHGlobal(nint.Size * (list.Count + 1));

        for (var i = 0; i < list.Count; i++)
        {
            var bytes = Encoding.UTF8.GetBytes(list[i] + '\0');
            var text = Marshal.AllocHGlobal(bytes.Length);
            Marshal.Copy(bytes, 0, text, bytes.Length);
            Marshal.WriteIntPtr(array, i * nint.Size, text);
        }

        Marshal.WriteIntPtr(array, list.Count * nint.Size, 0);
        return array;
    }

    private static void Free(nint array)
    {
        for (var i = 0; ; i++)
        {
            var text = Marshal.ReadIntPtr(array, i * nint.Size);

            if (text == 0)
            {
                break;
            }

            Marshal.FreeHGlobal(text);
        }

        Marshal.FreeHGlobal(array);
    }

    private static void Check(int result)
    {
        if (result != 0)
        {
            throw new DomainException($"Não foi possível preparar o pseudoterminal: {new Win32Exception(result).Message}");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowSize
    {
        public ushort Rows;
        public ushort Columns;
        public ushort PixelWidth;
        public ushort PixelHeight;
    }

    [LibraryImport("libc", EntryPoint = "openpty", SetLastError = true)]
    private static unsafe partial int OpenPty(out int master, out int slave, byte* name, nint termios, ref WindowSize size);

    [LibraryImport("libutil.so.1", EntryPoint = "openpty", SetLastError = true)]
    private static unsafe partial int OpenPtyFromLibUtil(out int master, out int slave, byte* name, nint termios, ref WindowSize size);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int ioctl(int fd, ulong request, ref WindowSize size);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int read(int fd, byte[] buffer, int count);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int close(int fd);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int kill(int pid, int signal);

    [LibraryImport("libc", SetLastError = true)]
    private static partial int waitpid(int pid, out int status, int options);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int posix_spawn(out int pid, string path, nint fileActions, nint attributes, nint argv, nint envp);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_init(nint actions);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_destroy(nint actions);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_addclose(nint actions, int fd);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int posix_spawn_file_actions_addopen(nint actions, int fd, string path, int flags, int mode);

    [LibraryImport("libc")]
    private static partial int posix_spawn_file_actions_adddup2(nint actions, int fd, int target);

    [LibraryImport("libc", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int posix_spawn_file_actions_addchdir_np(nint actions, string path);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_init(nint attributes);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_destroy(nint attributes);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_setflags(nint attributes, short flags);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_setsigdefault(nint attributes, nint signals);

    [LibraryImport("libc")]
    private static partial int posix_spawnattr_setsigmask(nint attributes, nint signals);

    [LibraryImport("libc")]
    private static partial int sigfillset(nint set);

    [LibraryImport("libc")]
    private static partial int sigemptyset(nint set);
}
