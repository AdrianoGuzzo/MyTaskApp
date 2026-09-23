using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using MyTaskApp.Application.Abstractions;

namespace MyTaskApp.Infrastructure.FileSystem;

/// <summary>
/// Quem está segurando algo dentro da pasta, no Windows (ADR-029). Duas
/// perguntas, porque cada uma enxerga o que a outra não enxerga:
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><b>Restart Manager</b>, sobre os arquivos que sobraram: acha o
/// executável rodando dali e a DLL carregada dali — imagem mapeada não é handle
/// aberto, e a varredura abaixo não a veria.</item>
/// <item><b>Tabela de handles do sistema</b>, filtrada pelos handles de arquivo
/// cujo caminho cai dentro da pasta: acha o terminal com a pasta de trabalho lá
/// dentro e a IDE com um arquivo aberto. O Restart Manager não aceita pasta,
/// só arquivo, e o caso mais comum — um terminal aberto no worktree — é pasta.</item>
/// </list>
/// <para>
/// Só enxerga processos do mesmo usuário e não elevados: é o que o Windows deixa
/// duplicar handle sem ser administrador. O caminho só é perguntado a handle de
/// disco (<c>GetFileType</c>), porque perguntar o nome de um pipe síncrono
/// ocupado trava; e a varredura inteira roda numa thread com prazo, por garantia.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
internal sealed partial class WindowsDirectoryLockFinder(ILogger<WindowsDirectoryLockFinder> logger) : IDirectoryLockFinder
{
    internal static readonly TimeSpan ScanTimeout = TimeSpan.FromSeconds(10);

    private const int MaxRestartManagerFiles = 1000;

    public IReadOnlyList<DirectoryLocker> Find(string path, CancellationToken cancellationToken = default)
    {
        var directory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var processIds = new HashSet<int>();

        try
        {
            processIds.UnionWith(FromRestartManager(directory));
        }
        catch (Exception exception) when (exception is Win32Exception or IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(exception, "RestartManagerFailed {Path}", directory);
        }

        cancellationToken.ThrowIfCancellationRequested();
        processIds.UnionWith(FromHandles(directory));

        return processIds
            .Select(Describe)
            .OfType<DirectoryLocker>()
            .OrderBy(locker => locker.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(locker => locker.ProcessId)
            .ToList();
    }

    private static DirectoryLocker? Describe(int processId)
    {
        string name;

        try
        {
            using var process = Process.GetProcessById(processId);
            name = process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
        {
            // Saiu entre a pergunta e agora: não segura mais nada.
            return null;
        }

        var canTerminate = processId != Environment.ProcessId
            && !string.Equals(name, "explorer", StringComparison.OrdinalIgnoreCase);

        return new DirectoryLocker(processId, name, ImagePath(processId), canTerminate);
    }

    // --- Restart Manager -----------------------------------------------------

    private static unsafe List<int> FromRestartManager(string directory)
    {
        var files = Directory
            .EnumerateFiles(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0,
            })
            .Take(MaxRestartManagerFiles)
            .ToArray();

        if (files.Length == 0)
        {
            return [];
        }

        var key = stackalloc char[RmSessionKeyLength + 1];
        Check(RmStartSession(out var session, 0, key));

        try
        {
            Check(RmRegisterResources(session, (uint)files.Length, files, 0, 0, 0, 0));

            var count = 0u;

            while (true)
            {
                var buffer = new RmProcessInfo[Math.Max(count, 1)];
                var capacity = (uint)buffer.Length;
                int status;

                fixed (RmProcessInfo* pointer = buffer)
                {
                    status = RmGetList(session, out var needed, ref capacity, pointer, out _);
                    count = needed;
                }

                if (status == ErrorMoreData)
                {
                    continue;
                }

                Check(status);

                return buffer.Take((int)capacity).Select(info => info.ProcessId).ToList();
            }
        }
        finally
        {
            _ = RmEndSession(session);
        }
    }

    private static void Check(int status)
    {
        if (status != 0)
        {
            throw new Win32Exception(status);
        }
    }

    // --- Tabela de handles ---------------------------------------------------

    private List<int> FromHandles(string directory)
    {
        var found = new List<int>();

        var scan = new Thread(() =>
        {
            try
            {
                ScanHandles(directory, found);
            }
            catch (Exception exception)
            {
                // Numa thread própria, uma exceção solta derrubaria o app inteiro.
                logger.LogWarning(exception, "HandleScanFailed {Path}", directory);
            }
        })
        {
            IsBackground = true,
            Name = "MyTaskApp directory lock scan",
        };

        scan.Start();

        if (!scan.Join(ScanTimeout))
        {
            logger.LogWarning("HandleScanTimedOut {Path}", directory);
        }

        lock (found)
        {
            return [.. found];
        }
    }

    private static unsafe void ScanHandles(string directory, List<int> found)
    {
        using var probe = CreateFileW(
            directory,
            FileReadAttributes,
            FileShareAll,
            0,
            OpenExisting,
            FileFlagBackupSemantics,
            0);

        if (probe.IsInvalid)
        {
            return;
        }

        var target = FinalPath(probe.DangerousGetHandle());

        if (target is null)
        {
            return;
        }

        var table = QueryHandleTable(out _);

        try
        {
            var count = (long)*(nint*)table;
            var entries = (HandleEntry*)(table + (2 * sizeof(nint)));
            var self = Environment.ProcessId;
            var probeValue = probe.DangerousGetHandle();

            // O índice do tipo "File" muda entre versões do Windows: o handle que
            // acabamos de abrir diz qual é.
            ushort? fileType = null;

            for (long i = 0; i < count; i++)
            {
                if (entries[i].UniqueProcessId == self && entries[i].HandleValue == probeValue)
                {
                    fileType = entries[i].ObjectTypeIndex;
                    break;
                }
            }

            if (fileType is null)
            {
                return;
            }

            var processes = new Dictionary<int, SafeProcessHandle?>();
            var seen = new HashSet<int>();

            try
            {
                for (long i = 0; i < count; i++)
                {
                    var entry = entries[i];
                    var processId = (int)entry.UniqueProcessId;

                    if (entry.ObjectTypeIndex != fileType
                        || processId <= 4
                        || seen.Contains(processId)
                        || (processId == self && entry.HandleValue == probeValue))
                    {
                        continue;
                    }

                    if (!processes.TryGetValue(processId, out var process))
                    {
                        process = OpenProcess(ProcessDupHandle, false, processId);

                        if (process.IsInvalid)
                        {
                            process.Dispose();
                            process = null;
                        }

                        processes[processId] = process;
                    }

                    if (process is null
                        || !DuplicateHandle(process, entry.HandleValue, CurrentProcess, out var duplicate, 0, false, DuplicateSameAccess))
                    {
                        continue;
                    }

                    try
                    {
                        if (GetFileType(duplicate) == FileTypeDisk && FinalPath(duplicate) is { } name && IsInside(name, target))
                        {
                            seen.Add(processId);

                            lock (found)
                            {
                                found.Add(processId);
                            }
                        }
                    }
                    finally
                    {
                        CloseHandle(duplicate);
                    }
                }
            }
            finally
            {
                foreach (var process in processes.Values)
                {
                    process?.Dispose();
                }
            }
        }
        finally
        {
            NativeMemory.Free((void*)table);
        }
    }

    private static bool IsInside(string candidate, string directory) =>
        candidate.Equals(directory, StringComparison.OrdinalIgnoreCase)
        || (candidate.StartsWith(directory, StringComparison.OrdinalIgnoreCase)
            && candidate.Length > directory.Length
            && candidate[directory.Length] == '\\');

    /// <summary>A tabela inteira, que cresce enquanto a lemos: pede de novo com folga.</summary>
    private static unsafe nint QueryHandleTable(out int length)
    {
        length = 4 * 1024 * 1024;

        while (true)
        {
            var buffer = (nint)NativeMemory.Alloc((nuint)length);
            var status = NtQuerySystemInformation(SystemExtendedHandleInformation, buffer, length, out var needed);

            if (status == 0)
            {
                return buffer;
            }

            NativeMemory.Free((void*)buffer);

            if (status != StatusInfoLengthMismatch || length >= 1024 * 1024 * 1024)
            {
                throw new Win32Exception($"NtQuerySystemInformation: 0x{status:X8}");
            }

            length = Math.Max(length * 2, needed + (1024 * 1024));
        }
    }

    private static unsafe string? FinalPath(nint handle)
    {
        var buffer = new char[1024];

        while (true)
        {
            uint length;

            fixed (char* pointer = buffer)
            {
                length = GetFinalPathNameByHandleW(handle, pointer, (uint)buffer.Length, 0);
            }

            if (length == 0)
            {
                return null;
            }

            if (length < buffer.Length)
            {
                return new string(buffer, 0, (int)length);
            }

            buffer = new char[length + 1];
        }
    }

    private static unsafe string? ImagePath(int processId)
    {
        using var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);

        if (process.IsInvalid)
        {
            return null;
        }

        var buffer = stackalloc char[1024];
        var length = 1024u;

        return QueryFullProcessImageNameW(process, 0, buffer, ref length) ? new string(buffer, 0, (int)length) : null;
    }

    // --- Win32 ---------------------------------------------------------------

    private const int RmSessionKeyLength = 32;

    private const int ErrorMoreData = 234;

    private const int SystemExtendedHandleInformation = 64;

    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    private const uint FileReadAttributes = 0x80;

    private const uint FileShareAll = 0x1 | 0x2 | 0x4;

    private const uint OpenExisting = 3;

    private const uint FileFlagBackupSemantics = 0x02000000;

    private const uint FileTypeDisk = 1;

    private const uint ProcessDupHandle = 0x0040;

    private const uint ProcessQueryLimitedInformation = 0x1000;

    private const uint DuplicateSameAccess = 0x2;

    private static readonly nint CurrentProcess = -1;

    [StructLayout(LayoutKind.Sequential)]
    private struct HandleEntry
    {
        public nint Object;
        public nint UniqueProcessId;
        public nint HandleValue;
        public uint GrantedAccess;
        public ushort CreatorBackTraceIndex;
        public ushort ObjectTypeIndex;
        public uint HandleAttributes;
        public uint Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct RmProcessInfo
    {
        public int ProcessId;
        public uint StartTimeLow;
        public uint StartTimeHigh;
        public fixed char AppName[256];
        public fixed char ServiceShortName[64];
        public int ApplicationType;
        public uint AppStatus;
        public uint SessionId;
        public int Restartable;
    }

    [LibraryImport("ntdll.dll")]
    private static partial int NtQuerySystemInformation(int informationClass, nint buffer, int length, out int returnLength);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        nint securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DuplicateHandle(
        SafeProcessHandle sourceProcess,
        nint sourceHandle,
        nint targetProcess,
        out nint targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetFileType(nint file);

    [LibraryImport("kernel32.dll")]
    private static unsafe partial uint GetFinalPathNameByHandleW(nint file, char* path, uint length, uint flags);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool QueryFullProcessImageNameW(
        SafeProcessHandle process,
        uint flags,
        char* exeName,
        ref uint size);

    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial int RmStartSession(out uint session, int flags, char* sessionKey);

    [LibraryImport("rstrtmgr.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int RmRegisterResources(
        uint session,
        uint fileCount,
        string[] files,
        uint applicationCount,
        nint applications,
        uint serviceCount,
        nint services);

    [LibraryImport("rstrtmgr.dll")]
    private static unsafe partial int RmGetList(
        uint session,
        out uint needed,
        ref uint count,
        RmProcessInfo* processes,
        out uint rebootReasons);

    [LibraryImport("rstrtmgr.dll")]
    private static partial int RmEndSession(uint session);
}
