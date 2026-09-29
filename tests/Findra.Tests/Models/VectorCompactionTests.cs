using Findra;
using Xunit;

/// <summary>
/// The vector file only grows: a deleted or replaced file's rows are zeroed in place and stay.
/// Compaction copies the rows that are still used into a new file and renumbers what points at
/// them. A memory-mapped file cannot be shrunk or replaced while another process has it mapped,
/// so the copy goes under a new name and the index records which name is current.
/// </summary>
public sealed class VectorCompactionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-compact-" + Guid.NewGuid().ToString("N"));

    public VectorCompactionTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private ContentDb Open() => new(Path.Combine(_dir, "search.db"));
    private string FirstFile => Path.Combine(_dir, "vectors.bin");

    private static float[] Axis(int i)
    {
        var v = new float[VectorStore.Dim];
        v[i] = 1f;
        return v;
    }

    /// <summary>Ten rows on ten axes. Two files own three of them; the other seven are dead,
    /// either zeroed by a delete or left behind with nothing pointing at them.</summary>
    private static VectorStore Filled(ContentDb db, string path)
    {
        var w = new VectorStore(path, writer: true);
        for (int i = 0; i < 10; i++) w.Append(Axis(i), i == 8 ? (byte)ContentDb.SegImage : (byte)ContentDb.SegText);
        foreach (long dead in new long[] { 0, 2, 3, 5, 6 }) w.Tombstone(dead);   // 7 and 9: nothing points at them
        w.Flush();
        using var tx = db.Begin();
        db.Upsert("C", 1, @"C:\a\lease.txt", ResultKind.Document, 1, 1, ContentDb.StateIndexed, null,
                  [new ContentDb.Segment(ContentDb.SegText, -1, -1, 1, "the lease"),
                   new ContentDb.Segment(ContentDb.SegText, -1, -1, 4, "the deposit")], tx);
        db.Upsert("C", 2, @"C:\a\sunset.jpg", ResultKind.Photo, 1, 1, ContentDb.StateIndexed, null,
                  [new ContentDb.Segment(ContentDb.SegImage, -1, -1, 8, "")], tx);
        tx.Commit();
        return w;
    }

    [Theory]
    [InlineData(100_000, 70_000, true)]    // 30,000 dead: past both bars
    [InlineData(100_000, 80_000, false)]   // 20,000 dead, but only a fifth of the file
    [InlineData(30_000, 5_000, true)]
    [InlineData(10_000, 0, false)]         // all of it dead, and still not worth a copy
    public void ItIsWorthCopyingOnlyWhenEnoughOfTheFileIsDead(long rows, long live, bool worth)
        => Assert.Equal(worth, VectorCompaction.Worth(rows, live));

    [Fact]
    public void EveryPassageIsFoundAtTheSameScoreThroughTheNewFile()
    {
        using ContentDb db = Open();
        VectorStore writer = Filled(db, FirstFile);

        VectorStore next = VectorCompaction.Run(db, writer, minDead: 1);
        writer.Dispose();
        using (next)
        {
            Assert.NotEqual(FirstFile, db.VectorsPath());
            Assert.Equal(3, next.Count);

            using var reader = new VectorStore(db.VectorsPath());
            foreach ((int axis, string text) in new[] { (1, "the lease"), (4, "the deposit"), (8, "") })
            {
                VectorStore.Match best = reader.Search(Axis(axis), 1, [])[0];
                Assert.True(best.Score > 0.99f, $"axis {axis} scored {best.Score} against its own row");
                Assert.Equal(text, Assert.Single(db.SegmentsByVec([best.Row])).Text);
            }
        }
    }

    [Fact]
    public void AFreshAppendLandsInTheNewFileAfterTheRowsItKept()
    {
        using ContentDb db = Open();
        VectorStore writer = Filled(db, FirstFile);

        using VectorStore next = VectorCompaction.Run(db, writer, minDead: 1);
        writer.Dispose();

        Assert.Equal(3, next.Append(Axis(20), ContentDb.SegText));
    }

    [Fact]
    public void TooLittleDeadIsLeftAlone()
    {
        using ContentDb db = Open();
        using VectorStore writer = Filled(db, FirstFile);

        VectorStore same = VectorCompaction.Run(db, writer);   // the real bars: 7 dead rows is nothing

        Assert.Same(writer, same);
        Assert.Equal(FirstFile, db.VectorsPath());
        Assert.Single(Directory.GetFiles(_dir, "vectors*.bin"));
    }

    [Fact]
    public void AnOldFileIsDeletedOnceNothingHasItOpen()
    {
        using ContentDb db = Open();
        VectorStore writer = Filled(db, FirstFile);
        var stillReading = new VectorStore(FirstFile);   // a card that has not searched since

        using VectorStore next = VectorCompaction.Run(db, writer, minDead: 1);
        writer.Dispose();

        VectorCompaction.DeleteStale(db);
        Assert.True(File.Exists(FirstFile), "a file another reader has mapped cannot go yet");

        stillReading.Dispose();
        VectorCompaction.DeleteStale(db);
        Assert.False(File.Exists(FirstFile));
        Assert.False(File.Exists(FirstFile + ".kinds"));
        Assert.True(File.Exists(db.VectorsPath()));
        Assert.True(File.Exists(db.VectorsPath() + ".kinds"));
    }

    [Fact]
    public void TheIndexerWritesToTheNewFileOnceItHasCompacted()
    {
        // At the real bars, so the store is just past them: 21,000 rows, 1,000 of them in use.
        using ContentDb db = Open();
        var writer = new VectorStore(FirstFile, writer: true);
        for (int i = 0; i < 21_000; i++) writer.Append(Axis(i % VectorStore.Dim), ContentDb.SegText);
        writer.Flush();
        using (var tx = db.Begin())
        {
            db.Upsert("C", 1, @"C:\a\book.txt", ResultKind.Document, 1, 1, ContentDb.StateIndexed, null,
                      [.. Enumerable.Range(0, 1_000).Select(i => new ContentDb.Segment(ContentDb.SegText, -1, -1, i, "p" + i))], tx);
            tx.Commit();
        }
        using var decoders = new Decoders(() => CapabilitySet.None, writer, modelDir: _dir);

        Assert.True(decoders.Compact(db));
        decoders.Release([0]);
        decoders.Flush();

        Assert.Equal(255, VectorStore.KindsOnDisk(db.VectorsPath())[0]);
        Assert.Equal(1_000, VectorStore.KindsOnDisk(db.VectorsPath()).Length);
        writer.Dispose();   // the caller's store stays the caller's
        VectorCompaction.DeleteStale(db);
        Assert.False(File.Exists(FirstFile));
    }

    [Fact]
    public void ACopyThatNeverReachedTheIndexIsCleanedUp()
    {
        // The process died between writing the copy and recording it. The index still names the
        // old file, which is intact; the copy is only in the way.
        using ContentDb db = Open();
        using (var w = new VectorStore(FirstFile, writer: true)) { w.Append(Axis(0), 1); w.Flush(); }
        string orphan = Path.Combine(_dir, "vectors-20260101000000000.bin");
        File.WriteAllBytes(orphan, [1, 2, 3]);
        File.WriteAllBytes(orphan + ".kinds", [1]);

        VectorCompaction.DeleteStale(db);

        Assert.False(File.Exists(orphan));
        Assert.True(File.Exists(FirstFile));
    }

    [Fact]
    public void ASearchSessionOpenedBeforeACompactionFollowsItToTheNewFile()
    {
        // A card holds its session for as long as it is open. Asking the old file with the new
        // numbering would hand every match to the wrong passage.
        using ContentDb db = Open();
        VectorStore writer = Filled(db, FirstFile);
        using var semantic = Semantic.For(new CapabilitySet(new HashSet<Capability> { Capability.Meaning }), FirstFile)!;
        semantic.Supply(QueryEncoder.Words, _ => Axis(4), new MemoryStream());

        using VectorStore next = VectorCompaction.Run(db, writer, minDead: 1);
        writer.Dispose();

        SearchResults r = ContentBranch.Search(db, "money held against damage", 10,
            stat: ContentBranchTests.Here, semantic: semantic);

        SearchResult row = Assert.Single(r.Rows);
        Assert.Equal(@"C:\a\lease.txt", row.Path);
        Assert.Contains("deposit", row.Excerpt, StringComparison.Ordinal);
    }
}
