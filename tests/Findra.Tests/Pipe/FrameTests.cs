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
    public async Task ACancelledWriteLeavesNothingBehind()
    {
        // A torn frame is unrecoverable: the peer parses every later frame at the wrong
        // offset. Cancellation must leave the stream byte-for-byte untouched.
        var ms = new MemoryStream();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Frame.WriteAsync(ms, new byte[5000], cancelled.Token));

        Assert.Equal(0, ms.Length);
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
