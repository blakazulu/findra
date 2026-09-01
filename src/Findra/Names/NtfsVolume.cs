using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Findra;

// Raw access to one NTFS volume's master file table and change journal - the two things that make
// a name search instant and change tracking free.
//
// ENUMERATION is FSCTL_ENUM_USN_DATA, what "Everything" does: the driver hands back every file
// record on the volume (name, parent, attributes) in MFT order, no directory walk, about a second
// per million files. It needs admin, which the elevated `--names` helper has.
//
// CHANGE TRACKING is FSCTL_READ_USN_JOURNAL: NTFS already writes a record for every create, delete,
// rename and data change, whether or not anyone reads it. Reading from a saved cursor costs one
// DeviceIoControl that returns zero bytes when nothing happened, and the journal persists across
// sleep, reboots and the helper not running, so nothing is ever missed - the only failure is the journal
// WRAPPING (a burst of changes larger than its size), which the caller answers by re-enumerating.
//
// V2 records only (64-bit file reference numbers). NTFS returns V2 for a V0 enumeration request;
// ReFS would need V3 and is out of scope.
[SupportedOSPlatform("windows")]
public sealed class NtfsVolume : IDisposable
{
    public readonly record struct Record(ulong Frn, ulong ParentFrn, uint Attributes, string Name);

    /// <summary>One journal entry, already reduced to what the index needs.</summary>
    public readonly record struct Change(ulong Frn, ulong ParentFrn, uint Attributes, string Name,
        uint Reason, long Usn);

    public const uint FileAttributeDirectory = 0x10;

    // USN_REASON_* bits that matter here
    public const uint ReasonDataOverwrite = 0x00000001;
    public const uint ReasonDataExtend = 0x00000002;
    public const uint ReasonDataTruncation = 0x00000004;
    public const uint ReasonFileCreate = 0x00000100;
    public const uint ReasonFileDelete = 0x00000200;
    public const uint ReasonRenameOldName = 0x00001000;
    public const uint ReasonRenameNewName = 0x00002000;
    public const uint ReasonClose = 0x80000000;
    public const uint ReasonDataChanged = ReasonDataOverwrite | ReasonDataExtend | ReasonDataTruncation;

    public char Letter { get; }
    public ulong JournalId { get; private set; }
    public long NextUsn { get; private set; }

    private readonly SafeFileHandle _handle;
    private byte[] _buffer = new byte[1 << 20];   // 1 MB: ~10k records per call

    public NtfsVolume(char letter)
    {
        Letter = char.ToUpperInvariant(letter);
        // GENERIC_READ on the volume handle needs admin; FILE_SHARE_READ|WRITE so the volume stays
        // usable. No FILE_FLAG_BACKUP_SEMANTICS needed for the FSCTLs we issue.
        _handle = CreateFile($@"\\.\{Letter}:", 0x80000000 /*GENERIC_READ*/,
            0x1 | 0x2 /*FILE_SHARE_READ|WRITE*/, IntPtr.Zero, 3 /*OPEN_EXISTING*/, 0, IntPtr.Zero);
        if (_handle.IsInvalid)
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"open volume {Letter}:");
    }

    public void Dispose() => _handle.Dispose();

    /// <summary>Read the journal's identity and its current position. Creates the journal if the
    /// volume has none (a fresh install, or one deleted by a disk tool). Returns false when the
    /// volume cannot carry a journal at all.</summary>
    public bool QueryJournal()
    {
        var data = new UsnJournalDataV0();
        int size = Marshal.SizeOf<UsnJournalDataV0>();
        IntPtr p = Marshal.AllocHGlobal(size);
        try
        {
            if (!DeviceIoControl(_handle, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, p, (uint)size, out _, IntPtr.Zero))
            {
                int err = Marshal.GetLastWin32Error();
                if (err is ERROR_JOURNAL_NOT_ACTIVE or ERROR_JOURNAL_DELETE_IN_PROGRESS)
                {
                    // 64 MB rather than the 32 MB default: a large build or a game update writes
                    // more records than that in a minute, and every wrap costs a re-enumeration.
                    var create = new CreateUsnJournalData { MaximumSize = 64UL << 20, AllocationDelta = 8UL << 20 };
                    int cs = Marshal.SizeOf<CreateUsnJournalData>();
                    IntPtr cp = Marshal.AllocHGlobal(cs);
                    try
                    {
                        Marshal.StructureToPtr(create, cp, false);
                        if (!DeviceIoControl(_handle, FSCTL_CREATE_USN_JOURNAL, cp, (uint)cs, IntPtr.Zero, 0, out _, IntPtr.Zero))
                            throw new Win32Exception(Marshal.GetLastWin32Error(), $"create USN journal on {Letter}:");
                    }
                    finally { Marshal.FreeHGlobal(cp); }
                    if (!DeviceIoControl(_handle, FSCTL_QUERY_USN_JOURNAL, IntPtr.Zero, 0, p, (uint)size, out _, IntPtr.Zero))
                        throw new Win32Exception(Marshal.GetLastWin32Error(), $"query USN journal on {Letter}:");
                }
                else if (err == ERROR_INVALID_FUNCTION) return false;   // not NTFS
                else throw new Win32Exception(err, $"query USN journal on {Letter}:");
            }
            data = Marshal.PtrToStructure<UsnJournalDataV0>(p);
        }
        finally { Marshal.FreeHGlobal(p); }

        JournalId = data.UsnJournalID;
        NextUsn = data.NextUsn;
        return true;
    }

    /// <summary>Every file record on the volume. Call <see cref="QueryJournal"/> FIRST and keep its
    /// <see cref="NextUsn"/>: changes that land during the enumeration are then replayed from the
    /// journal rather than lost.</summary>
    public IEnumerable<Record> Enumerate()
    {
        var med = new MftEnumDataV0 { StartFileReferenceNumber = 0, LowUsn = 0, HighUsn = long.MaxValue };
        int inSize = Marshal.SizeOf<MftEnumDataV0>();
        IntPtr inPtr = Marshal.AllocHGlobal(inSize);
        GCHandle pin = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        try
        {
            while (true)
            {
                Marshal.StructureToPtr(med, inPtr, false);
                if (!DeviceIoControl(_handle, FSCTL_ENUM_USN_DATA, inPtr, (uint)inSize,
                        pin.AddrOfPinnedObject(), (uint)_buffer.Length, out uint got, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err == ERROR_HANDLE_EOF) yield break;
                    throw new Win32Exception(err, $"enumerate MFT on {Letter}:");
                }
                if (got < 8) yield break;
                med.StartFileReferenceNumber = BitConverter.ToUInt64(_buffer, 0);
                int pos = 8;
                while (pos + 60 <= got)
                {
                    int len = BitConverter.ToInt32(_buffer, pos);
                    if (len < 60 || pos + len > got) break;
                    yield return new Record(
                        BitConverter.ToUInt64(_buffer, pos + 8),
                        BitConverter.ToUInt64(_buffer, pos + 16),
                        BitConverter.ToUInt32(_buffer, pos + 52),
                        NameAt(pos));
                    pos += len;
                }
            }
        }
        finally
        {
            pin.Free();
            Marshal.FreeHGlobal(inPtr);
        }
    }

    /// <summary>Journal entries since <paramref name="fromUsn"/>. Advances <see cref="NextUsn"/>.
    /// Returns false when the journal has wrapped past the cursor or been recreated - the caller
    /// must re-enumerate; the index it holds is no longer trustworthy.</summary>
    public bool Read(long fromUsn, List<Change> into)
    {
        var req = new ReadUsnJournalDataV0
        {
            StartUsn = fromUsn, ReasonMask = 0xFFFFFFFF, ReturnOnlyOnClose = 0,
            Timeout = 0, BytesToWaitFor = 0, UsnJournalID = JournalId
        };
        int inSize = Marshal.SizeOf<ReadUsnJournalDataV0>();
        IntPtr inPtr = Marshal.AllocHGlobal(inSize);
        GCHandle pin = GCHandle.Alloc(_buffer, GCHandleType.Pinned);
        try
        {
            while (true)
            {
                Marshal.StructureToPtr(req, inPtr, false);
                if (!DeviceIoControl(_handle, FSCTL_READ_USN_JOURNAL, inPtr, (uint)inSize,
                        pin.AddrOfPinnedObject(), (uint)_buffer.Length, out uint got, IntPtr.Zero))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err is ERROR_JOURNAL_ENTRY_DELETED or ERROR_JOURNAL_NOT_ACTIVE
                        or ERROR_JOURNAL_DELETE_IN_PROGRESS or ERROR_INVALID_PARAMETER)
                        return false;
                    throw new Win32Exception(err, $"read USN journal on {Letter}:");
                }
                if (got < 8) return true;
                long next = BitConverter.ToInt64(_buffer, 0);
                int pos = 8;
                while (pos + 60 <= got)
                {
                    int len = BitConverter.ToInt32(_buffer, pos);
                    if (len < 60 || pos + len > got) break;
                    ushort major = BitConverter.ToUInt16(_buffer, pos + 4);
                    if (major == 2)
                        into.Add(new Change(
                            BitConverter.ToUInt64(_buffer, pos + 8),
                            BitConverter.ToUInt64(_buffer, pos + 16),
                            BitConverter.ToUInt32(_buffer, pos + 52),
                            NameAt(pos),
                            BitConverter.ToUInt32(_buffer, pos + 40),
                            BitConverter.ToInt64(_buffer, pos + 24)));
                    pos += len;
                }
                // The driver returns the next USN even for an empty batch; a batch that moved the
                // cursor nowhere is the end of the journal.
                if (next == req.StartUsn) { NextUsn = next; return true; }
                req.StartUsn = next;
                NextUsn = next;
                if (got <= 8) return true;
            }
        }
        finally
        {
            pin.Free();
            Marshal.FreeHGlobal(inPtr);
        }
    }

    private string NameAt(int recordPos)
    {
        int nameLen = BitConverter.ToUInt16(_buffer, recordPos + 56);
        int nameOff = BitConverter.ToUInt16(_buffer, recordPos + 58);
        return Encoding.Unicode.GetString(_buffer, recordPos + nameOff, nameLen);
    }

    /// <summary>The NTFS volumes with a drive letter, which is what a drive list can offer.</summary>
    public static List<(char Letter, string Label, long Bytes, bool Fixed)> Volumes()
    {
        var list = new List<(char, string, long, bool)>();
        foreach (var d in System.IO.DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || d.DriveType is System.IO.DriveType.CDRom or System.IO.DriveType.Network) continue;
                if (!d.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase)) continue;
                list.Add((d.Name[0], d.VolumeLabel, d.TotalSize, d.DriveType == System.IO.DriveType.Fixed));
            }
            catch { /* a drive that is not ready answers nothing */ }
        }
        return list;
    }

    // ---- Win32 -----------------------------------------------------------------------------------

    private const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
    private const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;
    private const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
    private const uint FSCTL_CREATE_USN_JOURNAL = 0x000900E7;

    private const int ERROR_INVALID_FUNCTION = 1;
    private const int ERROR_HANDLE_EOF = 38;
    private const int ERROR_INVALID_PARAMETER = 87;
    private const int ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;
    private const int ERROR_JOURNAL_NOT_ACTIVE = 1179;
    private const int ERROR_JOURNAL_ENTRY_DELETED = 1181;

    [StructLayout(LayoutKind.Sequential)]
    private struct MftEnumDataV0 { public ulong StartFileReferenceNumber; public long LowUsn; public long HighUsn; }

    [StructLayout(LayoutKind.Sequential)]
    private struct UsnJournalDataV0
    {
        public ulong UsnJournalID; public long FirstUsn; public long NextUsn; public long LowestValidUsn;
        public long MaxUsn; public ulong MaximumSize; public ulong AllocationDelta;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ReadUsnJournalDataV0
    {
        public long StartUsn; public uint ReasonMask; public uint ReturnOnlyOnClose; public ulong Timeout;
        public ulong BytesToWaitFor; public ulong UsnJournalID;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CreateUsnJournalData { public ulong MaximumSize; public ulong AllocationDelta; }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode, IntPtr lpInBuffer,
        uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize, out uint lpBytesReturned, IntPtr lpOverlapped);
}
