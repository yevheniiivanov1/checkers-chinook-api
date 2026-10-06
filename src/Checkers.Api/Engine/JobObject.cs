using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Checkers.Api.Engine;

/// <summary>
/// A Windows job object with "kill on close": every worker process is assigned to it, so when the
/// API process ends for any reason — an IIS app pool recycle, a crash, a debugger stop — Windows
/// terminates the workers with it instead of leaving orphaned engines holding memory.
/// </summary>
internal sealed partial class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private nint _handle;

    private JobObject(nint handle) => _handle = handle;

    /// <summary>Null on non-Windows systems or if the job cannot be created; workers then rely on graceful shutdown.</summary>
    public static JobObject? TryCreate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        var handle = CreateJobObjectW(0, null);
        if (handle == 0)
        {
            return null;
        }

        var info = new ExtendedLimitInformation { BasicLimitInformation = { LimitFlags = JobObjectLimitKillOnJobClose } };
        unsafe
        {
            if (!SetInformationJobObject(handle, JobObjectExtendedLimitInformation, &info, (uint)sizeof(ExtendedLimitInformation)))
            {
                CloseHandle(handle);
                return null;
            }
        }

        return new JobObject(handle);
    }

    public bool TryAssign(Process process) => _handle != 0 && AssignProcessToJobObject(_handle, process.Handle);

    public void Dispose()
    {
        if (_handle != 0)
        {
            CloseHandle(_handle);
            _handle = 0;
        }
    }

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

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateJobObjectW(nint attributes, string? name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SetInformationJobObject(nint job, int infoClass, void* info, uint length);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AssignProcessToJobObject(nint job, nint process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}
