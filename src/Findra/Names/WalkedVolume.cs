using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace Findra;

/// <summary>
/// Names on a drive whose file table Findra does not read directly: a USB stick, a memory card, a
/// disk formatted exFAT or FAT32, a ReFS Dev Drive. Those were simply absent - a stick plugged in
/// and searched for found nothing on it.
///
/// <para>Such a drive is walked folder by folder, which costs about what listing it in Explorer
/// costs, and kept current by a watcher. Every record gets a number made from its path relative to
/// the drive's root, and the root gets NTFS's own number (5), so the index lays paths out, works
/// out depth and hides root system names exactly as it does for an NTFS volume.</para>
///
/// <para>Names only. Nothing on these drives is read inside: a drive that comes and goes would
/// queue and drop the same files every time it was plugged in, and the content index keys files
/// by numbers only NTFS keeps stable.</para>
/// </summary>
public static class WalkedVolume
{
    /// <summary>The root's number, the one NTFS gives its root directory.</summary>
    public const ulong RootId = 5;

    private const ulong Low48 = 0xFFFFFFFFFFFF;

    /// <summary>One entry found by a walk, in the shape the index takes.</summary>
    public readonly record struct Entry(ulong Id, ulong Parent, uint Attributes, string Name);

    /// <summary>
    /// Whether a drive is walked: removable media whatever its format, and a fixed disk in any
    /// format but NTFS. Network drives and optical discs are not, and a fixed NTFS disk has its
    /// file table read instead.
    /// </summary>
    public static bool Walks(DriveType type, string format) => type switch
    {
        DriveType.Removable or DriveType.Ram => true,
        DriveType.Fixed => !string.Equals(format, "NTFS", StringComparison.OrdinalIgnoreCase),
        _ => false,
    };

    /// <summary>
    /// The number for a path relative to the drive's root ("" is the root). Case-insensitive,
    /// because every format this walks is; stable across restarts, because it is the path. Never
    /// the root's number for anything else, never a number the index reserves (0, all ones), and
    /// never one whose low 48 bits read as the root, which is how NTFS numbers are compared.
    /// </summary>
    public static ulong IdOf(string relative)
    {
        ArgumentNullException.ThrowIfNull(relative);
        string r = relative.Trim('\\');
        if (r.Length == 0) return RootId;

        // FNV-1a over the upper-cased UTF-16. Two paths colliding among a million is about one
        // chance in forty million, and the cost of one is a name shown under another folder.
        ulong h = 14695981039346656037UL;
        foreach (char c in r.ToUpperInvariant())
        {
            h ^= c;
            h *= 1099511628211UL;
        }
        if ((h & Low48) is RootId or 0 || h == ulong.MaxValue) h ^= 0x2;
        return h;
    }

    /// <summary>The path relative to <paramref name="root"/>, or null when it is not under it.</summary>
    public static string? Relative(string root, string path)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(path);
        string r = root.TrimEnd('\\') + "\\";
        if (!path.StartsWith(r, StringComparison.OrdinalIgnoreCase))
            return string.Equals(path.TrimEnd('\\'), root.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? "" : null;
        return path[r.Length..].TrimEnd('\\');
    }

    /// <summary>The folder a relative path sits in, "" for the root.</summary>
    public static string ParentOf(string relative)
    {
        int cut = relative.TrimEnd('\\').LastIndexOf('\\');
        return cut < 0 ? "" : relative[..cut];
    }

    private static readonly EnumerationOptions OneFolder = new()
    {
        RecurseSubdirectories = false,
        IgnoreInaccessible = true,
        // Hidden and system entries too: the NTFS side lists every record, and a hidden folder a
        // person is looking for is the one they cannot find in Explorer.
        AttributesToSkip = 0,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Every entry under <paramref name="root"/>, one folder at a time. A folder that cannot be
    /// opened is passed over; a linked folder is listed but not entered, so a link back up the tree
    /// cannot loop and a link to another drive does not list that drive under this one.
    /// </summary>
    public static IEnumerable<Entry> Walk(string root, string from = "", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        var pending = new Stack<string>();
        pending.Push(from.Trim('\\'));
        var folder = new List<(Entry Entry, string Relative, bool Enter)>();
        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            string rel = pending.Pop();
            ulong parent = IdOf(rel);
            folder.Clear();
            try
            {
                foreach (FileSystemInfo fi in new DirectoryInfo(Path.Combine(root, rel)).EnumerateFileSystemInfos("*", OneFolder))
                {
                    string child = rel.Length == 0 ? fi.Name : rel + "\\" + fi.Name;
                    FileAttributes a = fi.Attributes;
                    bool enter = (a & FileAttributes.Directory) != 0 && (a & FileAttributes.ReparsePoint) == 0;
                    folder.Add((new Entry(IdOf(child), parent, (uint)a, fi.Name), child, enter));
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A folder that vanished or refuses listing mid-walk contributes what it listed.
            }

            foreach ((Entry e, string child, bool enter) in folder)
            {
                yield return e;
                if (enter) pending.Push(child);
            }
        }
    }

    /// <summary>
    /// Make <paramref name="ix"/> hold exactly what a walk found: every entry written, every record
    /// the walk did not see removed. Written in batches, each under its own hold of the write lock,
    /// so a search is never held up for the length of a whole walk.
    /// </summary>
    public static (int Written, int Removed) Reconcile(NameIndex ix, IEnumerable<Entry> walked, Func<IDisposable> write,
                                                       int batch = 4096)
    {
        ArgumentNullException.ThrowIfNull(ix);
        ArgumentNullException.ThrowIfNull(walked);
        ArgumentNullException.ThrowIfNull(write);

        var seen = new HashSet<ulong>();
        var pending = new List<Entry>(batch);
        int written = 0;

        void Flush()
        {
            if (pending.Count == 0) return;
            using (write())
                foreach (Entry e in pending) ix.Upsert(e.Id, e.Parent, e.Attributes, e.Name);
            written += pending.Count;
            pending.Clear();
        }

        foreach (Entry e in walked)
        {
            seen.Add(e.Id);
            pending.Add(e);
            if (pending.Count == batch) Flush();
        }
        Flush();

        int removed = 0;
        using (write())
        {
            var gone = new List<ulong>();
            for (int rec = 0; rec < ix.Capacity; rec++)
                if (ix.IsAlive(rec) && !seen.Contains(ix.Frn(rec))) gone.Add(ix.Frn(rec));
            foreach (ulong id in gone)
                if (ix.Remove(id)) removed++;
        }
        return (written, removed);
    }

    /// <summary>What the watcher saw, reduced to what the index needs.</summary>
    public enum ChangeKind { Created, Deleted, Renamed }

    /// <summary>
    /// Apply one watcher notification. A file is written or removed on the spot. Anything involving
    /// a folder answers true instead, meaning walk the drive again: a folder moved in arrives with
    /// everything inside it, and a folder removed or renamed changes the number of everything under
    /// it, which one notification does not list.
    /// </summary>
    public static bool Apply(NameIndex ix, ChangeKind kind, string relative, string? oldRelative,
                             Func<string, FileAttributes?> attributesOf, Func<IDisposable> write)
    {
        ArgumentNullException.ThrowIfNull(ix);
        ArgumentNullException.ThrowIfNull(relative);
        ArgumentNullException.ThrowIfNull(attributesOf);
        ArgumentNullException.ThrowIfNull(write);

        bool WasFolder(string rel)
        {
            using (write())
                return ix.TryIndexOf(IdOf(rel), out int rec) && ix.IsDirectory(rec);
        }

        switch (kind)
        {
            case ChangeKind.Deleted:
                if (WasFolder(relative)) return true;
                using (write()) ix.Remove(IdOf(relative));
                return false;

            case ChangeKind.Created:
            case ChangeKind.Renamed:
            {
                if (kind == ChangeKind.Renamed && oldRelative is not null && WasFolder(oldRelative)) return true;
                FileAttributes? a = attributesOf(relative);
                if (a is { } attr && (attr & FileAttributes.Directory) != 0) return true;
                using (write())
                {
                    if (kind == ChangeKind.Renamed && oldRelative is not null) ix.Remove(IdOf(oldRelative));
                    // Gone again before it could be looked at: nothing to add.
                    if (a is { } found)
                        ix.Upsert(IdOf(relative), IdOf(ParentOf(relative)), (uint)found, Path.GetFileName(relative));
                }
                return false;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, "no rule for this change");
        }
    }
}

/// <summary>
/// The walked drives, kept in the helper's volume table for as long as they are plugged in. A drive
/// that appears is walked and added; one that goes is taken out; one whose letter now belongs to a
/// different stick is walked afresh. Each is followed by a <see cref="FileSystemWatcher"/>.
/// </summary>
public sealed class WalkedDrives : IDisposable
{
    /// <summary>How often the drive list is looked at for sticks plugged in or pulled out.</summary>
    public static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(5);

    /// <summary>How long after a folder changes the drive is walked again, so that copying a tree
    /// onto it is one walk rather than one per folder.</summary>
    public static readonly TimeSpan SettleFor = TimeSpan.FromSeconds(2);

    private readonly ConcurrentDictionary<char, VolumeView> _views;
    private readonly IndexLock _gate;
    private readonly Dictionary<char, Drive> _held = [];

    public WalkedDrives(ConcurrentDictionary<char, VolumeView> views, IndexLock gate)
    {
        _views = views ?? throw new ArgumentNullException(nameof(views));
        _gate = gate ?? throw new ArgumentNullException(nameof(gate));
    }

    /// <summary>Which drives would be walked now, with what makes each one itself.</summary>
    public static List<(char Letter, string Identity, string Description)> Candidates()
    {
        var list = new List<(char, string, string)>();
        foreach (DriveInfo d in DriveInfo.GetDrives())
        {
            try
            {
                if (!d.IsReady || !WalkedVolume.Walks(d.DriveType, d.DriveFormat)) continue;
                string identity = string.Create(CultureInfo.InvariantCulture,
                    $"{d.DriveFormat}|{d.VolumeLabel}|{d.TotalSize}");
                list.Add((char.ToUpperInvariant(d.Name[0]), identity,
                          $"{d.DriveFormat}, {(d.DriveType == DriveType.Fixed ? "fixed" : "removable")}"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* not ready */ }
        }
        return list;
    }

    /// <summary>Look at the drives until <paramref name="ct"/> ends.</summary>
    public async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { Look(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                Log.Repeat("names|walked|look", TimeSpan.FromMinutes(10), "WARN ", "names",
                           "the walked drives could not be looked at: " + ex.Message);
            }
            try { await Task.Delay(LookEvery, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>One look: add what arrived, drop what left.</summary>
    public void Look(CancellationToken ct)
    {
        var now = Candidates();
        foreach ((char letter, string identity, string description) in now)
        {
            if (_held.TryGetValue(letter, out Drive? had))
            {
                if (had.Identity == identity) continue;
                Drop(letter, "was replaced by another drive");
            }
            // A letter the file table already answers for is never walked a second time.
            if (_views.TryGetValue(letter, out VolumeView? existing) && !existing.NamesOnly) continue;
            Add(letter, identity, description, ct);
        }

        foreach (char letter in _held.Keys.ToList())
            if (!now.Exists(d => d.Letter == letter)) Drop(letter, "was removed");
    }

    private void Add(char letter, string identity, string description, CancellationToken ct)
    {
        string root = letter + ":\\";
        var ix = new NameIndex(letter);
        long started = Stopwatch.GetTimestamp();
        // Not yet in the table, so nothing else reads it and the walk writes it unheld.
        foreach (WalkedVolume.Entry e in WalkedVolume.Walk(root, "", ct))
            ix.Upsert(e.Id, e.Parent, e.Attributes, e.Name);
        ix.Trim();
        double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

        var drive = new Drive(letter, root, identity, ix, _gate);
        _held[letter] = drive;
        _views[letter] = new VolumeView(ix, 0, 0, ms, NamesOnly: true);
        drive.Watch();
        Log.Info("names", string.Create(CultureInfo.InvariantCulture,
            $"{letter}: {ix.Count:N0} names walked in {ms / 1000.0:F2}s ({description}); names only, followed by a watcher"));
    }

    private void Drop(char letter, string why)
    {
        if (!_held.Remove(letter, out Drive? drive)) return;
        drive.Dispose();
        if (_views.TryGetValue(letter, out VolumeView? v) && v.NamesOnly) _views.TryRemove(letter, out _);
        Log.Info("names", $"{letter}: {why}; its names are no longer searched");
    }

    public void Dispose()
    {
        foreach (Drive d in _held.Values) d.Dispose();
        _held.Clear();
    }

    /// <summary>One walked drive and its watcher.</summary>
    private sealed class Drive : IDisposable
    {
        public char Letter { get; }
        public string Identity { get; }
        private readonly string _root;
        private readonly NameIndex _ix;
        private readonly IndexLock _gate;
        private FileSystemWatcher? _watcher;
        private readonly Timer _settle;
        private readonly object _walking = new();
        private int _owed;
        private volatile bool _gone;

        public Drive(char letter, string root, string identity, NameIndex ix, IndexLock gate)
        {
            Letter = letter;
            Identity = identity;
            _root = root;
            _ix = ix;
            _gate = gate;
            _settle = new Timer(_ => WalkAgain(), null, Timeout.Infinite, Timeout.Infinite);
        }

        private IDisposable Write() => _gate.Write(Letter);

        public void Watch()
        {
            try
            {
                var w = new FileSystemWatcher(_root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    InternalBufferSize = 64 * 1024,
                };
                w.Created += (_, e) => On(WalkedVolume.ChangeKind.Created, e.FullPath, null);
                w.Deleted += (_, e) => On(WalkedVolume.ChangeKind.Deleted, e.FullPath, null);
                w.Renamed += (_, e) => On(WalkedVolume.ChangeKind.Renamed, e.FullPath, e.OldFullPath);
                // An overflow means notifications were lost; only a walk knows what they said.
                w.Error += (_, e) =>
                {
                    Log.Repeat("names|walked|overflow|" + Letter, TimeSpan.FromMinutes(10), "WARN ", "names",
                        $"{Letter}: the watcher lost changes ({e.GetException().Message}); walking the drive again");
                    Owe();
                };
                w.EnableRaisingEvents = true;
                _watcher = w;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or PlatformNotSupportedException)
            {
                Log.Warn("names", $"{Letter}: changes cannot be followed ({ex.Message}); its names are as they were when it was walked");
            }
        }

        private void On(WalkedVolume.ChangeKind kind, string path, string? oldPath)
        {
            if (_gone) return;
            try
            {
                string? rel = WalkedVolume.Relative(_root, path);
                if (rel is null || rel.Length == 0) return;
                string? oldRel = oldPath is null ? null : WalkedVolume.Relative(_root, oldPath);
                if (WalkedVolume.Apply(_ix, kind, rel, oldRel, AttributesOf, Write)) Owe();
            }
            catch (Exception ex)
            {
                Log.Repeat("names|walked|change|" + Letter, TimeSpan.FromMinutes(10), "WARN ", "names",
                    $"{Letter}: a change could not be applied ({ex.GetType().Name}: {ex.Message}); walking the drive again");
                Owe();
            }
        }

        /// <summary>What is at a path now; null when it has gone again.</summary>
        private FileAttributes? AttributesOf(string rel)
        {
            try { return File.GetAttributes(Path.Combine(_root, rel)); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
        }

        private void Owe()
        {
            if (_gone) return;
            Interlocked.Exchange(ref _owed, 1);
            try { _settle.Change(SettleFor, Timeout.InfiniteTimeSpan); } catch (ObjectDisposedException) { }
        }

        private void WalkAgain()
        {
            if (_gone || Interlocked.Exchange(ref _owed, 0) == 0) return;
            // One walk at a time; a change during it owes another, which the timer brings.
            if (!Monitor.TryEnter(_walking)) { Owe(); return; }
            try
            {
                long started = Stopwatch.GetTimestamp();
                (int written, int removed) = WalkedVolume.Reconcile(_ix, WalkedVolume.Walk(_root), Write);
                Log.Repeat("names|walked|again|" + Letter, TimeSpan.FromMinutes(10), "INFO ", "names",
                    string.Create(CultureInfo.InvariantCulture,
                        $"{Letter}: walked again after a folder changed, {written:N0} names, {removed:N0} gone, " +
                        $"{Stopwatch.GetElapsedTime(started).TotalSeconds:F2}s"));
            }
            catch (Exception ex) when (!_gone)
            {
                Log.Warn("names", $"{Letter}: walking the drive again failed: {ex.Message}");
            }
            catch (Exception) { /* pulled out mid-walk */ }
            finally { Monitor.Exit(_walking); }
        }

        public void Dispose()
        {
            _gone = true;
            _watcher?.Dispose();
            _settle.Dispose();
        }
    }
}
