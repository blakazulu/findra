using Findra;
using Findra.Pipe;
using Xunit;
using Pipelines = System.IO.Pipelines;   // `using Findra.Pipe` puts the namespace name
                                         // `Pipe` in scope, so `new Pipe()` would be
                                         // "namespace used like a type"

public class NameServerTests
{
    private static NameIndex Sample()
    {
        var ix = new NameIndex('C');
        ix.Upsert(5, 0, NtfsVolume.FileAttributeDirectory, "C:");
        ix.Upsert(100, 5, NtfsVolume.FileAttributeDirectory, "Photos");
        ix.Upsert(101, 100, 0, "sunset over water.jpg");
        ix.Upsert(102, 100, 0, "sunset over water.png");
        return ix;
    }

    /// <summary>A duplex pair of streams, so a server and a client can talk in-process.</summary>
    private static (Stream Server, Stream Client) Pair()
    {
        var a = new Pipelines.Pipe();
        var b = new Pipelines.Pipe();
        return (new DuplexStream(b.Reader.AsStream(), a.Writer.AsStream()),
                new DuplexStream(a.Reader.AsStream(), b.Writer.AsStream()));
    }

    /// <summary>Task 8's client tests reuse this duplex pair.</summary>
    public static (Stream Server, Stream Client) PairForTests() => Pair();

    [Fact]
    public async Task AnswersAQueryWithResolvedRows()
    {
        var (server, client) = Pair();
        var cts = new CancellationTokenSource();
        _ = NameServer.Serve(server, new Dictionary<char, NameIndex> { ['C'] = Sample() }, cts.Token);

        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindQuery, new QueryRequest(1, "sunset", 50)), default);
        byte[]? raw = await Frame.ReadAsync(client, default);

        QueryReply reply = Envelope.Unpack(raw!).Body<QueryReply>();
        Assert.Equal(1, reply.Gen);
        Assert.Equal(2, reply.Rows.Count);
        NameRow jpg = Assert.Single(reply.Rows, r => r.Name == "sunset over water.jpg");
        Assert.Equal(@"C:\Photos\sunset over water.jpg", jpg.Path);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task AppliesTheFiltersTheIndexScanDoesNotEnforce()
    {
        // NameIndex.Search's vectorised word-scan branch never consults q.Exts, so without
        // the Allows call in AnswerQuery this returns both files. Deleting that line must
        // fail this test - that is the whole point of it.
        var (server, client) = Pair();
        var cts = new CancellationTokenSource();
        _ = NameServer.Serve(server, new Dictionary<char, NameIndex> { ['C'] = Sample() }, cts.Token);

        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindQuery,
            new QueryRequest(1, "sunset ext:png", 50)), default);
        QueryReply reply = Envelope.Unpack((await Frame.ReadAsync(client, default))!).Body<QueryReply>();

        Assert.Single(reply.Rows);
        Assert.Equal("sunset over water.png", reply.Rows[0].Name);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task EchoesTheGenerationItWasAskedWith()
    {
        var (server, client) = Pair();
        var cts = new CancellationTokenSource();
        _ = NameServer.Serve(server, new Dictionary<char, NameIndex> { ['C'] = Sample() }, cts.Token);

        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindQuery, new QueryRequest(913, "sunset", 50)), default);
        QueryReply reply = Envelope.Unpack((await Frame.ReadAsync(client, default))!).Body<QueryReply>();

        Assert.Equal(913, reply.Gen);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task AnswersStatusWithItsOwnProcessId()
    {
        var (server, client) = Pair();
        var cts = new CancellationTokenSource();
        _ = NameServer.Serve(server, new Dictionary<char, NameIndex> { ['C'] = Sample() }, cts.Token);

        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindStatus, new StatusRequest()), default);
        StatusReply reply = Envelope.Unpack((await Frame.ReadAsync(client, default))!).Body<StatusReply>();

        Assert.Equal(Environment.ProcessId, reply.ProcessId);
        Assert.Equal('C', reply.Volumes[0].Letter);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task IgnoresAnUnknownKindAndKeepsServing()
    {
        var (server, client) = Pair();
        var cts = new CancellationTokenSource();
        _ = NameServer.Serve(server, new Dictionary<char, NameIndex> { ['C'] = Sample() }, cts.Token);

        await Frame.WriteAsync(client, Envelope.Pack("nonsense", new StatusRequest()), default);
        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindQuery, new QueryRequest(2, "sunset", 50)), default);

        QueryReply reply = Envelope.Unpack((await Frame.ReadAsync(client, default))!).Body<QueryReply>();
        Assert.Equal(2, reply.Gen);
        await cts.CancelAsync();
    }

    private sealed class DuplexStream(Stream read, Stream write) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        // Without this, disposing one end never completes the underlying pipe, so the other
        // end's read blocks forever instead of seeing EOF - and any test that simulates a
        // dropped connection silently becomes a test of its own timeout.
        protected override void Dispose(bool disposing)
        {
            if (disposing) { read.Dispose(); write.Dispose(); }
            base.Dispose(disposing);
        }

        public override void Flush() => write.Flush();
        public override Task FlushAsync(CancellationToken ct) => write.FlushAsync(ct);
        public override int Read(byte[] b, int o, int c) => read.Read(b, o, c);
        public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct) => read.ReadAsync(b, ct);
        public override void Write(byte[] b, int o, int c) => write.Write(b, o, c);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct) => write.WriteAsync(b, ct);
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
    }
}
