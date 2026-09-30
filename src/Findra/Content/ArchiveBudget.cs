using System;
using System.IO;
using System.IO.Compression;

namespace Findra;

/// <summary>
/// How much one zip-based document may make Findra decompress: a Word, PowerPoint, Excel, EPUB or
/// OpenDocument file is a zip of XML parts, and a zip can declare anything it likes about itself.
///
/// <para><see cref="DocText.MaxChars"/> stops reading once enough TEXT has come out, but text is
/// not what costs: a part of a few kilobytes on disk can inflate to gigabytes of markup with no
/// words in it at all (a "zip bomb"), and a workbook's shared-strings table is read whole before a
/// single cell. So the bytes that come out of the archive are counted as they are read, across
/// every part of the file, and the reading stops at <see cref="MaxBytes"/>. The count is taken on
/// the decompressed stream itself, never from the sizes the archive claims for its parts.</para>
///
/// <para>Stopping is not failing. What was read before the limit is kept and indexed, the same
/// way a long book is read up to <see cref="DocText.MaxChars"/>: a real document that big is
/// better half-findable than not at all.</para>
/// </summary>
public sealed class ArchiveBudget
{
    /// <summary>Decompressed bytes one archive may yield. A two-hundred-page book's
    /// <c>document.xml</c> is a few megabytes and a large workbook's biggest sheet a few tens, so
    /// this is far beyond any document somebody wrote - and still only a few seconds of XML
    /// parsing, which is the cost that matters in a process reading one file after another.
    /// </summary>
    public const long MaxBytes = 256L << 20;

    /// <summary>Parts one archive may have opened. Bytes alone do not bound the work: an empty
    /// part costs nothing to inflate and still costs an open, and a deck of ten thousand slides
    /// is not a deck.</summary>
    public const int MaxParts = 10_000;

    private long _left;
    private int _parts;

    public ArchiveBudget(long maxBytes = MaxBytes) => _left = maxBytes;

    /// <summary>Open one part of the archive, counted against this file's budget. Throws
    /// <see cref="SpentException"/> - caught by the reader, which keeps what it has - once either
    /// limit is reached.</summary>
    public Stream Open(ZipArchiveEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (++_parts > MaxParts) throw new SpentException($"more than {MaxParts} parts");
        return new Counted(entry.Open(), this);
    }

    private void Take(int n)
    {
        _left -= n;
        if (_left < 0) throw new SpentException("more than the decompressed size one document may have");
    }

    /// <summary>The budget ran out. An <see cref="IOException"/> so that anything between the
    /// stream and the reader passes it on rather than wrapping it.</summary>
    public sealed class SpentException(string why) : IOException(why);

    private sealed class Counted(Stream inner, ArchiveBudget budget) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            budget.Take(n);
            return n;
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
