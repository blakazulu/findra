using Findra;
using Findra.Pipe;
using Xunit;

public sealed class FirstPassTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-pass-" + Guid.NewGuid().ToString("N"));

    private ContentDb Open()
    {
        Directory.CreateDirectory(_dir);
        return new ContentDb(Path.Combine(_dir, "search.db"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Fact]
    public async Task TheWalkKeepsTheRestOfTheFlowMovingWhileItRuns()
    {
        // The flow that runs a first pass owns the index's writer connection, and a first pass
        // over a real disk takes minutes. Everything else that connection owes happens between
        // the walk's steps or it does not happen at all - which is what pressing "Start reading
        // now" during one felt like: the request was posted to a queue nobody would drain until
        // the pass ended, so no indexer child started and nothing anywhere said why.
        //
        // The fake helper below will not send its Done frame until the walk has pumped, so a walk
        // that only pumps at the end never finishes and this test fails on its deadline rather
        // than on an assertion. That is the shape of the defect, reproduced.
        var pumped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, clientEnd) = NameServerTests.PairForTests();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));

        Task helper = Task.Run(async () =>
        {
            EnumerateRequest req = Envelope.Unpack((await Frame.ReadAsync(server, cts.Token))!)
                                           .Body<EnumerateRequest>();
            await Frame.WriteAsync(server, Envelope.Pack(Envelope.KindEnumerateReply,
                new EnumerateReply(req.Id, 'C', [new EnumeratedFile(101, @"C:\Papers\lease.pdf", 1)], false)),
                cts.Token);

            await pumped.Task.WaitAsync(cts.Token);

            await Frame.WriteAsync(server, Envelope.Pack(Envelope.KindEnumerateReply,
                new EnumerateReply(req.Id, 'C', [], true)), cts.Token);
        }, CancellationToken.None);

        using ContentDb db = Open();
        using var feeder = new QueueFeeder(db, () => Config.Default);
        await using var client = new NameClient(clientEnd);

        int pumps = 0;
        await FirstPass.WalkAsync(client, feeder, 'C',
            new VolumeResume('C', 1, 0, NeedsFullPass: true, Replayed: 0, "a full pass is owed"),
            batchSize: 100,
            pump: () => { pumps++; pumped.TrySetResult(); },
            cts.Token);

        await helper;
        Assert.True(pumps > 0, "the walk finished without ever giving the flow it runs on a turn");
    }
}
