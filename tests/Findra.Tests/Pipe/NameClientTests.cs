using Findra;
using Findra.Pipe;
using Xunit;

public class NameClientTests
{
    private static NameIndex Sample()
    {
        var ix = new NameIndex('C');
        ix.Upsert(5, 0, NtfsVolume.FileAttributeDirectory, "C:");
        ix.Upsert(100, 5, 0, "sunset.jpg");
        ix.Upsert(101, 5, 0, "sunrise.jpg");
        return ix;
    }

    [Fact]
    public async Task ReturnsRowsForALiveQuery()
    {
        var (server, client) = NameServerTests.PairForTests();
        var cts = new CancellationTokenSource();
        _ = NameServer.Serve(server, new Dictionary<char, NameIndex> { ['C'] = Sample() }, cts.Token);

        await using var c = new NameClient(client);
        QueryReply? reply = await c.SearchAsync("sunset", 50, default);

        Assert.NotNull(reply);
        Assert.Single(reply!.Rows);
        Assert.Equal("sunset.jpg", reply.Rows[0].Name);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task ReturnsStatusFromTheServer()
    {
        var (server, client) = NameServerTests.PairForTests();
        var cts = new CancellationTokenSource();
        _ = NameServer.Serve(server, new Dictionary<char, NameIndex> { ['C'] = Sample() }, cts.Token);

        await using var c = new NameClient(client);
        StatusReply status = await c.StatusAsync(default);

        Assert.Equal(Environment.ProcessId, status.ProcessId);
        Assert.Equal('C', status.Volumes[0].Letter);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task KeepsServingAfterAnUndecodableFrame()
    {
        // One malformed reply must not end the pump. If it does, the transport stays
        // writable and every later search awaits a completion nobody will ever make -
        // a search box that stops responding, with a log line as the only trace.
        var (server, client) = NameServerTests.PairForTests();

        Task pretendServer = Task.Run(async () =>
        {
            await Frame.ReadAsync(server, default);
            await Frame.WriteAsync(server, "this is not an envelope"u8.ToArray(), default);
            QueryRequest req = Envelope.Unpack((await Frame.ReadAsync(server, default))!).Body<QueryRequest>();
            await Frame.WriteAsync(server, Envelope.Pack(Envelope.KindQueryReply,
                new QueryReply(req.Gen, 'C', 0, new[] { new NameRow(1, "ok.jpg", @"C:\ok.jpg", 0, 1, 0) })), default);
        });

        await using var c = new NameClient(client);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Task<QueryReply?> poisoned = c.SearchAsync("first", 50, timeout.Token);

        QueryReply? survived = await c.SearchAsync("second", 50, timeout.Token);

        Assert.NotNull(survived);
        Assert.Equal("ok.jpg", survived!.Rows[0].Name);
        await pretendServer;
    }

    [Fact]
    public async Task FailsFastOnceThePumpIsGone()
    {
        // A closed connection must surface as an exception, never as an await that
        // never returns. Note the deliberate `default` token: a caller with no timeout
        // is exactly the case that would hang forever.
        var (server, client) = NameServerTests.PairForTests();
        await using var c = new NameClient(client);

        server.Dispose();                                   // the helper goes away

        async Task Attempt()
        {
            while (true)
            {
                try { await c.SearchAsync("anything", 50, default); }
                catch (IOException) { return; }             // the contract: the pump is gone
                catch (ObjectDisposedException) { return; } // the write faulted first - also fail-fast
                await Task.Delay(20);
            }
        }

        // The timeout is the TEST's guard, never the client's. If it trips, the client hung
        // and this must FAIL - do not catch OperationCanceledException here and call that a
        // pass, which would turn a hang into a green test.
        await Attempt().WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ADeadStatusWaiterDoesNotSwallowTheNextReply()
    {
        // A status waiter abandoned by a failed write must not consume the reply belonging
        // to the next caller. Cancelling cannot produce that state - the token throws at
        // _writeLock.WaitAsync, before the enqueue, so nothing is ever stranded. The write
        // itself has to fail AFTER the enqueue with the read side still alive, which is what
        // FailsFirstWrite arranges. Without the pump's skip-on-dequeue loop, the reply goes
        // to the corpse and the second call hangs until this test's timeout fails it.
        var (server, client) = NameServerTests.PairForTests();
        var cts = new CancellationTokenSource();
        _ = NameServer.Serve(server, new Dictionary<char, NameIndex> { ['C'] = Sample() }, cts.Token);

        await using var c = new NameClient(new FailsFirstWrite(client));

        await Assert.ThrowsAsync<IOException>(() => c.StatusAsync(default));   // strands a dead waiter

        StatusReply status = await c.StatusAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(Environment.ProcessId, status.ProcessId);

        await cts.CancelAsync();
    }

    /// <summary>Fails the first write only; reads and later writes pass straight through.</summary>
    private sealed class FailsFirstWrite(Stream inner) : Stream
    {
        private int _writes;
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> b, CancellationToken ct) =>
            Interlocked.Increment(ref _writes) == 1
                ? ValueTask.FromException(new IOException("simulated transient write failure"))
                : inner.WriteAsync(b, ct);
        public override ValueTask<int> ReadAsync(Memory<byte> b, CancellationToken ct) => inner.ReadAsync(b, ct);
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] b, int o, int n) => inner.Read(b, o, n);
        public override void Write(byte[] b, int o, int n) => inner.Write(b, o, n);
        public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    [Fact]
    public async Task DropsAStaleReply()
    {
        // A server that answers the SECOND query first, so the answer to the abandoned
        // first query lands last - exactly the race the counter exists for.
        //
        // Do not gate the second SearchAsync behind the server's first write: the server
        // cannot write until it has read both frames, and the second frame is only sent
        // by the call the gate would be blocking. That deadlocks.
        var (server, client) = NameServerTests.PairForTests();

        Task pretendServer = Task.Run(async () =>
        {
            QueryRequest first  = Envelope.Unpack((await Frame.ReadAsync(server, default))!).Body<QueryRequest>();
            QueryRequest second = Envelope.Unpack((await Frame.ReadAsync(server, default))!).Body<QueryRequest>();

            await Frame.WriteAsync(server, Envelope.Pack(Envelope.KindQueryReply,
                new QueryReply(second.Gen, 'C', 0, new[] { new NameRow(1, "new.jpg", @"C:\new.jpg", 0, 1, 0) })), default);
            await Frame.WriteAsync(server, Envelope.Pack(Envelope.KindQueryReply,
                new QueryReply(first.Gen, 'C', 0, new[] { new NameRow(2, "old.jpg", @"C:\old.jpg", 0, 1, 0) })), default);
        });

        await using var c = new NameClient(client);
        // Sequential calls: SearchAsync writes its frame before it suspends, so the
        // generation order on the wire is deterministic without any synchronisation.
        Task<QueryReply?> slow = c.SearchAsync("sun", 50, default);
        Task<QueryReply?> fast = c.SearchAsync("sunset", 50, default);

        await pretendServer;

        QueryReply? winner = await fast;
        Assert.NotNull(winner);
        Assert.Equal("new.jpg", winner!.Rows[0].Name);   // the RIGHT reply, not merely a non-null one
        Assert.Null(await slow);                          // the stale answer is dropped, not shown
    }
}
