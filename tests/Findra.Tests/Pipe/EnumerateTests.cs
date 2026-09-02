using Findra;
using Findra.Pipe;
using Xunit;

public class EnumerateTests
{
    private static NameIndex Sample()
    {
        var ix = new NameIndex('C');
        ix.Upsert(5, 0, NtfsVolume.FileAttributeDirectory, "C:");
        ix.Upsert(10, 5, NtfsVolume.FileAttributeDirectory, "Papers");
        ix.Upsert(11, 10, 0, "lease.pdf");
        ix.Upsert(12, 10, 0, "notes.txt");
        ix.Upsert(13, 10, 0, "sunset.jpg");
        ix.Upsert(14, 10, 0, "build.exe");
        ix.Upsert(15, 10, NtfsVolume.FileAttributeDirectory, "Archive.pdf");   // a FOLDER named .pdf
        return ix;
    }

    /// <summary>Enumerate needs no journal state, so the view is zeroed apart from the index.
    /// The three-argument Serve overload does the same thing for Plan 1's tests.</summary>
    private static Dictionary<char, VolumeView> One()
        => new() { ['C'] = new VolumeView(Sample(), JournalId: 0, NextUsn: 0, EnumerateMs: 0) };

    /// <summary>Reads until the Done frame. The token carries a deadline, so a server that
    /// never terminates the stream fails this test in ten seconds instead of hanging CI.</summary>
    private static async Task<List<EnumerateReply>> Ask(Stream client, EnumerateRequest req, CancellationToken ct)
    {
        await Frame.WriteAsync(client, Envelope.Pack(Envelope.KindEnumerate, req), ct);
        var replies = new List<EnumerateReply>();
        while (true)
        {
            Envelope e = Envelope.Unpack((await Frame.ReadAsync(client, ct))!);
            Assert.Equal(Envelope.KindEnumerateReply, e.Kind);
            EnumerateReply r = e.Body<EnumerateReply>();
            replies.Add(r);
            if (r.Done) return replies;
        }
    }

    [Fact]
    public async Task OnlyThePathsWhoseSuffixTheCallerNamedComeBack()
    {
        var (server, client) = NameServerTests.PairForTests();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = NameServer.Serve(server, One(), new IndexLock(), new JournalBroadcast(), null, cts.Token);

        List<EnumerateReply> replies = await Ask(client, new EnumerateRequest(7, 'C', [".pdf", ".txt"], 100), cts.Token);

        string[] paths = replies.SelectMany(r => r.Files).Select(f => f.Path).Order().ToArray();
        Assert.Equal([@"C:\Papers\lease.pdf", @"C:\Papers\notes.txt"], paths);
        Assert.All(replies, r => Assert.Equal(7, r.Id));
        await cts.CancelAsync();
    }

    [Fact]
    public async Task DirectoriesAreNeverEnumeratedEvenWhenTheirNameEndsInASuffix()
    {
        // "Archive.pdf" is a FOLDER. Sending it back queues a directory for text extraction,
        // which fails once per folder for the life of the index.
        var (server, client) = NameServerTests.PairForTests();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = NameServer.Serve(server, One(), new IndexLock(), new JournalBroadcast(), null, cts.Token);

        List<EnumerateReply> replies = await Ask(client, new EnumerateRequest(1, 'C', [".pdf"], 100), cts.Token);

        EnumeratedFile only = Assert.Single(replies.SelectMany(r => r.Files));
        Assert.Equal(@"C:\Papers\lease.pdf", only.Path);
        Assert.Equal(11ul, only.Frn);
        await cts.CancelAsync();
    }

    [Fact]
    public async Task ABigVolumeArrivesInBatchesAndSaysWhenItIsDone()
    {
        // One frame per volume would be a 20 MB JSON payload on a real disk. Batching is what
        // keeps the helper's memory flat and lets the UI start queuing before the walk ends.
        var (server, client) = NameServerTests.PairForTests();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = NameServer.Serve(server, One(), new IndexLock(), new JournalBroadcast(), null, cts.Token);

        List<EnumerateReply> replies = await Ask(client, new EnumerateRequest(1, 'C', [".pdf", ".txt", ".jpg"], 2), cts.Token);

        // Assert the contract, not one flush discipline: fill-then-final-partial gives 2
        // frames and partial-then-empty-Done gives 3, and both are correct. What must hold
        // is that exactly one frame is marked Done, it is the last, and nothing is lost.
        Assert.True(replies.Count > 1, "3 files at a batch size of 2 must not arrive in one frame");
        Assert.Single(replies, r => r.Done);
        Assert.True(replies[^1].Done);
        Assert.All(replies.Take(replies.Count - 1), r => Assert.True(r.Files.Count <= 2));
        Assert.Equal(3, replies.Sum(r => r.Files.Count));
        await cts.CancelAsync();
    }

    [Fact]
    public async Task AVolumeTheHelperDoesNotHoldAnswersDoneWithNothing()
    {
        // A drive that failed to enumerate, or was unplugged. The UI must get an answer, not
        // a session that quietly stops replying.
        var (server, client) = NameServerTests.PairForTests();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = NameServer.Serve(server, One(), new IndexLock(), new JournalBroadcast(), null, cts.Token);

        List<EnumerateReply> replies = await Ask(client, new EnumerateRequest(1, 'Z', [".pdf"], 100), cts.Token);

        EnumerateReply only = Assert.Single(replies);
        Assert.True(only.Done);
        Assert.Empty(only.Files);
        await cts.CancelAsync();
    }
}
