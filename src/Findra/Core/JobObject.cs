using System;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Findra;

/// <summary>
/// A Windows job object with kill-on-close semantics: whatever is assigned to it is terminated by
/// the KERNEL when the last handle to the job closes.
///
/// <para>This is what makes the specification's sentence about the indexer true rather than
/// aspirational. Spec §3 says content indexing stops when the interface quits "by construction,
/// with no lifetime code to write" - and a child that polls its parent's process id is lifetime
/// code of the most fragile kind, because Windows reuses process ids. If the interface exits and
/// its id is reissued to something else before the child's next poll, <c>HasExited</c> is false
/// forever and the child outlives the parent it belongs to. A job object has no such failure mode:
/// every handle a process holds is closed by the kernel when it dies, however it dies, so a
/// force-kill and a crash discharge it exactly as an orderly exit does.</para>
///
/// <para>It is the PRIMARY mechanism, not the only one. The child keeps its parent poll, because
/// an environment that refuses the assignment has to leave something behind - and which mechanism
/// is in force is logged rather than guessed at.</para>
/// </summary>
public sealed class JobObject : IDisposable
{
    private nint _handle;

    private JobObject(nint handle) => _handle = handle;

    /// <summary>The job, or null if this machine would not give us one. Never throws: a missing
    /// job is a fallback, not a failure.</summary>
    public static JobObject? CreateKillOnClose()
    {
        nint h;
        try { h = CreateJobObjectW(0, null); }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
        if (h == 0) return null;

        var info = new ExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;

        int size = Marshal.SizeOf<ExtendedLimitInformation>();
        nint block = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, block, false);
            if (!SetInformationJobObject(h, JobObjectExtendedLimitInformation, block, (uint)size))
            {
                CloseHandle(h);
                return null;
            }
        }
        finally { Marshal.FreeHGlobal(block); }

        return new JobObject(h);
    }

    /// <summary>Put a process in the job. False means it stayed out of it, which is the caller's
    /// cue to say so and fall back.</summary>
    public bool Assign(Process process)
    {
        if (_handle == 0) return false;
        try { return AssignProcessToJobObject(_handle, process.Handle); }
        catch (InvalidOperationException) { return false; }   // the process already exited
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    public void Dispose()
    {
        nint h = _handle;
        _handle = 0;
        if (h != 0) CloseHandle(h);
    }

    // ---- the interop, and only what is used -------------------------------------------------

    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern nint CreateJobObjectW(nint attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(nint job, int infoClass, nint info, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(nint job, nint process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
