using System;
using System.Collections.Generic;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Numerics.Tensors;

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

    // ---- reading (Findra) ----

    /// <summary>Re-map if the indexer has appended since. Cheap when nothing changed.</summary>
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
        if (count == _mappedRows && _view is not null) { _count = count; return false; }

        _view?.Dispose(); _map?.Dispose();
        _view = null; _map = null;
        if (count > 0)
        {
            _map = MemoryMappedFile.CreateFromFile(new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite),
                null, HeaderBytes + count * RowBytes, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
            _view = _map.CreateViewAccessor(0, HeaderBytes + count * RowBytes, MemoryMappedFileAccess.Read);
            try
            {
                using var kf = new FileStream(_kindsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                _kinds = new byte[count];
                kf.ReadExactly(_kinds, 0, (int)Math.Min(count, kf.Length));
            }
            catch { _kinds = new byte[count]; }
        }
        _mappedRows = count;
        _count = count;
        return true;
    }

    public readonly record struct Match(long Row, float Score);

    /// <summary>Top-K rows by dot product with a normalised query, restricted to segment kinds in
    /// <paramref name="kinds"/> (empty = all). One pass, blocks of rows converted to float32.</summary>
    public List<Match> Search(ReadOnlySpan<float> query, int k, ReadOnlySpan<byte> kinds)
    {
        var top = new List<Match>(k + 1);
        if (_view is null || _count == 0) return top;
        const int Block = 256;
        var half = new Half[Block * Dim];
        var f = new float[Block * Dim];
        float floor = float.NegativeInfinity;

        for (long start = 0; start < _count; start += Block)
        {
            int n = (int)Math.Min(Block, _count - start);
            _view.ReadArray(HeaderBytes + start * RowBytes, half, 0, n * Dim);
            TensorPrimitives.ConvertToSingle(half.AsSpan(0, n * Dim), f.AsSpan(0, n * Dim));
            for (int i = 0; i < n; i++)
            {
                long row = start + i;
                byte kind = row < _kinds.Length ? _kinds[row] : (byte)0;
                if (kind == 255) continue;
                if (kinds.Length > 0 && kinds.IndexOf(kind) < 0) continue;
                float s = TensorPrimitives.Dot(query, f.AsSpan(i * Dim, Dim));
                if (s <= floor && top.Count >= k) continue;
                Insert(top, new Match(row, s), k);
                if (top.Count >= k) floor = top[^1].Score;
            }
        }
        return top;
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
