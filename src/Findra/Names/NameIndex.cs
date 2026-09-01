using System;
using System.Collections.Generic;
using System.Text;

namespace Findra;

// Every name on one volume, in RAM, laid out for a scan rather than for lookups.
//
// Three million names as .NET strings would be ~250 MB of object headers and UTF-16; here they are
// two UTF-8 byte buffers (the original for display, a case-folded one for matching) plus flat
// arrays per record, about a third of that. A search is ONE vectorised IndexOf pass over the folded
// buffer - the same scan for a 3-character query and a 30-character one - and each hit is mapped
// back to its record through a small segment table. That is what makes results appear as you type
// on a volume with millions of files, and it is the approach Everything takes.
//
// Records are appended and never moved. A rename appends the new name to the buffers and repoints
// the record; a delete tombstones it. The orphaned bytes are reclaimed by a rebuild, which happens
// anyway whenever the journal wraps. Not thread-safe by itself: the caller must serialise the USN
// replay and the searches with one lock, because a search takes milliseconds and a replay batch
// takes microseconds, so neither ever waits long.
public sealed class NameIndex
{
    /// <summary>Match: 0 = the whole name, 1 = a prefix, 2 = the start of a word, 3 = anywhere,
    /// 4 = within one typo, 5 = a wildcard pattern, 6 = filters only (every name they allow).</summary>
    public readonly record struct Hit(int Record, float Score, int Match);

    public char Letter { get; }

    // per record
    private ulong[] _frn = new ulong[1 << 16];
    private ulong[] _parent = new ulong[1 << 16];
    private uint[] _attr = new uint[1 << 16];
    private int[] _start = new int[1 << 16];        // into _orig / _fold (both buffers share offsets)
    private ushort[] _len = new ushort[1 << 16];    // bytes in _orig; 0 = tombstone
    private ushort[] _flen = new ushort[1 << 16];   // bytes in _fold (folding can change UTF-8 length)
    private int _count;
    private int _live;

    // the name buffers; a record's original and folded bytes start at the same offset, padded so
    // the longer of the two fits, which keeps one offset array enough for both
    private byte[] _orig = new byte[1 << 22];
    private byte[] _fold = new byte[1 << 22];
    private int _used;

    // segment table: buffer offset -> record, in offset order (so a hit can be located by binary
    // search); an entry whose record no longer starts there is an orphan and is skipped
    private int[] _segStart = new int[1 << 16];
    private int[] _segRecord = new int[1 << 16];
    private int _segCount;

    private readonly LongIntMap _byFrn = new();

    public NameIndex(char letter) => Letter = char.ToUpperInvariant(letter);

    public int Count => _live;
    public int Capacity => _count;
    public long BufferBytes => (long)_used * 2;

    // ---- building --------------------------------------------------------------------------------

    /// <summary>Add or update a record. Returns true when the name or parent changed (a rename or a
    /// move), which is what a content index needs to know about.</summary>
    public bool Upsert(ulong frn, ulong parent, uint attr, string name)
    {
        if (_byFrn.TryGet(frn, out int i))
        {
            bool moved = _parent[i] != parent;
            _parent[i] = parent;
            _attr[i] = attr;
            if (!NameEquals(i, name)) { Place(i, name); return true; }
            return moved;
        }

        if (_count == _frn.Length) Grow();
        i = _count++;
        _frn[i] = frn;
        _parent[i] = parent;
        _attr[i] = attr;
        Place(i, name);
        _byFrn.Set(frn, i);
        _live++;
        return true;
    }

    public bool Remove(ulong frn)
    {
        if (!_byFrn.TryGet(frn, out int i)) return false;
        _byFrn.Remove(frn);
        _len[i] = 0;
        _flen[i] = 0;
        _frn[i] = 0;
        _live--;
        return true;
    }

    public bool TryIndexOf(ulong frn, out int record) => _byFrn.TryGet(frn, out record);

    private void Place(int i, string name)
    {
        string folded = name.ToLowerInvariant();
        int ob = Encoding.UTF8.GetByteCount(name), fb = Encoding.UTF8.GetByteCount(folded);
        int need = Math.Max(ob, fb);
        // Two guard bytes so a match can never run from one name into the next: the folded buffer
        // carries a 0 between names and no UTF-8 query can contain a 0.
        while (_used + need + 1 > _orig.Length)
        {
            Array.Resize(ref _orig, _orig.Length * 2);
            Array.Resize(ref _fold, _fold.Length * 2);
        }
        Encoding.UTF8.GetBytes(name, 0, name.Length, _orig, _used);
        Encoding.UTF8.GetBytes(folded, 0, folded.Length, _fold, _used);
        // pad the shorter one to `need` so neither buffer's bytes bleed into the guard
        for (int k = ob; k < need; k++) _orig[_used + k] = 0;
        for (int k = fb; k < need; k++) _fold[_used + k] = 0;
        _orig[_used + need] = 0;
        _fold[_used + need] = 0;

        _start[i] = _used;
        _len[i] = (ushort)ob;
        _flen[i] = (ushort)fb;

        if (_segCount == _segStart.Length)
        {
            Array.Resize(ref _segStart, _segStart.Length * 2);
            Array.Resize(ref _segRecord, _segRecord.Length * 2);
        }
        _segStart[_segCount] = _used;
        _segRecord[_segCount] = i;
        _segCount++;

        _used += need + 1;
    }

    private bool NameEquals(int i, string name)
    {
        int ob = Encoding.UTF8.GetByteCount(name);
        if (ob != _len[i]) return false;
        Span<byte> tmp = ob <= 512 ? stackalloc byte[ob] : new byte[ob];
        Encoding.UTF8.GetBytes(name, tmp);
        return tmp.SequenceEqual(_orig.AsSpan(_start[i], ob));
    }

    /// <summary>After a full enumeration: shrink every buffer to what is used plus room for a
    /// day's churn. The doubling growth leaves up to half of each array empty, which on a
    /// million-file volume is a hundred megabytes of nothing.</summary>
    public void Trim()
    {
        int slack = Math.Max(65536, _count / 16);
        int n = _count + slack;
        if (_frn.Length > n)
        {
            Array.Resize(ref _frn, n); Array.Resize(ref _parent, n); Array.Resize(ref _attr, n);
            Array.Resize(ref _start, n); Array.Resize(ref _len, n); Array.Resize(ref _flen, n);
        }
        int bytes = _used + Math.Max(4 << 20, _used / 16);
        if (_orig.Length > bytes) { Array.Resize(ref _orig, bytes); Array.Resize(ref _fold, bytes); }
        int segs = _segCount + slack;
        if (_segStart.Length > segs) { Array.Resize(ref _segStart, segs); Array.Resize(ref _segRecord, segs); }
    }

    private void Grow()
    {
        int n = _frn.Length * 2;
        Array.Resize(ref _frn, n);
        Array.Resize(ref _parent, n);
        Array.Resize(ref _attr, n);
        Array.Resize(ref _start, n);
        Array.Resize(ref _len, n);
        Array.Resize(ref _flen, n);
    }

    // ---- reading -----------------------------------------------------------------------------------

    public bool IsAlive(int record) => record >= 0 && record < _count && _len[record] != 0;
    public bool IsDirectory(int record) => (_attr[record] & NtfsVolume.FileAttributeDirectory) != 0;
    public uint Attributes(int record) => _attr[record];
    public ulong Frn(int record) => _frn[record];
    public ulong Parent(int record) => _parent[record];

    public string Name(int record) => Encoding.UTF8.GetString(_orig, _start[record], _len[record]);

    /// <summary>Full path, or null when a parent is missing (the record is under something the
    /// index no longer has - a race with a delete, or a damaged enumeration).</summary>
    public string? PathOf(int record)
    {
        Span<int> chain = stackalloc int[128];
        int depth = 0;
        int i = record;
        if (IsAlive(i) && IsRoot(i)) return $"{Letter}:\\";
        while (true)
        {
            if (!IsAlive(i) || depth == chain.Length) return null;
            chain[depth++] = i;
            // The root (MFT record 5) is not always among the enumerated records, so the walk stops
            // at a PARENT that is the root rather than expecting to land on it.
            ulong p = _parent[i];
            if ((p & 0xFFFFFFFFFFFF) == 5) break;
            if (!_byFrn.TryGet(p, out i)) return null;
        }
        var sb = new StringBuilder(depth * 16 + 3);
        sb.Append(Letter).Append(":\\");
        for (int d = depth - 1; d >= 0; d--)
        {
            sb.Append(Name(chain[d]));
            if (d > 0) sb.Append('\\');
        }
        return sb.ToString();
    }

    /// <summary>The root directory is MFT record 5, and it is its own parent.</summary>
    public bool IsRoot(int record) => (_frn[record] & 0xFFFFFFFFFFFF) == 5;

    // ---- searching -------------------------------------------------------------------------------

    /// <summary>
    /// Records whose name contains every token of <paramref name="query"/> (case-folded), scored.
    /// One vectorised pass for the longest token, then the rest are verified per candidate.
    /// <paramref name="max"/> caps the candidate scan, not the score sort, so a query that matches
    /// half the volume still answers in the time it takes to find <paramref name="max"/> hits.
    /// </summary>
    public void Search(string query, List<Hit> into, int max = 4000)
        => Search(new SearchQuery(query), into, max);

    /// <summary>
    /// The parsed form: plain words are the vectorised scan as before; a glob is scanned by its
    /// longest literal run and then matched in full; a query with no name terms at all (filters
    /// only, or a pattern that is all wildcards) walks every live record. The filters themselves
    /// (type, folder, size, date) are the caller's - they need paths and stats this index does
    /// not hold.
    /// </summary>
    public void Search(SearchQuery q, List<Hit> into, int max = 4000)
    {
        // A regex has nothing the vectorised scan can look for, so it walks every live name.
        // Slower than a word (a few hundred ms on 1.5M names) and priced accordingly: it only
        // runs when the person typed `regex:`.
        if (q.Rx is not null)
        {
            for (int rec = 0; rec < _count && into.Count < max; rec++)
            {
                if (_len[rec] == 0 || IsMetafile(rec)) continue;
                if (q.Exts.Count > 0 && !ExtMatches(rec, q.Exts)) continue;
                string name = Name(rec);
                if (q.Kinds.Count > 0 && !q.Kinds.Contains(FileKinds.Classify(name, IsDirectory(rec)))) continue;
                bool ok;
                try { ok = q.Rx.IsMatch(name); } catch (System.Text.RegularExpressions.RegexMatchTimeoutException) { break; }
                if (!ok) continue;
                // plain words and globs beside the pattern still have to hold
                if (q.Words.Count > 0 || q.Phrases.Count > 0)
                {
                    string folded = name.ToLowerInvariant();
                    bool all = true;
                    foreach (var w in q.Words) if (!folded.Contains(w)) { all = false; break; }
                    if (all) foreach (var p in q.Phrases) if (!folded.Contains(p)) { all = false; break; }
                    if (!all) continue;
                }
                if (q.Globs.Count > 0 && !GlobsMatch(rec, q.Globs)) continue;
                into.Add(new Hit(rec, 0.88f, 5));
            }
            return;
        }

        var tokens = new List<byte[]>();
        foreach (var w in q.Words) tokens.Add(Encoding.UTF8.GetBytes(w));
        foreach (var ph in q.Phrases) tokens.Add(Encoding.UTF8.GetBytes(ph));
        var globs = q.Globs;
        // the longest literal run of a glob is scanned for like a word; the full match comes after
        byte[]? globLiteral = null;
        foreach (var g in globs)
        {
            string lit = SearchQuery.LiteralOf(g);
            if (lit.Length > (globLiteral?.Length ?? 0)) globLiteral = Encoding.UTF8.GetBytes(lit);
        }

        if (tokens.Count == 0 && globLiteral is null)
        {
            // nothing to scan for: every live name, checked against the globs (if any)
            if (!q.HasNameTerms && !q.HasFilters) return;
            // the extension and kind filters are applied HERE, not after: a walk that stops at
            // its cap before reaching the first .mp4 answers "ext:mp4" with nothing
            for (int rec = 0; rec < _count && into.Count < max; rec++)
            {
                if (_len[rec] == 0 || IsMetafile(rec)) continue;
                if (q.Exts.Count > 0 && !ExtMatches(rec, q.Exts)) continue;
                if (q.Kinds.Count > 0 && !q.Kinds.Contains(FileKinds.Classify(Name(rec), IsDirectory(rec)))) continue;
                if (globs.Count > 0 && !GlobsMatch(rec, globs)) continue;
                into.Add(new Hit(rec, globs.Count > 0 ? 0.88f : 0.7f, globs.Count > 0 ? 5 : 6));
            }
            return;
        }

        byte[] needle = globLiteral ?? tokens[0];
        foreach (var t in tokens) if (t.Length > needle.Length) needle = t;
        bool needleIsWord = tokens.Contains(needle);
        ReadOnlySpan<byte> hay = _fold.AsSpan(0, _used);

        int pos = 0;
        while (pos < hay.Length && into.Count < max)
        {
            int rel = hay.Slice(pos).IndexOf(needle);
            if (rel < 0) break;
            int at = pos + rel;

            int seg = SegmentAt(at);
            int rec = seg >= 0 ? _segRecord[seg] : -1;
            int segStart = seg >= 0 ? _segStart[seg] : 0;
            // skip orphaned bytes (a renamed record's old name) and tombstones
            bool valid = rec >= 0 && _len[rec] != 0 && _start[rec] == segStart
                         && at + needle.Length <= segStart + _flen[rec];
            if (valid && AllTokens(rec, tokens) && (globs.Count == 0 || GlobsMatch(rec, globs)) && !IsMetafile(rec))
            {
                int match = globs.Count > 0 && !needleIsWord ? 5 : MatchClass(rec, at - segStart, needle.Length);
                into.Add(new Hit(rec, Score(rec, match), match));
            }

            // continue at the next name; more hits inside this one are the same record
            pos = seg < 0 ? at + 1 : seg + 1 < _segCount ? _segStart[seg + 1] : _used;
        }
    }

    /// <summary>Names within one typo of <paramref name="word"/> - a wrong, missing, extra or
    /// swapped letter. A single edit leaves one half of the word intact, so each half is scanned
    /// for and the candidates checked properly. Cheap enough because it only runs when the exact
    /// query found little.</summary>
    public void SearchFuzzy(string word, List<Hit> into, int max = 400)
    {
        if (word.Length < 5) return;
        var seen = new HashSet<int>();
        string[] halves = { word[..(word.Length / 2)], word[(word.Length / 2)..] };
        ReadOnlySpan<byte> hay = _fold.AsSpan(0, _used);
        int checkedRecs = 0;
        foreach (var half in halves)
        {
            var needle = Encoding.UTF8.GetBytes(half);
            int pos = 0;
            while (pos < hay.Length && into.Count < max && checkedRecs < 60_000)
            {
                int rel = hay.Slice(pos).IndexOf(needle);
                if (rel < 0) break;
                int at = pos + rel;
                int seg = SegmentAt(at);
                int rec = seg >= 0 ? _segRecord[seg] : -1;
                int segStart = seg >= 0 ? _segStart[seg] : 0;
                if (rec >= 0 && _len[rec] != 0 && _start[rec] == segStart && seen.Add(rec) && !IsMetafile(rec))
                {
                    checkedRecs++;
                    string name = Encoding.UTF8.GetString(_fold, _start[rec], _flen[rec]);
                    if (!name.Contains(word) && SearchQuery.FuzzyContains(name, word))
                        into.Add(new Hit(rec, Score(rec, 4), 4));
                }
                pos = seg < 0 ? at + 1 : seg + 1 < _segCount ? _segStart[seg + 1] : _used;
            }
        }
    }

    // ".ext" against the tail of the folded bytes: no string for the million names that miss
    private bool ExtMatches(int rec, HashSet<string> exts)
    {
        var name = _fold.AsSpan(_start[rec], _flen[rec]);
        int dot = name.LastIndexOf((byte)'.');
        if (dot < 0 || dot == name.Length - 1) return exts.Contains("");
        string ext = Encoding.UTF8.GetString(name[(dot + 1)..]);
        return exts.Contains(ext);
    }

    /// <summary>Every record whose folded name equals <paramref name="name"/> exactly. A whole-name
    /// lookup, not a search: it answers "where are all the files called this" without scoring or
    /// ranking anything. Nothing in the name pipe calls it yet; it is what a "jump to this exact
    /// file name" path would use.</summary>
    public List<int> FindExact(string name)
    {
        var needle = Encoding.UTF8.GetBytes(name.ToLowerInvariant());
        var list = new List<int>();
        for (int rec = 0; rec < _count; rec++)
            if (_flen[rec] == needle.Length && _len[rec] != 0 && _fold.AsSpan(_start[rec], _flen[rec]).SequenceEqual(needle))
                list.Add(rec);
        return list;
    }

    private bool GlobsMatch(int rec, List<string> globs)
    {
        string name = Encoding.UTF8.GetString(_fold, _start[rec], _flen[rec]);
        foreach (var g in globs) if (!SearchQuery.GlobMatch(name, g)) return false;
        return true;
    }

    /// <summary>
    /// The plain word scan on its own, with none of the grammar: no globs, no regex, no
    /// filters-only walk. Nothing calls it - <see cref="Search(SearchQuery, List{Hit}, int)"/>
    /// replaced it - and it is kept only as the readable reference for what that branch does
    /// once the parsing is stripped away.
    /// </summary>
    [Obsolete("superseded by the SearchQuery overload; kept as the bare reference scan", false)]
    private void SearchLegacy(string query, List<Hit> into, int max)
    {
        var tokens = Tokens(query);
        if (tokens.Count == 0) return;

        int primary = 0;
        for (int t = 1; t < tokens.Count; t++)
            if (tokens[t].Length > tokens[primary].Length) primary = t;
        ReadOnlySpan<byte> needle = tokens[primary];
        ReadOnlySpan<byte> hay = _fold.AsSpan(0, _used);

        int pos = 0;
        while (pos < hay.Length && into.Count < max)
        {
            int rel = hay.Slice(pos).IndexOf(needle);
            if (rel < 0) break;
            int at = pos + rel;

            int seg = SegmentAt(at);
            int rec = seg >= 0 ? _segRecord[seg] : -1;
            int segStart = seg >= 0 ? _segStart[seg] : 0;
            // skip orphaned bytes (a renamed record's old name) and tombstones
            bool valid = rec >= 0 && _len[rec] != 0 && _start[rec] == segStart
                         && at + needle.Length <= segStart + _flen[rec];
            if (valid && (tokens.Count == 1 || AllTokens(rec, tokens)) && !IsMetafile(rec))
            {
                int match = MatchClass(rec, at - segStart, needle.Length);
                into.Add(new Hit(rec, Score(rec, match), match));
            }

            // continue at the next name; more hits inside this one are the same record
            pos = seg < 0 ? at + 1 : seg + 1 < _segCount ? _segStart[seg + 1] : _used;
        }
    }

    private bool AllTokens(int rec, List<byte[]> tokens)
    {
        var name = _fold.AsSpan(_start[rec], _flen[rec]);
        foreach (var t in tokens)
            if (name.IndexOf(t) < 0) return false;
        return true;
    }

    // $MFT, $LogFile and friends live at the root with a leading '$'. They are real MFT records and
    // real files, and nobody has ever wanted them in a search result.
    private bool IsMetafile(int rec)
        => _orig[_start[rec]] == (byte)'$' && (_parent[rec] & 0xFFFFFFFFFFFF) == 5;

    // How good a hit is, from where in the name it landed: an exact name beats a prefix beats the
    // start of a word beats a substring; shorter names win ties because the query explains more of
    // them. Folders are not penalised - "find that folder" is half of what a name search is for.
    private int MatchClass(int rec, int at, int matchLen)
    {
        if (at == 0 && matchLen == _flen[rec]) return 0;
        if (at == 0) return 1;
        return IsWordStart(_fold[_start[rec] + at - 1]) ? 2 : 3;
    }

    private float Score(int rec, int match)
    {
        float s = match switch { 0 => 1.0f, 1 => 0.90f, 2 => 0.80f, 4 => 0.58f, 5 => 0.88f, 6 => 0.70f, _ => 0.68f };
        // up to -0.08 for length: a 200-byte name that merely contains the query is a weak answer
        s -= Math.Min(0.08f, _flen[rec] / 2500f);
        return s;
    }

    private static bool IsWordStart(byte before)
        => before is (byte)' ' or (byte)'.' or (byte)'_' or (byte)'-' or (byte)'(' or (byte)'[' or (byte)',' or (byte)'+';

    private int SegmentAt(int offset)
    {
        int lo = 0, hi = _segCount - 1, ans = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_segStart[mid] <= offset) { ans = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        return ans;
    }

    public static List<byte[]> Tokens(string query)
    {
        var list = new List<byte[]>();
        foreach (var part in query.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            // a path query: only the last segment is a name; the rest is verified against the path
            string t = part;
            int slash = Math.Max(t.LastIndexOf('\\'), t.LastIndexOf('/'));
            if (slash >= 0) t = t[(slash + 1)..];
            if (t.Length == 0) continue;
            list.Add(Encoding.UTF8.GetBytes(t));
        }
        return list;
    }
}

// FRN -> record, open addressing with linear probing. A Dictionary<ulong,int> costs ~40 bytes an
// entry; this costs 12 at a 0.6 load, and there are millions of entries.
public sealed class LongIntMap
{
    private ulong[] _keys = new ulong[1 << 17];
    private int[] _vals = new int[1 << 17];
    private int _count, _tombs;
    private const ulong Empty = 0, Tomb = ulong.MaxValue;

    public int Count => _count;

    public bool TryGet(ulong key, out int value)
    {
        if (key is Empty or Tomb) { value = -1; return false; }
        int mask = _keys.Length - 1;
        int i = (int)(Mix(key) & (ulong)mask);
        while (true)
        {
            ulong k = _keys[i];
            if (k == key) { value = _vals[i]; return true; }
            if (k == Empty) { value = -1; return false; }
            i = (i + 1) & mask;
        }
    }

    public void Set(ulong key, int value)
    {
        if ((_count + _tombs + 1) * 10 > _keys.Length * 6) Rehash(_count * 10 > _keys.Length * 3 ? _keys.Length * 2 : _keys.Length);
        int mask = _keys.Length - 1;
        int i = (int)(Mix(key) & (ulong)mask);
        int firstTomb = -1;
        while (true)
        {
            ulong k = _keys[i];
            if (k == key) { _vals[i] = value; return; }
            if (k == Tomb && firstTomb < 0) firstTomb = i;
            if (k == Empty)
            {
                if (firstTomb >= 0) { i = firstTomb; _tombs--; }
                _keys[i] = key; _vals[i] = value; _count++;
                return;
            }
            i = (i + 1) & mask;
        }
    }

    public bool Remove(ulong key)
    {
        int mask = _keys.Length - 1;
        int i = (int)(Mix(key) & (ulong)mask);
        while (true)
        {
            ulong k = _keys[i];
            if (k == key) { _keys[i] = Tomb; _count--; _tombs++; return true; }
            if (k == Empty) return false;
            i = (i + 1) & mask;
        }
    }

    private void Rehash(int size)
    {
        var oldK = _keys; var oldV = _vals;
        _keys = new ulong[size]; _vals = new int[size];
        _count = 0; _tombs = 0;
        for (int i = 0; i < oldK.Length; i++)
            if (oldK[i] is not (Empty or Tomb)) Set(oldK[i], oldV[i]);
    }

    private static ulong Mix(ulong x)
    {
        x ^= x >> 33; x *= 0xff51afd7ed558ccdUL; x ^= x >> 33; x *= 0xc4ceb9fe1a85ec53UL; x ^= x >> 33;
        return x;
    }
}
