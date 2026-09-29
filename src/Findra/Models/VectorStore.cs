using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Numerics.Tensors;
using Microsoft.Win32.SafeHandles;

namespace Findra;

// The vectors: one fixed-width float16 row per segment, appended by the indexer, memory-mapped by
// Findra for the search. A dead row is all zeros - its dot product with anything is 0, so it can
// never surface, and no free list is needed. A parallel byte per row says what KIND of segment it
// is, so "photos only" never touches the database.
//
// Search is brute force on purpose: a normalised dot product over N rows is a single pass that
// the CPU vectorises, ~1 ms per 20k rows, and it needs no build step, no tuning and no memory
// beyond the file. An approximate index only starts to win past a million rows, and a library
// this size has far fewer.
public sealed class VectorStore : IDisposable
{
    public const int Dim = 768;
    private const int HeaderBytes = 16;   // magic, dim, count (int64)

    /// <summary>The four bytes every vector file starts with: 'F' 'V' 'S' '1'.
    ///
    /// <para>The constant looks reversed because it is not a string, it is an int32 written
    /// little-endian - the low byte lands first. Writing the "obvious" 0x46565331 puts `1SVF` on
    /// disk instead, and nothing ever notices: a header only this file reads back is happy either
    /// way round, and by the time anybody looks there are gigabytes of vectors behind it. So the
    /// test asserts on the BYTES on disk, never on the literal here.</para>
    /// </summary>
    private const int Magic = 0x31535646;
    private static readonly int RowBytes = Dim * 2;

    private readonly string _path, _kindsPath;
    private readonly bool _writer;
    private FileStream? _vecW, _kindW;
    private MemoryMappedFile? _map;
    private MemoryMappedViewAccessor? _view;
    private long _mappedRows;
    private byte[] _kinds = Array.Empty<byte>();
    private long _count;

    public static string DefaultPath => Path.Combine(Paths.Index, "vectors.bin");

    public long Count => _count;

    /// <summary>The file this store reads or writes.</summary>
    public string FilePath => _path;

    public VectorStore(string? path = null, bool writer = false)
    {
        _path = path ?? DefaultPath;
        _kindsPath = _path + ".kinds";
        _writer = writer;
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        if (writer)
        {
            _vecW = new FileStream(_path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            _kindW = new FileStream(_kindsPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            // a store written at another width belongs to another model: start it over (the
            // segment rows are re-pointed by the indexer's migration)
            if (_vecW.Length >= HeaderBytes)
            {
                Span<byte> hdr = stackalloc byte[HeaderBytes];
                _vecW.Seek(0, SeekOrigin.Begin);
                _vecW.ReadExactly(hdr);
                if (BitConverter.ToInt32(hdr) != Magic || BitConverter.ToInt32(hdr[4..]) != Dim)
                {
                    Log.Warn("models", $"vector store is {BitConverter.ToInt32(hdr[4..])}-wide, want {Dim} - recreating it");
                    _vecW.SetLength(0);
                    _kindW.SetLength(0);
                }
            }
            if (_vecW.Length < HeaderBytes)
            {
                _vecW.SetLength(0);
                Span<byte> h = stackalloc byte[HeaderBytes];
                BitConverter.TryWriteBytes(h, Magic);
                BitConverter.TryWriteBytes(h[4..], Dim);
                BitConverter.TryWriteBytes(h[8..], 0L);
                _vecW.Write(h);
                _vecW.Flush();
            }
            _count = (_vecW.Length - HeaderBytes) / RowBytes;
            if (_kindW.Length < _count) { _kindW.SetLength(_count); }
        }
        else Reload();
    }

    public void Dispose()
    {
        _view?.Dispose(); _map?.Dispose();
        _vecW?.Dispose(); _kindW?.Dispose();
    }

    // ---- writing (the indexer) ----

    /// <summary>Append a normalised vector; returns its row.</summary>
    public long Append(ReadOnlySpan<float> v, byte kind)
    {
        if (_vecW is null || _kindW is null) throw new InvalidOperationException("read-only store");
        if (v.Length != Dim) throw new ArgumentException($"vector has {v.Length} dims, want {Dim}");
        Span<byte> row = stackalloc byte[RowBytes];
        for (int i = 0; i < Dim; i++)
            BitConverter.TryWriteBytes(row[(i * 2)..], BitConverter.HalfToInt16Bits((Half)v[i]));
        long r = _count;
        _vecW.Seek(HeaderBytes + r * RowBytes, SeekOrigin.Begin);
        _vecW.Write(row);
        _kindW.Seek(r, SeekOrigin.Begin);
        _kindW.WriteByte(kind);
        _count++;
        return r;
    }

    /// <summary>Zero a row so it can never match again.</summary>
    public void Tombstone(long row)
    {
        if (_vecW is null || _kindW is null || row < 0 || row >= _count) return;
        Span<byte> zero = stackalloc byte[RowBytes];
        _vecW.Seek(HeaderBytes + row * RowBytes, SeekOrigin.Begin);
        _vecW.Write(zero);
        _kindW.Seek(row, SeekOrigin.Begin);
        _kindW.WriteByte(255);
    }

    public void Flush()
    {
        if (_vecW is null || _kindW is null) return;
        _vecW.Flush(true);
        _kindW.Flush(true);
        // the count lives in the header so a reader can trust the file length only up to it
        _vecW.Seek(8, SeekOrigin.Begin);
        Span<byte> c = stackalloc byte[8];
        BitConverter.TryWriteBytes(c, _count);
        _vecW.Write(c);
        _vecW.Flush(true);
    }

    /// <summary>Write <paramref name="rows"/>, in the order given, into a new store at
    /// <paramref name="toPath"/>: row <c>rows[i]</c> becomes row <c>i</c>, its bytes and its kind
    /// copied exactly, nothing re-encoded. Everything is on the disk before this returns, because
    /// the index is about to be pointed at it.</summary>
    public void CopyRowsTo(IReadOnlyList<long> rows, string toPath)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (_vecW is null || _kindW is null) throw new InvalidOperationException("read-only store");
        Flush();

        using var vec = new FileStream(toPath, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 20);
        using var kind = new FileStream(toPath + ".kinds", FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16);
        Span<byte> h = stackalloc byte[HeaderBytes];
        BitConverter.TryWriteBytes(h, Magic);
        BitConverter.TryWriteBytes(h[4..], Dim);
        BitConverter.TryWriteBytes(h[8..], (long)rows.Count);
        vec.Write(h);

        var row = new byte[RowBytes];
        foreach (long r in rows)
        {
            if (r < 0 || r >= _count) throw new ArgumentOutOfRangeException(nameof(rows), $"row {r} is not in a store of {_count}");
            _vecW.Seek(HeaderBytes + r * RowBytes, SeekOrigin.Begin);
            _vecW.ReadExactly(row);
            vec.Write(row);
            _kindW.Seek(r, SeekOrigin.Begin);
            int k = _kindW.ReadByte();
            kind.WriteByte(k < 0 ? (byte)0 : (byte)k);
        }
        vec.Flush(true);
        kind.Flush(true);
    }

    // ---- reading (Findra) ----

    /// <summary>The kind byte of every row, read straight from the file beside the vectors, with
    /// no mapping and no store to open. Empty when there is no file yet. Shared for writing, since
    /// the indexer holds the file open for as long as it runs.</summary>
    public static byte[] KindsOnDisk(string? path = null)
    {
        string kinds = (path ?? DefaultPath) + ".kinds";
        try
        {
            using var f = new FileStream(kinds, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bytes = new byte[f.Length];
            f.ReadExactly(bytes);
            return bytes;
        }
        catch (FileNotFoundException) { return []; }
        catch (DirectoryNotFoundException) { return []; }
    }

    /// <summary>Re-map if the indexer has appended since, and re-read the kind of every row
    /// whether it has or not: a delete marks a row's kind and changes no count, so a reader that
    /// re-read the kinds only when the count moved went on answering with every row deleted since
    /// it first opened the file. One byte a row, a fraction of what a search then reads.</summary>
    public bool Reload()
    {
        if (_writer) return false;
        if (!File.Exists(_path)) { _count = 0; return false; }
        long len;
        long count;
        using (var fs = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            len = fs.Length;
            if (len < HeaderBytes) { _count = 0; return false; }
            Span<byte> h = stackalloc byte[HeaderBytes];
            fs.ReadExactly(h);
            if (BitConverter.ToInt32(h) != Magic || BitConverter.ToInt32(h[4..]) != Dim) { _count = 0; return false; }
            count = Math.Min(BitConverter.ToInt64(h[8..]), (len - HeaderBytes) / RowBytes);
        }
        if (count == _mappedRows && _view is not null)
        {
            _kinds = ReadKinds(count);
            _count = count;
            return false;
        }

        _view?.Dispose(); _map?.Dispose();
        _view = null; _map = null;
        if (count > 0)
        {
            _map = MemoryMappedFile.CreateFromFile(new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                null, HeaderBytes + count * RowBytes, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            _view = _map.CreateViewAccessor(0, HeaderBytes + count * RowBytes, MemoryMappedFileAccess.Read);
            _kinds = ReadKinds(count);
        }
        _mappedRows = count;
        _count = count;
        return true;
    }

    /// <summary>The kind of each of the first <paramref name="count"/> rows. A row the kinds file
    /// does not reach yet reads as kind 0, as it always has.</summary>
    private byte[] ReadKinds(long count)
    {
        var kinds = new byte[count];
        try
        {
            using var kf = new FileStream(_kindsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            kf.ReadExactly(kinds, 0, (int)Math.Min(count, kf.Length));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return kinds;
    }

    public readonly record struct Match(long Row, float Score);

    /// <summary>One question put to the store: a normalised vector, how many rows to keep, and
    /// the segment kinds that may answer it (empty = every live kind).</summary>
    public readonly record struct Query(float[] Vector, int K, byte[] Kinds);

    /// <summary>Rows per block of the scan. The blocks are what the cores share out; each worker
    /// keeps its own best rows, and they are merged once at the end.</summary>
    public const int ScanBlock = 4096;

    /// <summary>At most this many questions in one pass: which ones a row answers is a bit each.</summary>
    public const int MaxQueries = 32;

    /// <summary>Half the cores, never fewer than one. A search runs while somebody types, and the
    /// indexer may be working beside it; the other half is theirs.</summary>
    private static int Workers => Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>Top-K rows by dot product with a normalised query, restricted to segment kinds in
    /// <paramref name="kinds"/> (empty = all). One question through <see cref="Search(IReadOnlyList{Query})"/>.</summary>
    public List<Match> Search(ReadOnlySpan<float> query, int k, ReadOnlySpan<byte> kinds)
        => Search([new Query(query.ToArray(), k, kinds.ToArray())])[0];

    /// <summary>
    /// Every question in ONE pass over the file, best first per question.
    ///
    /// <para>A content search asks two - one of the pictures, one of the words - and each used to
    /// read every row in the file, convert it to float32, and only then look at its kind. Now the
    /// kind byte is read first and a row is touched only when some question wants its kind, and
    /// then scored only against those questions. A deleted row, or one of a kind nobody asked
    /// for, is never read at all, so its pages are never brought in from the disk.</para>
    ///
    /// <para>Rows are read in place through the mapping rather than copied out first. The blocks
    /// are shared across <see cref="Workers"/> cores and merged at the end; ties go to the lower
    /// row, so the answer does not depend on which core finished first.</para>
    /// </summary>
    public unsafe List<Match>[] Search(IReadOnlyList<Query> queries)
    {
        ArgumentNullException.ThrowIfNull(queries);
        if (queries.Count > MaxQueries) throw new ArgumentException($"at most {MaxQueries} queries in one pass");
        var answers = new List<Match>[queries.Count];
        for (int q = 0; q < answers.Length; q++) answers[q] = new List<Match>();
        MemoryMappedViewAccessor? view = _view;
        if (view is null || _count == 0 || queries.Count == 0) return answers;

        // Which questions each kind byte answers, a bit per question. 255 marks a deleted row and
        // answers none of them, whatever a question asked for.
        var wants = new int[256];
        for (int q = 0; q < queries.Count; q++)
        {
            Query query = queries[q];
            if (query.Vector.Length != Dim) throw new ArgumentException($"query has {query.Vector.Length} dims, want {Dim}");
            if (query.K <= 0) continue;
            if (query.Kinds.Length == 0) for (int b = 0; b < 255; b++) wants[b] |= 1 << q;
            else foreach (byte b in query.Kinds) if (b != 255) wants[b] |= 1 << q;
        }

        byte[] kinds = _kinds;
        long count = Math.Min(_count, kinds.Length);
        int blocks = (int)((count + ScanBlock - 1) / ScanBlock);
        var merge = new object();

        byte* mapped = null;
        SafeMemoryMappedViewHandle handle = view.SafeMemoryMappedViewHandle;
        handle.AcquirePointer(ref mapped);
        try
        {
            nint first = (nint)(mapped + view.PointerOffset + HeaderBytes);
            Parallel.For(0, blocks, new ParallelOptions { MaxDegreeOfParallelism = Workers },
                () => new Best(queries),
                (block, _, best) =>
                {
                    long start = (long)block * ScanBlock, end = Math.Min(start + ScanBlock, count);
                    for (long row = start; row < end; row++)
                    {
                        int mask = wants[kinds[row]];
                        if (mask == 0) continue;
                        best.Score(row, new ReadOnlySpan<Half>((byte*)first + row * RowBytes, Dim), mask);
                    }
                    return best;
                },
                best => { lock (merge) best.AddTo(answers); });
        }
        finally { handle.ReleasePointer(); }

        for (int q = 0; q < answers.Length; q++)
        {
            answers[q].Sort(static (a, b) => b.Score.CompareTo(a.Score) is var c && c != 0 ? c : a.Row.CompareTo(b.Row));
            if (answers[q].Count > queries[q].K) answers[q].RemoveRange(queries[q].K, answers[q].Count - queries[q].K);
        }
        return answers;
    }

    /// <summary>One worker's best rows so far, per question, and the buffer it converts a row into.</summary>
    private sealed class Best
    {
        private readonly IReadOnlyList<Query> _queries;
        private readonly List<Match>[] _top;
        private readonly float[] _floor;
        private readonly float[] _row = new float[Dim];

        public Best(IReadOnlyList<Query> queries)
        {
            _queries = queries;
            _top = new List<Match>[queries.Count];
            _floor = new float[queries.Count];
            for (int q = 0; q < queries.Count; q++)
            {
                _top[q] = new List<Match>(Math.Max(0, queries[q].K) + 1);
                _floor[q] = float.NegativeInfinity;
            }
        }

        public void Score(long row, ReadOnlySpan<Half> stored, int mask)
        {
            TensorPrimitives.ConvertToSingle(stored, _row);
            while (mask != 0)
            {
                int q = System.Numerics.BitOperations.TrailingZeroCount(mask);
                mask &= mask - 1;
                int k = _queries[q].K;
                float s = TensorPrimitives.Dot(_queries[q].Vector, _row);
                if (s <= _floor[q] && _top[q].Count >= k) continue;
                Insert(_top[q], new Match(row, s), k);
                if (_top[q].Count >= k) _floor[q] = _top[q][^1].Score;
            }
        }

        public void AddTo(List<Match>[] answers)
        {
            for (int q = 0; q < answers.Length; q++) answers[q].AddRange(_top[q]);
        }
    }

    /// <summary>
    /// What ONE known row scores against a query, and which segment kind it holds.
    ///
    /// <para><see cref="Search"/> answers "what are the best rows", which cannot explain a row
    /// that is not among them - and a file somebody is asking about is almost always a file that
    /// did NOT come back. Without this, "why did this picture not match" had no answer that did
    /// not involve reading source and guessing, which is the same gap <c>--searchprobe</c> exists
    /// to close for the progress pill.</para>
    ///
    /// <para>A tombstoned row (kind 255) is reported as such rather than scored: its bytes are
    /// still on the disk and dotting them would produce a confident number for a segment that no
    /// longer belongs to anything.</para>
    /// </summary>
    public (float Score, byte Kind, bool Live) ScoreOf(long row, ReadOnlySpan<float> query)
    {
        if (_view is null || row < 0 || row >= _count) return (0f, 0, false);
        byte kind = row < _kinds.Length ? _kinds[row] : (byte)0;
        if (kind == 255) return (0f, kind, false);

        var half = new Half[Dim];
        var f = new float[Dim];
        _view.ReadArray(HeaderBytes + row * RowBytes, half, 0, Dim);
        TensorPrimitives.ConvertToSingle(half, f);
        return (TensorPrimitives.Dot(query, f), kind, true);
    }

    private static void Insert(List<Match> top, Match m, int k)
    {
        int at = top.Count;
        while (at > 0 && top[at - 1].Score < m.Score) at--;
        top.Insert(at, m);
        if (top.Count > k) top.RemoveAt(top.Count - 1);
    }

    public static void Normalise(Span<float> v)
    {
        float n = MathF.Sqrt(TensorPrimitives.Dot(v, v));
        if (n > 1e-6f) TensorPrimitives.Divide(v, n, v);
    }
}
