using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Anitools.Core.Platform;

/// <summary>
/// Windows Job Object с KILL_ON_JOB_CLOSE (docs/PLAN.md §2.8 #10): каждая запущенная программа входит в него, и если
/// anitools закроется или упадёт, Windows сама завершит ffmpeg вместе с его дочерними процессами (шим Chocolatey →
/// настоящий ffmpeg). Сам anitools в задание не входит — открытые им проводник и блокнот не закроются.
/// </summary>
public sealed partial class ChildProcessJob : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private static readonly Lazy<ChildProcessJob?> SharedJob = new(() => OperatingSystem.IsWindows() ? TryCreate() : null);

    private readonly SafeFileHandle _handle;

    private ChildProcessJob(SafeFileHandle handle) => _handle = handle;

    /// <summary>Общее задание на всё время работы приложения; не Windows или не создалось — null.</summary>
    public static ChildProcessJob? Shared => SharedJob.Value;

    public static ChildProcessJob? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = CreateJobObject(0, null);
        if (handle.IsInvalid)
        {
            handle.Dispose();
            return null;
        }

        var info = new ExtendedLimitInformation { BasicLimitInformation = new BasicLimitInformation { LimitFlags = JobObjectLimitKillOnJobClose } };
        if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            handle.Dispose();
            return null;
        }

        return new ChildProcessJob(handle);
    }

    /// <summary>Включить процесс (и всех, кого он запустит потом) в задание; false — не вышло.</summary>
    public bool Assign(Process process)
    {
        try
        {
            return OperatingSystem.IsWindows() && AssignProcessToJobObject(_handle, process.Handle);
        }
        catch (InvalidOperationException)
        {
            return false; // процесс уже завершился
        }
    }

    /// <summary>Закрыть задание — все процессы в нём будут завершены.</summary>
    public void Dispose() => _handle.Dispose();

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateJobObject(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimitInformation info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(SafeFileHandle job, nint process);

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
