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

    /// <summary>A NameIndex whose parent chain resolves to a REAL path on this machine, so the
    /// record the helper answers with names a file that actually exists and has a timestamp.
    /// Returns the FRN of the leaf.</summary>
    private static (NameIndex Index, ulong Frn) IndexOver(string filePath)
    {
        string root = Path.GetPathRoot(filePath)!.TrimEnd('\\');       // "C:"
        var ix = new NameIndex(root[0]);
        ulong frn = 5;
        ix.Upsert(frn, 0, NtfsVolume.FileAttributeDirectory, root);
        ulong parent = frn;
        string[] parts = filePath[(root.Length + 1)..].Split('\\');
        for (int i = 0; i < parts.Length; i++)
        {
            frn++;
            ix.Upsert(frn, parent, i < parts.Length - 1 ? NtfsVolume.FileAttributeDirectory : 0u, parts[i]);
            parent = frn;
        }
        return (ix, frn);
    }

    [Fact]
    public async Task AnEnumeratedFileCarriesItsModificationTimeSoAPassCanSeeAnEdit()
    {
        // Without this the first pass can only ask "have I seen this FRN before". It then sees a
        // file that is NEW and is blind to one that was MODIFIED while Findra was closed - and it
        // is the ONLY fallback once the journal has wrapped, so the edited file keeps answering
        // searches with its old words indefinitely. Reading the timestamp is a metadata call and
        // not content parsing, which is what lets the elevated helper do it at all (spec §3).
        string dir = Path.Combine(Path.GetTempPath(), "findra-enum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "lease.pdf");
        try
        {
            await File.WriteAllTextAsync(file, "a lease");
            var when = new DateTime(2026, 3, 4, 5, 6, 7, DateTimeKind.Utc);
            File.SetLastWriteTimeUtc(file, when);

            (NameIndex ix, ulong frn) = IndexOver(file);
            var (server, client) = NameServerTests.PairForTests();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            _ = NameServer.Serve(server,
                new Dictionary<char, VolumeView> { [ix.Letter] = new(ix, JournalId: 0, NextUsn: 0, EnumerateMs: 0) },
                new IndexLock(), new JournalBroadcast(), null, cts.Token);

            List<EnumerateReply> replies = await Ask(client, new EnumerateRequest(3, ix.Letter, [".pdf"], 100), cts.Token);

            EnumeratedFile only = Assert.Single(replies.SelectMany(r => r.Files));
            Assert.Equal(frn, only.Frn);
            Assert.Equal(when.Ticks, only.Mtime);
            await cts.CancelAsync();
        }
        finally { try { Directory.Delete(dir, true); } catch { } }
    }

    [Fact]
    public async Task AFileTheHelperCannotStatCarriesZeroRatherThanTheFiletimeEpoch()
    {
        // Windows answers a missing or unreadable file with the FILETIME epoch - 1601, a large
        // non-zero tick count - rather than an error. Passed through, that is a modification time
        // the pass would happily compare and find equal on every walk, which is the original hole
        // wearing a number. Zero is the only honest answer, and the pass reads it as "cannot prove
        // this file is unchanged".
        var (server, client) = NameServerTests.PairForTests();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = NameServer.Serve(server, One(), new IndexLock(), new JournalBroadcast(), null, cts.Token);

        List<EnumerateReply> replies = await Ask(client, new EnumerateRequest(4, 'C', [".pdf"], 100), cts.Token);

        EnumeratedFile only = Assert.Single(replies.SelectMany(r => r.Files));
        Assert.Equal(@"C:\Papers\lease.pdf", only.Path);      // a fixture path; no such file exists
        Assert.Equal(0, only.Mtime);
        await cts.CancelAsync();
    }

    /// <summary>One file per suffix, all of the same shape so no suffix is a suffix of another
    /// and the count is the only thing under test.</summary>
    private static NameIndex Many(int count)
    {
        var ix = new NameIndex('C');
        ix.Upsert(5, 0, NtfsVolume.FileAttributeDirectory, "C:");
        ix.Upsert(10, 5, NtfsVolume.FileAttributeDirectory, "Papers");
        for (int i = 0; i < count; i++)
            ix.Upsert((ulong)(100 + i), 10, 0, "f" + i.ToString("000") + Suffix(i));
        return ix;
    }

    private static string Suffix(int i) => ".x" + i.ToString("000");

    [Fact]
    public async Task EveryNamedSuffixComesBackEvenWhenTheListIsLongerThanOneRequestCarries()
    {
        // The helper clamps the suffix list and drops the tail. ContentSuffixes() is sorted, so a
        // build that outgrows the clamp loses the alphabetically LAST extensions from every first
        // pass on every machine, silently and forever - the exact drift the generated list exists
        // to prevent. The caller must never be able to lose a suffix by naming too many.
        int count = NameServer.MaxSuffixes + 3;
        var (server, client) = NameServerTests.PairForTests();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        _ = NameServer.Serve(server,
            new Dictionary<char, VolumeView> { ['C'] = new(Many(count), JournalId: 0, NextUsn: 0, EnumerateMs: 0) },
            new IndexLock(), new JournalBroadcast(), null, cts.Token);

        await using var c = new NameClient(client);
        string[] asked = [.. Enumerable.Range(0, count).Select(Suffix)];
        var got = new List<string>();
        await foreach (EnumeratedFile f in c.EnumerateAsync('C', asked, 100, cts.Token)) got.Add(f.Path);

        Assert.Equal(count, got.Count);
        Assert.Equal(count, got.Distinct().Count());
        // The ones that vanish first are the tail of the list: the clamp drops the tail.
        Assert.Contains(got, p => p.EndsWith(Suffix(count - 1), StringComparison.Ordinal));
        await cts.CancelAsync();
    }

    /// <summary>
    /// A transport that does to the enumerate handler what a real machine does to it, and what an
    /// in-process stream pair cannot.
    ///
    /// <para>It moves the volume on every frame the handler sends, which drives the walk through
    /// its restart budget deterministically instead of hoping a churning background thread lands
    /// in the right gap; the epoch is checked BEFORE a frame goes out, so a bump made during the
    /// write is seen by the next batch, every time.</para>
    ///
    /// <para>And it finishes the write on a thread of its own, so whatever the handler does after
    /// the await runs somewhere else. A named pipe does that as a matter of course - the
    /// continuation resumes on whichever pool thread the completion lands on - and a
    /// <c>System.IO.Pipelines</c> pair usually resumes inline, which is why nothing in this suite
    /// had ever exercised the one path that holds a lock across an await.</para>
    /// </summary>
    private sealed class MovesTheVolumeAndResumesElsewhere(Stream inner, IndexLock gate, char volume) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken ct) => inner.FlushAsync(ct);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct)
            => inner.ReadAsync(buffer, ct);
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct)
        {
            gate.Bump(volume);
            await OnAThreadOfItsOwn().ConfigureAwait(false);
            await inner.WriteAsync(buffer, ct).ConfigureAwait(false);
        }

        // No RunContinuationsAsynchronously: the point is that the awaiting code resumes on the
        // brand new thread that completed this, which is never the thread it entered on.
        private static Task OnAThreadOfItsOwn()
        {
            var done = new TaskCompletionSource();
            new Thread(done.SetResult) { IsBackground = true }.Start();
            return done.Task;
        }
    }

    [Fact]
    public async Task AVolumeThatKeepsMovingIsStillWalkedToADoneFrame()
    {
        // The walk restarts when the index is rewritten under it, and after a few restarts it
        // stops batching and takes one hold for the whole pass, so that a volume under constant
        // churn still terminates. That last pass used to write its frames from inside the hold -
        // and a ReaderWriterLockSlim belongs to the thread that entered it, so releasing it after
        // an await threw SynchronizationLockException on whatever thread the write came back on.
        // The enumeration then ended with no Done frame at all: on the machine this was found on,
        // the interface waited for the rest of a first pass that was never coming, and the queue
        // feeder, the capsule's line and the indexer child all stopped with it.
        var gate = new IndexLock();
        var (server, client) = NameServerTests.PairForTests();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        _ = NameServer.Serve(new MovesTheVolumeAndResumesElsewhere(server, gate, 'C'),
                             One(), gate, new JournalBroadcast(), null, cts.Token);

        List<EnumerateReply> replies = await Ask(client, new EnumerateRequest(9, 'C', [".pdf", ".txt", ".jpg"], 1), cts.Token);

        Assert.True(replies[^1].Done);
        Assert.Single(replies, r => r.Done);
        string[] paths = replies.SelectMany(r => r.Files).Select(f => f.Path).Distinct().Order().ToArray();
        Assert.Equal([@"C:\Papers\lease.pdf", @"C:\Papers\notes.txt", @"C:\Papers\sunset.jpg"], paths);
        await cts.CancelAsync();
    }

    [Fact]
    public void TheContentSuffixListFitsInOneEnumerateRequest()
    {
        // A pin, not a preference. The helper honours at most NameServer.MaxSuffixes suffixes per
        // request; anything beyond that costs a second full walk of the volume. When this fails,
        // the choice is to raise the clamp (a longer per-record inner loop inside the elevated
        // process) or to accept the extra pass - not to quietly ship a shorter list.
        int have = QueueFeeder.ContentSuffixes().Count;
        Assert.True(have <= NameServer.MaxSuffixes,
            $"FileKinds now yields {have} content extensions and one enumerate request carries " +
            $"{NameServer.MaxSuffixes}; the first pass would take {1 + have / NameServer.MaxSuffixes} " +
            "walks of every volume. Raise NameServer.MaxSuffixes or accept the extra pass.");
    }
}
