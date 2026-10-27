using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace MyTaskApp.Infrastructure.Processes;

/// <summary>
/// Um Job Object do Windows com <c>KILL_ON_JOB_CLOSE</c> (seção 4.4 da
/// orquestração remota): quando o último handle fecha — no <see cref="Dispose"/>,
/// ou porque o processo que o tinha morreu —, o Windows mata tudo o que está
/// nele. Quem nasce de um processo do job entra nele: o neto vai junto.
/// </summary>
/// <remarks>
/// O handle não é herdável: um filho com os pipes redirecionados herda os
/// handles herdáveis, e um que herdasse este manteria o job vivo depois da
/// queda de quem o criou.
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class KillOnCloseJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;

    private const uint KillOnJobClose = 0x2000;

    /// <summary>O job do próprio agente: fica aberto até o processo acabar, e é isso que mata os filhos junto.</summary>
    private static KillOnCloseJob? _thisProcess;

    private nint _handle;

    private KillOnCloseJob(nint handle) => _handle = handle;

    public static KillOnCloseJob Create()
    {
        var handle = CreateJobObjectW(0, null);

        if (handle == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }

        var job = new KillOnCloseJob(handle);
        var limits = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = KillOnJobClose } };

        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref limits, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }

        return job;
    }

    /// <summary>
    /// Põe este processo num job que morre com ele: o que ele abrir depois
    /// (o comando de longe, o Claude, o neto que o comando abriu) não sobrevive
    /// à queda do agente. Fora do Windows não faz nada: no Linux, o systemd
    /// encerra o grupo inteiro do serviço.
    /// </summary>
    /// <returns>Se o job está valendo.</returns>
    public static bool KillChildrenWithThisProcess()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        if (_thisProcess is not null)
        {
            return true;
        }

        var job = Create();
        using var current = Process.GetCurrentProcess();
        job.Assign(current);
        _thisProcess = job;
        return true;
    }

    public void Assign(Process process)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);

        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
    }

    /// <summary>Se o processo está neste job (entrou, ou nasceu de quem estava).</summary>
    public bool Contains(Process process)
    {
        ObjectDisposedException.ThrowIf(_handle == 0, this);

        return IsProcessInJob(process.Handle, _handle, out var inJob) && inJob;
    }

    /// <summary>Fecha o handle: o que estiver no job morre.</summary>
    public void Dispose()
    {
        var handle = Interlocked.Exchange(ref _handle, 0);

        if (handle != 0)
        {
            CloseHandle(handle);
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(nint job, int informationClass, ref ExtendedLimitInformation information, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsProcessInJob(nint process, nint job, [MarshalAs(UnmanagedType.Bool)] out bool result);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }
}
