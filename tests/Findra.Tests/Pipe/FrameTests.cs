using System.Text;
using Findra.Pipe;
using Xunit;

public class FrameTests
{
    [Fact]
    public async Task RoundTripsOnePayload()
    {
        var ms = new MemoryStream();
        await Frame.WriteAsync(ms, Encoding.UTF8.GetBytes("hello"), default);
        ms.Position = 0;

        byte[]? got = await Frame.ReadAsync(ms, default);

        Assert.NotNull(got);
        Assert.Equal("hello", Encoding.UTF8.GetString(got!));
    }

    [Fact]
    public async Task RoundTripsManyPayloadsInOrder()
    {
        var ms = new MemoryStream();
        foreach (string s in new[] { "a", "bb", "ccc" })
            await Frame.WriteAsync(ms, Encoding.UTF8.GetBytes(s), default);
        ms.Position = 0;

        Assert.Equal("a",   Encoding.UTF8.GetString((await Frame.ReadAsync(ms, default))!));
        Assert.Equal("bb",  Encoding.UTF8.GetString((await Frame.ReadAsync(ms, default))!));
        Assert.Equal("ccc", Encoding.UTF8.GetString((await Frame.ReadAsync(ms, default))!));
    }

    [Fact]
    public async Task ReturnsNullAtCleanEndOfStream()
    {
        var ms = new MemoryStream();
        Assert.Null(await Frame.ReadAsync(ms, default));
    }

    [Fact]
    public async Task ReassemblesAPayloadDeliveredInPieces()
    {
        // a pipe hands over whatever arrived; a reader that assumes one read per
        // frame silently truncates under load.
        var full = new MemoryStream();
        await Frame.WriteAsync(full, Encoding.UTF8.GetBytes(new string('x', 5000)), default);
        var drip = new DripStream(full.ToArray(), chunk: 7);

        byte[]? got = await Frame.ReadAsync(drip, default);

        Assert.NotNull(got);
        Assert.Equal(5000, got!.Length);
    }

    [Fact]
    public async Task RejectsAnOversizedLengthPrefix()
    {
        var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(Frame.MaxPayload + 1));
        ms.Position = 0;

        await Assert.ThrowsAsync<InvalidDataException>(() => Frame.ReadAsync(ms, default));
    }

    [Fact]
    public async Task ThrowsOnATruncatedPayload()
    {
        var ms = new MemoryStream();
        ms.Write(BitConverter.GetBytes(10));
        ms.Write(new byte[4]);
        ms.Position = 0;

        await Assert.ThrowsAsync<EndOfStreamException>(() => Frame.ReadAsync(ms, default));
    }

    [Fact]
    public async Task ACancelledWriteCannotLeaveAPartialFrame()
    {
        // A torn frame is unrecoverable: the peer reads the next frame as this one's
        // payload and parses everything after it at the wrong offset, forever.
        //
        // Cancelling before the write proves nothing - an implementation that writes the
        // header and the payload as two separate awaits also leaves zero bytes, because
        // the first write already sees the cancelled token. The tear only appears when
        // cancellation lands BETWEEN the two writes, so this stream cancels itself once
        // the first write has completed. A two-write implementation leaves exactly the
        // 4-byte header here; a single-write one leaves the whole frame or nothing.
        var inner = new MemoryStream();
        using var cts = new CancellationTokenSource();
        var stream = new CancelsAfterFirstWrite(inner, cts);

        try { await Frame.WriteAsync(stream, new byte[5000], cts.Token); }
        catch (OperationCanceledException) { }

        Assert.True(inner.Length is 0 or 5004,
            $"partial frame on the wire: {inner.Length} bytes - a peer would misparse everything after it");
    }

    /// <summary>Cancels the token once the first write has gone through.</summary>
    private sealed class CancelsAfterFirstWrite(Stream inner, CancellationTokenSource cts) : Stream
    {
        private int _writes;
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            await inner.WriteAsync(b, CancellationToken.None);
            if (Interlocked.Increment(ref _writes) == 1) await cts.CancelAsync();
        }
        public override Task FlushAsync(CancellationToken ct) => Task.CompletedTask;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] b, int o, int n) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int n) => inner.Write(b, o, n);
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }

    private sealed class DripStream(byte[] data, int chunk) : Stream
    {
        private int _pos;
        public override int Read(byte[] buffer, int offset, int count)
        {
            int n = Math.Min(Math.Min(chunk, count), data.Length - _pos);
            Array.Copy(data, _pos, buffer, offset, n);
            _pos += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => data.Length;
        public override long Position { get => _pos; set => _pos = (int)value; }
        public override void Flush() { }
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
}
