using Findra;
using Xunit;

public sealed class IndexerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-ix-" + Guid.NewGuid().ToString("N"));

    private string Under(string name)
    {
        Directory.CreateDirectory(_dir);
        return Path.Combine(_dir, name);
    }

    private ContentDb Open() => new(Under("search.db"));

    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    [Theory]
    [InlineData(new[] { "--index", "4321" }, 4321)]
    [InlineData(new[] { "--index" }, 0)]                 // no pid: run standalone, never exit early
    [InlineData(new[] { "--index", "not-a-number" }, 0)]
    [InlineData(new[] { "--index", "-7" }, 0)]           // a negative pid is not a pid
    public void ParsesTheParentPidOrZero(string[] args, int expected)
        => Assert.Equal(expected, IndexerArgs.Parse(args).ParentPid);

    [Fact]
    public void TheDatabasePathIsTheDefaultUnlessGiven()
    {
        Assert.Equal(ContentDb.DefaultPath, IndexerArgs.Parse(["--index", "1"]).DbPath);
        Assert.Equal(@"C:\somewhere\other.db", IndexerArgs.Parse(["--index", "1", @"C:\somewhere\other.db"]).DbPath);
    }

    [Fact]
    public void DrainingIndexesAPlainTextFileAndItsWordsBecomeFindable()
    {
        string doc = Under("meeting notes.txt");
        File.WriteAllText(doc, "We agreed the quarterly lease agreement is signed on the fourteenth. "
                             + "Rent is paid monthly and the deposit is returned at the end.");

        using ContentDb db = Open();
        db.Enqueue("C", 1, doc, ResultKind.Document, "probe");

        var lines = new List<string>();
        Indexer.DrainOnce(db, lines.Add);

        Assert.Equal(0, db.PendingCount());
        Assert.Equal(1, db.IndexedCount());
        ContentDb.SegmentHit hit = Assert.Single(db.Fts("deposit", 10));
        Assert.Equal(doc, hit.Path);
        Assert.Contains("deposit", hit.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(lines, l => l.Contains("indexed", StringComparison.Ordinal));
    }

    [Fact]
    public void AKindWithNoDecoderIsSkippedWithAReasonRatherThanFailed()
    {
        // Spec §6: a missing model is a normal state, not an error state. An all-or-nothing
        // gate - index nothing at all until every model is present - would make this case
        // unreachable. Here a photo is a normal, recorded, re-queueable outcome, and the
        // document beside it is indexed regardless.
        string photo = Under("sunset.jpg");
        File.WriteAllBytes(photo, new byte[64]);
        string doc = Under("lease.txt");
        File.WriteAllText(doc, "the quarterly lease agreement, signed and countersigned");

        using ContentDb db = Open();
        db.Enqueue("C", 1, photo, ResultKind.Photo, "probe");
        db.Enqueue("C", 2, doc, ResultKind.Document, "probe");

        Indexer.DrainOnce(db, _ => { });

        (long queued, long indexed, long failed, long skipped) = db.Counts();
        Assert.Equal(0, queued);
        Assert.Equal(1, indexed);
        Assert.Equal(0, failed);            // NOT a failure
        Assert.Equal(1, skipped);
        Assert.Contains(db.Describe(photo), r => r.Contains("no decoder for this kind yet", StringComparison.Ordinal));

        // ...and Plan 5 can pick up exactly those, and nothing else.
        Assert.Equal(1, db.RequeueKinds([(int)ResultKind.Photo], "photos enabled"));
    }

    [Fact]
    public void AFileThatVanishedBeforeItsTurnLeavesTheQueueQuietly()
    {
        using ContentDb db = Open();
        db.Enqueue("C", 1, Under("never-existed.txt"), ResultKind.Document, "probe");

        Indexer.DrainOnce(db, _ => { });

        Assert.Equal(0, db.PendingCount());
        Assert.Equal((0L, 0L, 0L, 0L), db.Counts());   // nothing indexed, nothing failed
    }

    [Fact]
    public void AnUnreadableDocumentIsRecordedAsFailedWithItsReason()
    {
        // A malformed PDF must cost one row in the index and a log line, not the queue.
        string pdf = Under("broken.pdf");
        File.WriteAllBytes(pdf, "%PDF-1.4 this is not a pdf"u8.ToArray());

        using ContentDb db = Open();
        db.Enqueue("C", 1, pdf, ResultKind.Document, "probe");

        Indexer.DrainOnce(db, _ => { });

        Assert.Equal(0, db.PendingCount());
        Assert.Equal(1, db.Counts().Failed);
        (string Path, string Error) f = Assert.Single(db.RecentFailures(10));
        Assert.Equal(pdf, f.Path);
        Assert.NotEqual("", f.Error);
    }

    [Fact]
    public void ChunksActuallyOverlapSoASentenceOnABoundaryIsStillWhole()
    {
        // The overlap is the whole reason chunking is not just Split(): a sentence cut across
        // two chunks is findable in neither half. Asserting only "more than one chunk" and
        // "each is under `size`" passes with overlap: 0 - both are guaranteed by the loop
        // bounds - so this asserts the tail of each chunk reappears at the head of the next.
        string text = string.Join(" ", Enumerable.Range(0, 600).Select(i => $"word{i}"));
        List<string> chunks = DocText.Chunk(text, size: 200, overlap: 60, max: 240);

        Assert.True(chunks.Count > 2, $"a long text must chunk; got {chunks.Count}");
        for (int i = 1; i < chunks.Count; i++)
        {
            string tail = chunks[i - 1][^40..];
            Assert.True(chunks[i].Contains(tail[..20], StringComparison.Ordinal)
                        || tail.Contains(chunks[i][..20], StringComparison.Ordinal),
                $"chunk {i} does not overlap chunk {i - 1}: overlap is 0 and boundary text is lost");
        }
    }

    [Fact]
    public void EveryChunkOfALongDocumentIsIndexedNotJustTheFirst()
    {
        // End to end: extract, chunk, index, search. An earlier draft of this test claimed to
        // prove the overlap, and did not - with the default size 1200 / overlap 200, a marker
        // placed at the cut lands inside chunk 2 either way, so it passed at overlap: 0. The
        // overlap property is proved by ChunksActuallyOverlap... above; what THIS covers is
        // that a document longer than one chunk is indexed all the way to its end, which
        // fails if the chunk loop stops early, if `max` truncates, or if only the first
        // segment reaches FTS.
        string filler = string.Join(" ", Enumerable.Repeat("padding", 900));   // ~7,200 chars
        string doc = Under("long.txt");
        File.WriteAllText(doc, "zygomorphic " + filler + " brachiate " + filler + " quincunx");

        using ContentDb db = Open();
        db.Enqueue("C", 1, doc, ResultKind.Document, "probe");
        Indexer.DrainOnce(db, _ => { });

        Assert.NotEmpty(db.Fts("zygomorphic", 10));   // first chunk
        Assert.NotEmpty(db.Fts("brachiate", 10));     // somewhere in the middle
        Assert.NotEmpty(db.Fts("quincunx", 10));      // the very last chunk
    }
}
