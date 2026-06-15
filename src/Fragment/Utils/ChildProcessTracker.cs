using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Fragment.Utils;

/// <summary>
/// Assigns spawned child processes (ffmpeg) to a Windows Job Object configured to kill them when
/// this process's handle closes — i.e. when Fragment exits for ANY reason (normal close, crash,
/// unhandled exception, or even Task Manager "End task"). This guarantees no orphaned ffmpeg.exe is
/// left holding the GPU encoder / audio endpoint after the app is gone.
/// </summary>
public static class ChildProcessTracker
{
    private static readonly IntPtr s_jobHandle;

    static ChildProcessTracker()
    {
        try
        {
            s_jobHandle = CreateJobObject(IntPtr.Zero, null);
            if (s_jobHandle == IntPtr.Zero)
            {
                return;
            }

            var info = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            };
            var extended = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION { BasicLimitInformation = info };

            int length = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
            IntPtr extendedPtr = Marshal.AllocHGlobal(length);
            try
            {
                Marshal.StructureToPtr(extended, extendedPtr, false);
                SetInformationJobObject(s_jobHandle, JobObjectExtendedLimitInformation, extendedPtr, (uint)length);
            }
            finally
            {
                Marshal.FreeHGlobal(extendedPtr);
            }
        }
        catch
        {
            s_jobHandle = IntPtr.Zero; // best-effort; fall back to per-service Stop() cleanup
        }
    }

    /// <summary>Adds a process to the kill-on-close job. Safe to call even if the job is unavailable.</summary>
    public static void Track(Process process)
    {
        if (s_jobHandle == IntPtr.Zero || process is null)
        {
            return;
        }

        try
        {
            AssignProcessToJobObject(s_jobHandle, process.Handle);
        }
        catch
        {
            // Process may have already exited, or the OS denied assignment; ignore.
        }
    }

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;
    private const int JobObjectExtendedLimitInformation = 9;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll")]
    private static extern bool SetInformationJobObject(IntPtr hJob, int infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll")]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
