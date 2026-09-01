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

    [Fact]
    public async Task EveryVolumeContributesAndTheBestRowsWin()
    {
        // Concatenating volumes and breaking at the cap means the second disk never appears
        // once the first fills it - and NameIndex.Search appends in MFT order, so what
        // survived was an arbitrary prefix of C: rather than the best matches on the machine.
        var c = new NameIndex('C');
        c.Upsert(5, 0, NtfsVolume.FileAttributeDirectory, "C:");
        for (ulong i = 0; i < 20; i++)
            c.Upsert(100 + i, 5, 0, $"holiday photo of a sunset {i}.jpg");   // mid-name match

        var d = new NameIndex('D');
        d.Upsert(5, 0, NtfsVolume.FileAttributeDirectory, "D:");
        d.Upsert(200, 5, 0, "sunset.jpg");                                   // prefix match, scores higher

        var (server, client) = Pair();
        var cts = new CancellationTokenSource();
        _ = NameServer.Serve(server, new Dictionary<char, NameIndex> { ['C'] = c, ['D'] = d }, cts.Token);

        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindQuery, new QueryRequest(1, "sunset", 3)), default);
        QueryReply reply = Envelope.Unpack((await Frame.ReadAsync(client, default))!).Body<QueryReply>();

        Assert.Equal(3, reply.Rows.Count);
        Assert.Contains(reply.Rows, r => r.Volume == 'D');            // D: is not starved by C:
        Assert.Equal("sunset.jpg", reply.Rows[0].Name);               // and the best row is first
        Assert.Equal(@"D:\sunset.jpg", reply.Rows[0].Path);
        for (int i = 1; i < reply.Rows.Count; i++)
            Assert.True(reply.Rows[i - 1].Score >= reply.Rows[i].Score, "rows are not score-ordered");
        await cts.CancelAsync();
    }

    [Fact]
    public async Task RefusesAnAbsurdlyLongQueryAndKeepsServing()
    {
        // Max is clamped; Raw was not, and a `regex:` prefix hands it to the Regex constructor
        // inside the ELEVATED process. The refusal must still answer, or the caller's
        // generation gate waits on a reply that never comes.
        var (server, client) = Pair();
        var cts = new CancellationTokenSource();
        _ = NameServer.Serve(server, new Dictionary<char, NameIndex> { ['C'] = Sample() }, cts.Token);

        string huge = "regex:" + new string('a', 8000);
        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindQuery, new QueryRequest(1, huge, 50)), default);
        QueryReply refused = Envelope.Unpack((await Frame.ReadAsync(client, default))!).Body<QueryReply>();

        Assert.Equal(1, refused.Gen);
        Assert.Empty(refused.Rows);

        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindQuery, new QueryRequest(2, "sunset", 50)), default);
        QueryReply after = Envelope.Unpack((await Frame.ReadAsync(client, default))!).Body<QueryReply>();
        Assert.Equal(2, after.Gen);
        Assert.Equal(2, after.Rows.Count);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task ANullBodyDoesNotEndTheSession()
    {
        // An envelope whose Json field is the literal `null` throws InvalidDataException, not
        // JsonException - the shape that used to escape the dispatch guard and drop the
        // session, taking the UI's search with it.
        var (server, client) = Pair();
        var cts = new CancellationTokenSource();
        _ = NameServer.Serve(server, new Dictionary<char, NameIndex> { ['C'] = Sample() }, cts.Token);

        Envelope poison = Envelope.Unpack(Envelope.Pack(Envelope.KindQuery, new QueryRequest(1, "x", 1)))
                          with { Json = "null" };
        await Frame.WriteAsync(client,
            System.Text.Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(poison)), default);

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
