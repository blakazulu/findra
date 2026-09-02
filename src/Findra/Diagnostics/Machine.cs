using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace Findra.Diagnostics;

/// <summary>
/// The box a benchmark ran on. Every field is a string or a byte count that
/// <see cref="Bench.Fragment"/> prints beside the numbers, because a number without its machine
/// is marketing rather than measurement (spec §9).
///
/// <para><c>RamBytes</c> is 0 when the lookup failed, and the renderer prints "unknown" for it -
/// never "0.0 GB". A published zero is a claim about the machine; the absence of a reading is not.</para>
/// </summary>
public sealed record MachineInfo(string Cpu, string Architecture, long RamBytes, string Disk,
                                 string Windows, string Accelerator);

/// <summary>
/// Reads what a published benchmark has to name about the machine, WITHOUT elevation - this mode
/// runs from an ordinary terminal, and a lookup that quietly needs administrator would read
/// "unknown" on every machine and nobody would ever find out.
///
/// <para>Each of the five lookups is wrapped on its own. One failing costs exactly one field,
/// which becomes the literal <see cref="Unknown"/>, and the other four are still published.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public static class Machine
{
    /// <summary>What a field says when it could not be read. Never a zero and never blank: an
    /// empty cell in a published table reads as a machine with no disk.</summary>
    public const string Unknown = "unknown";

    /// <summary>
    /// This build loads no model, so there is no provider to name. The line states that as a fact
    /// about Findra rather than as a note to ourselves - the fragment is pasted onto a product
    /// page, where a forward reference to unwritten work means nothing to the reader and is wrong
    /// the day the work lands.
    /// </summary>
    public const string NoAccelerator = "CPU only - this build runs no models";

    public static MachineInfo Read() => new(
        Cpu: Try(Cpu),
        // Never assumed - printed. An arm64 run has to be self-identifying in its own numbers.
        Architecture: RuntimeInformation.ProcessArchitecture.ToString(),
        RamBytes: TotalPhysicalBytes(),
        // The medium under the index, which is the disk every store size and every extraction
        // number on this page was actually paid to.
        Disk: Try(() => DiskOf(Paths.Index)),
        Windows: Try(WindowsBuild),
        Accelerator: NoAccelerator);

    private static string Try(Func<string> f)
    {
        try
        {
            string s = f();
            return s.Length > 0 ? s : Unknown;
        }
        catch (Exception ex)
        {
            Log.Warn("bench", "a machine field could not be read: " + ex.Message);
            return Unknown;
        }
    }

    // ---- CPU -------------------------------------------------------------------------------

    /// <summary>The processor's own name string, straight out of the hardware hive. No WMI - a
    /// WMI query costs a service, a second of startup and a dependency, for a value the registry
    /// already holds and any user can read.</summary>
    private static string Cpu()
    {
        using RegistryKey? k = Registry.LocalMachine.OpenSubKey(
            @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        return (k?.GetValue("ProcessorNameString") as string ?? "").Trim();
    }

    // ---- RAM -------------------------------------------------------------------------------

    /// <summary>Installed physical memory. <c>GC.GetGCMemoryInfo</c> would describe THIS PROCESS's
    /// heap, which is not the machine, and <c>TotalAvailableMemoryBytes</c> is capped by a job
    /// object or a container limit when one is present. Returns 0 when the call fails, which the
    /// renderer prints as "unknown".</summary>
    private static long TotalPhysicalBytes()
    {
        try
        {
            var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref m) ? (long)m.ullTotalPhys : 0;
        }
        catch (Exception ex) { Log.Warn("bench", "installed memory could not be read: " + ex.Message); return 0; }
    }

    // ---- Windows ---------------------------------------------------------------------------

    /// <summary>
    /// The edition and the full four-part build.
    ///
    /// <para><c>Environment.OSVersion</c> is shimmed by the application manifest and reports the
    /// build the manifest declares compatibility with, not the one that is running -
    /// <c>RtlGetVersion</c> is the only call that does not lie. It stops at the build number, so
    /// the revision comes from the <c>UBR</c> value beside it; without that, two machines a year
    /// of patches apart publish the same build string.</para>
    ///
    /// <para><c>ProductName</c> still says "Windows 10" on Windows 11 - a compatibility decision
    /// Microsoft took and never revisited - so the major name is derived from the build number,
    /// which is the value that actually moved.</para>
    /// </summary>
    private static string WindowsBuild()
    {
        var v = new OsVersionInfo { dwOSVersionInfoSize = (uint)Marshal.SizeOf<OsVersionInfo>() };
        if (RtlGetVersion(ref v) != 0) return "";

        using RegistryKey? k = Registry.LocalMachine.OpenSubKey(
            @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        string product = (k?.GetValue("ProductName") as string ?? "").Trim();
        int ubr = k?.GetValue("UBR") is int u ? u : 0;

        // "Windows 10 Pro" on a build past 22000 is the registry's known lie; only the leading
        // product word is replaced, so the edition ("Pro", "Home", "Enterprise") is kept as read.
        if (v.dwBuildNumber >= 22000 && product.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
            product = "Windows 11" + product["Windows 10".Length..];
        if (product.Length == 0) product = "Windows";

        var c = CultureInfo.InvariantCulture;
        return $"{product} {v.dwMajorVersion.ToString(c)}.{v.dwMinorVersion.ToString(c)}." +
               $"{v.dwBuildNumber.ToString(c)}.{ubr.ToString(c)}";
    }

    // ---- Disk ------------------------------------------------------------------------------

    /// <summary>
    /// The medium and the bus under a path - "NVMe SSD", "SATA HDD", "USB SSD".
    /// <c>DriveInfo</c> knows the filesystem and the free space and nothing at all about what the
    /// bytes are stored on, and the two are not interchangeable in a benchmark: the same index on
    /// a spindle and on an NVMe device is two different products.
    ///
    /// <para>It is two steps, and both handles are opened with <b>zero desired access</b>. That is
    /// the whole trick that keeps this unelevated: a zero-access handle can carry an IOCTL and
    /// nothing else, and it opens for an ordinary user. <c>GENERIC_READ</c> on either handle needs
    /// administrator, and because the fallback here is a silent per-field "unknown", asking for it
    /// would print "unknown" on every machine and never be noticed.</para>
    /// </summary>
    private static string DiskOf(string path)
    {
        string root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path)) ?? "";
        if (root.Length < 2 || root[1] != ':') return "";
        int disk = PhysicalDriveNumber(char.ToUpperInvariant(root[0]));
        if (disk < 0) return "";

        (bool known, bool spins) = SeekPenalty(disk);
        string bus = BusName(BusType(disk));
        string medium = known ? (spins ? "HDD" : "SSD") : "";

        // Whatever came back is published; a half-answer ("NVMe", or "SSD" alone) is still a fact,
        // and it is not the same fact as "unknown".
        return (bus + " " + medium).Trim();
    }

    /// <summary>
    /// Which <c>\\.\PhysicalDriveN</c> a volume actually sits on. There is no other supported way
    /// to ask, and assuming <c>PhysicalDrive0</c> is wrong on every machine that boots from its
    /// second disk - which is most machines with a separate data drive added later.
    /// </summary>
    private static int PhysicalDriveNumber(char letter)
    {
        using SafeFileHandle h = Open($@"\\.\{letter}:");
        if (h.IsInvalid) return -1;

        // VOLUME_DISK_EXTENTS is a DWORD count followed by an array of DISK_EXTENT, and
        // DISK_EXTENT starts with a DWORD followed by two LARGE_INTEGERs - so the array is
        // 8-aligned and the first extent's DiskNumber sits at offset 8, not 4. Read by offset
        // rather than through a struct: a struct with an inline array of one is a fixed-size
        // lie about a variable-length reply.
        const int NumberOfDiskExtents = 0, FirstDiskNumber = 8;
        byte[] buf = new byte[1024];
        if (!Control(h, IoctlVolumeGetVolumeDiskExtents, null, buf, out _)) return -1;
        if (BitConverter.ToUInt32(buf, NumberOfDiskExtents) == 0) return -1;
        return (int)BitConverter.ToUInt32(buf, FirstDiskNumber);
    }

    /// <summary>Does the device incur a seek penalty - which is the storage stack's way of saying
    /// "it spins". <c>known</c> is false when the driver does not answer, and the caller then
    /// publishes the bus alone rather than guessing a medium.</summary>
    private static (bool Known, bool Spins) SeekPenalty(int disk)
    {
        const int StorageDeviceSeekPenaltyProperty = 7;
        const int IncursSeekPenalty = 8;   // DWORD Version, DWORD Size, BOOLEAN IncursSeekPenalty
        byte[]? d = QueryProperty(disk, StorageDeviceSeekPenaltyProperty, IncursSeekPenalty + 1);
        return d is null ? (false, false) : (true, d[IncursSeekPenalty] != 0);
    }

    /// <summary>The adapter's bus type: NVMe, SATA, USB and the rest. 0 is the storage stack's own
    /// "unknown", which is passed straight through rather than dressed up.</summary>
    private static int BusType(int disk)
    {
        const int StorageAdapterProperty = 1;
        // STORAGE_ADAPTER_DESCRIPTOR: five DWORDs (20 bytes), four BOOLEANs (4), then BusType.
        const int BusTypeOffset = 24;
        byte[]? d = QueryProperty(disk, StorageAdapterProperty, BusTypeOffset + 1);
        return d is null ? 0 : d[BusTypeOffset];
    }

    private static byte[]? QueryProperty(int disk, int propertyId, int minimumBytes)
    {
        using SafeFileHandle h = Open($@"\\.\PhysicalDrive{disk.ToString(CultureInfo.InvariantCulture)}");
        if (h.IsInvalid) return null;

        // STORAGE_PROPERTY_QUERY: PropertyId, QueryType (0 = PropertyStandardQuery), then a
        // variable tail this query does not use.
        byte[] query = new byte[12];
        BitConverter.TryWriteBytes(query.AsSpan(0), propertyId);
        BitConverter.TryWriteBytes(query.AsSpan(4), 0);

        byte[] buf = new byte[1024];
        if (!Control(h, IoctlStorageQueryProperty, query, buf, out uint returned)) return null;
        return returned >= minimumBytes ? buf : null;
    }

    private static string BusName(int bus) => bus switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "IEEE 1394", 5 => "SSA", 6 => "Fibre Channel",
        7 => "USB", 8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA", 12 => "SD", 13 => "MMC",
        14 => "virtual", 15 => "file-backed virtual", 16 => "Storage Spaces", 17 => "NVMe",
        18 => "SCM", 19 => "UFS",
        _ => "",
    };

    // ---- the calls -------------------------------------------------------------------------

    private const uint IoctlVolumeGetVolumeDiskExtents = 0x00560000;
    private const uint IoctlStorageQueryProperty = 0x002D1400;

    private static SafeFileHandle Open(string device) =>
        // dwDesiredAccess = 0. See DiskOf: this is what makes the whole lookup work for a user
        // who is not an administrator. FILE_SHARE_READ | FILE_SHARE_WRITE so the volume stays
        // usable by everything else while the handle is open.
        CreateFile(device, 0, 0x1 | 0x2, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);

    private static bool Control(SafeFileHandle h, uint code, byte[]? input, byte[] output, out uint returned)
    {
        GCHandle inPin = default, outPin = GCHandle.Alloc(output, GCHandleType.Pinned);
        try
        {
            IntPtr inPtr = IntPtr.Zero;
            uint inLen = 0;
            if (input is not null)
            {
                inPin = GCHandle.Alloc(input, GCHandleType.Pinned);
                inPtr = inPin.AddrOfPinnedObject();
                inLen = (uint)input.Length;
            }
            return DeviceIoControl(h, code, inPtr, inLen, outPin.AddrOfPinnedObject(),
                                   (uint)output.Length, out returned, IntPtr.Zero);
        }
        finally
        {
            if (inPin.IsAllocated) inPin.Free();
            outPin.Free();
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
                     ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OsVersionInfo
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion, dwMinorVersion, dwBuildNumber, dwPlatformId;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szCSDVersion;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx lpBuffer);

    [DllImport("ntdll.dll")]
    private static extern int RtlGetVersion(ref OsVersionInfo lpVersionInformation);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess,
        uint dwShareMode, IntPtr lpSecurityAttributes, uint dwCreationDisposition,
        uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);
}
