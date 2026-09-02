using System.Diagnostics;
using Findra;
using Microsoft.Data.Sqlite;
using Xunit;
using Xunit.Abstractions;

public sealed class IndexerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-ix-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _out;

    public IndexerTests(ITestOutputHelper output) => _out = output;

    private string Under(string name)
    {
        Directory.CreateDirectory(_dir);
        return Path.Combine(_dir, name);
    }

    private ContentDb Open() => new(Under("search.db"));

    private VectorStore? _vectors;
    private Decoders? _decoders;

    /// <summary>The decoder set these tests drain with: no capability at all, an empty model
    /// folder, and a vector store in this test's own temp directory. Deliberately
    /// <see cref="CapabilitySet.None"/> rather than what the machine has, so that what these
    /// tests assert about a photo does not change when somebody installs a model - and never
    /// <see cref="Decoders.ForThisMachine"/>, which takes a writer on the real index.</summary>
    private IDecoders Dec()
    {
        Directory.CreateDirectory(_dir);
        _vectors ??= new VectorStore(Path.Combine(_dir, "vectors.bin"), writer: true);
        return _decoders ??= new Decoders(CapabilitySet.None, _vectors, modelDir: _dir);
    }

    public void Dispose()
    {
        _decoders?.Dispose();
        _vectors?.Dispose();
        try { Directory.Delete(_dir, true); } catch { }
    }

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
        Indexer.DrainOnce(db, lines.Add, Dec());

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

        Indexer.DrainOnce(db, _ => { }, Dec());

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

        Indexer.DrainOnce(db, _ => { }, Dec());

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

        Indexer.DrainOnce(db, _ => { }, Dec());

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
        Indexer.DrainOnce(db, _ => { }, Dec());

        Assert.NotEmpty(db.Fts("zygomorphic", 10));   // first chunk
        Assert.NotEmpty(db.Fts("brachiate", 10));     // somewhere in the middle
        Assert.NotEmpty(db.Fts("quincunx", 10));      // the very last chunk
    }

    [Fact]
    public void ARequeuedSkippedFileIsOpenedAgainWhateverReasonTheRequeueGave()
    {
        // The promise every later capability rests on: it arrives, and exactly the rows that
        // were skipped for want of it are picked up. RequeueKinds takes a free-form reason, so
        // the indexer must not decide from that string alone whether a file is already
        // finished - a Skipped row has never been opened, whatever mtime is recorded beside it.
        // When this fails the counts do not move and nothing is logged, so nothing else notices.
        string photo = Under("sunset.jpg");
        File.WriteAllBytes(photo, new byte[64]);

        using ContentDb db = Open();
        db.Enqueue("C", 1, photo, ResultKind.Photo, "probe");

        var pass1 = new List<string>();
        Indexer.DrainOnce(db, pass1.Add, Dec());
        foreach (string l in pass1) _out.WriteLine("pass1: " + l);
        Assert.Equal(1, db.Counts().Skipped);

        int requeued = db.RequeueKinds([(int)ResultKind.Photo], "photos enabled");
        _out.WriteLine($"requeued: {requeued}, pending now {db.PendingCount()}");
        Assert.Equal(1, requeued);

        var pass2 = new List<string>();
        Indexer.DrainOnce(db, pass2.Add, Dec());
        foreach (string l in pass2) _out.WriteLine("pass2: " + l);

        // "current" means the row was dequeued without the decoder ever being asked.
        Assert.DoesNotContain(pass2, l => l.Contains("current", StringComparison.Ordinal));
        Assert.Contains(pass2, l => l.Contains("skipped", StringComparison.Ordinal));
        Assert.Equal(0, db.PendingCount());
    }

    [Fact]
    public void AnIndexedFileWhoseBytesDidNotChangeIsStillDequeuedUntouched()
    {
        // The other half of that guard. Re-queueing a file that really was indexed, and has not
        // changed since, must still cost nothing - only Recheck reopens one of those. Losing
        // this is how "a capability arrived" turns into re-indexing a finished disk.
        string doc = Under("lease.txt");
        File.WriteAllText(doc, "the quarterly lease agreement, signed and countersigned");

        using ContentDb db = Open();
        db.Enqueue("C", 1, doc, ResultKind.Document, "probe");
        Indexer.DrainOnce(db, _ => { }, Dec());
        Assert.Equal(1, db.IndexedCount());

        db.Enqueue("C", 1, doc, ResultKind.Document, "documents enabled");
        var lines = new List<string>();
        Indexer.DrainOnce(db, lines.Add, Dec());
        Assert.Contains(lines, l => l.Contains("current", StringComparison.Ordinal));

        db.Enqueue("C", 1, doc, ResultKind.Document, Indexer.Recheck);
        lines.Clear();
        Indexer.DrainOnce(db, lines.Add, Dec());
        Assert.Contains(lines, l => l.Contains("indexed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("doc")]
    [InlineData("xls")]
    [InlineData("ppt")]
    [InlineData("rtf")]
    [InlineData("odt")]
    [InlineData("odp")]
    [InlineData("ods")]
    public void AFormatWithNoReaderIsSkippedRatherThanReadAsRawBytes(string ext)
    {
        // These are documents by classification and unreadable by this build: the legacy binary
        // Office formats and the OpenDocument zips. Reading their bytes as text indexes
        // structure words and mojibake and then records the file as indexed, which is worse
        // than a gap - a gap is visible in --searchindex and a later reader can re-queue
        // exactly these rows.
        string f = Under("contract." + ext);
        File.WriteAllText(f, "the quarterly lease agreement was signed on the fourteenth of March");

        using ContentDb db = Open();
        db.Enqueue("C", 1, f, ResultKind.Document, "probe");
        Indexer.DrainOnce(db, _ => { }, Dec());

        Assert.Equal((0L, 0L, 0L, 1L), db.Counts());
        Assert.Contains(db.Describe(f), r => r.Contains("no decoder for this format yet", StringComparison.Ordinal));
        Assert.Empty(db.Fts("lease", 10));
    }

    [Fact]
    public void TheWordsInsideAnOpenDocumentZipAreNotFoundByReadingItsBytes()
    {
        // Why the format gate is not merely tidy: an .odt read as text indexes the zip's
        // structure and never its words, because they are deflate-compressed. Recorded as
        // indexed it becomes a file the index claims to hold and no search can ever return.
        string odt = Under("contract.odt");
        using (FileStream fs = File.Create(odt))
        using (var zip = new System.IO.Compression.ZipArchive(fs, System.IO.Compression.ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("content.xml").Open()))
            w.Write("<office><text>"
                    + string.Concat(Enumerable.Repeat("the quarterly lease agreement was signed. ", 400))
                    + "</text></office>");

        using ContentDb db = Open();
        db.Enqueue("C", 1, odt, ResultKind.Document, "probe");
        Indexer.DrainOnce(db, _ => { }, Dec());

        Assert.Equal(0, db.IndexedCount());
        Assert.Equal(1, db.Counts().Skipped);
        Assert.Empty(db.Fts("lease", 10));
    }

    [Fact]
    public void CanExtractKnowsWhichFormatsThisBuildCanReadInside()
    {
        foreach (string ext in new[] { "pdf", "docx", "pptx", "xlsx", "epub", "html", "htm", "txt", "md", "csv" })
            Assert.True(DocText.CanExtract("b." + ext), ext + " has a reader in this build");
        foreach (string ext in new[] { "doc", "xls", "ppt", "rtf", "odt", "odp", "ods" })
        {
            Assert.False(DocText.CanExtract("b." + ext), ext + " has no reader in this build");
            Assert.False(DocText.CanExtract("B." + ext.ToUpperInvariant()), ext + " must match whatever the case");
        }
    }

    [Fact]
    public void TheWorkingLoopStopsAtARowItCannotAdvancePast()
    {
        // A row whose failure-recording write ALSO throws is never dequeued, so the next
        // TakeNext hands back the same one. Without a guard the child spins on it at the 30 ms
        // working rest - about thirty passes a second, indefinitely, in the process that ships,
        // with Log.Once suppressing every line after the first. Reproduced the only way it
        // happens for real: the writes that record an outcome fail.
        string doc = Under("stuck.txt");
        File.WriteAllText(doc, "the quarterly lease agreement, signed and countersigned");

        using ContentDb db = Open();
        db.Enqueue("C", 1, doc, ResultKind.Document, "probe");
        using (var side = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = db.Path, Pooling = false }.ToString()))
        {
            side.Open();
            using SqliteCommand cmd = side.CreateCommand();
            cmd.CommandText = "DROP TABLE items";      // every write about a file now throws
            cmd.ExecuteNonQuery();
        }

        int passes = 0;
        var sw = Stopwatch.StartNew();
        Indexer.Loop(db, parentPid: 0, running: () => passes++ < 2, decoders: Dec());
        sw.Stop();
        _out.WriteLine($"two passes took {sw.ElapsedMilliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture)} ms");

        Assert.Equal("stuck", db.Get("indexer:state"));
        Assert.Equal("1", db.Get("indexer:failed"));    // handled once, not once per pass
        Assert.Equal(1, db.PendingCount());             // and left where a later run can retry it
    }

    [Fact]
    public void TheDeleteReasonTheInterfaceWritesIsTheOneTheIndexerAndTheQueueRead()
    {
        // One string crosses from the interface into the indexer and into TakeNext's ORDER BY.
        // A typo in any copy stops deletes jumping the queue and stops them being handled as
        // deletes at all - the row would be read as a file to index instead.
        string doc = Under("kept.txt");
        File.WriteAllText(doc, "the quarterly lease agreement, signed and countersigned");

        using ContentDb db = Open();
        db.Enqueue("C", 1, doc, ResultKind.Document, "probe");
        db.Enqueue("C", 2, Under("removed.txt"), ResultKind.Document, ContentDb.ReasonDelete);

        ContentDb.Pending? first = db.TakeNext();
        Assert.NotNull(first);
        Assert.Equal(ContentDb.ReasonDelete, first.Value.Reason);   // deletes are taken first

        var lines = new List<string>();
        Indexer.DrainOnce(db, lines.Add, Dec());
        Assert.Contains(lines, l => l.StartsWith("removed", StringComparison.Ordinal));
    }
}
