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
    public async Task GenerationAdvancesWithEachSearch()
    {
        var (server, client) = NameServerTests.PairForTests();
        var cts = new CancellationTokenSource();
        _ = NameServer.Serve(server, new Dictionary<char, NameIndex> { ['C'] = Sample() }, cts.Token);

        await using var c = new NameClient(client);
        await c.SearchAsync("sun", 50, default);
        long first = c.CurrentGeneration;
        await c.SearchAsync("sunset", 50, default);

        Assert.Equal(first + 1, c.CurrentGeneration);
        await cts.CancelAsync();
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

        Assert.NotNull(await fast);
        Assert.Null(await slow);           // the stale answer is dropped, not shown
    }
}
