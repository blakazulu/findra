using Findra;
using Microsoft.Data.Sqlite;
using Xunit;

/// <summary>
/// "More like this": one file's own vectors are the question. Built against a vector store filled
/// by hand, with no model on disk - which is also the proof that no encoder is needed to ask it.
/// </summary>
public class MoreLikeThisTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-like-" + Guid.NewGuid().ToString("N"));

    public MoreLikeThisTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private ContentDb Open() => new(Path.Combine(_dir, "search.db"));
    private string VecPath => Path.Combine(_dir, "vectors.bin");
    private string At(string name) => Path.Combine(_dir, name);

    /// <summary>A unit vector at this cosine from the first axis, turned toward <paramref name="axis"/>.</summary>
    private static float[] Cos(float c, int axis = 1)
    {
        var v = new float[VectorStore.Dim];
        v[0] = c;
        v[axis] = MathF.Sqrt(1 - c * c);
        return v;
    }

    private sealed record Seg(int Kind, float[] Vector, double T0 = -1, string Text = "");

    /// <summary>Write every file's vectors, then every file's rows pointing at them.</summary>
    private void Fill(ContentDb db, params (string Path, ResultKind Kind, Seg[] Segs)[] files)
    {
        var rows = new List<List<ContentDb.Segment>>();
        using (var w = new VectorStore(VecPath, writer: true))
        {
            foreach (var f in files)
                rows.Add([.. f.Segs.Select(s => new ContentDb.Segment(s.Kind, s.T0, -1,
                    s.Vector.Length == 0 ? -1 : w.Append(s.Vector, (byte)s.Kind), s.Text))]);
            w.Flush();
        }
        ulong frn = 1;
        using var tx = db.Begin();
        for (int i = 0; i < files.Length; i++)
            db.Upsert("C", frn++, files[i].Path, files[i].Kind, 0, 100, ContentDb.StateIndexed, null, rows[i], tx);
        tx.Commit();
    }

    private SearchResults Like(ContentDb db, string path, string extra = "", int max = 20,
                               Func<string, bool, ResultMapper.Stat>? stat = null)
    {
        using var vectors = new VectorStore(VecPath);
        var semantic = new Semantic(vectors, text: null, image: null);
        return ContentBranch.Search(db, (SearchQuery.LikeQuery(path) + " " + extra).Trim(), max,
                                    stat: stat ?? ContentBranchTests.Here, semantic: semantic);
    }

    [Fact]
    public void APhotoFindsThePicturesAndMomentsThatLookLikeItAndNotItself()
    {
        using ContentDb db = Open();
        Fill(db,
            (At("a.jpg"), ResultKind.Photo, [new Seg(ContentDb.SegImage, Cos(1f))]),
            (At("b.jpg"), ResultKind.Photo, [new Seg(ContentDb.SegImage, Cos(0.9f))]),
            (At("c.jpg"), ResultKind.Photo, [new Seg(ContentDb.SegImage, Cos(0.5f))]),
            (At("clip.mp4"), ResultKind.Video, [new Seg(ContentDb.SegFrame, Cos(0.3f), 10), new Seg(ContentDb.SegFrame, Cos(0.8f), 100)]),
            // Exactly the photo's vector, in the passages' space: a document is never a picture.
            (At("notes.txt"), ResultKind.Document, [new Seg(ContentDb.SegText, Cos(1f), Text: "a page of notes")]));

        SearchResults r = Like(db, At("a.jpg"));

        Assert.Equal(["b.jpg", "clip.mp4"], r.Rows.Select(x => x.Name));
        Assert.Equal("looks like a.jpg", r.Rows[0].Why);
        Assert.Equal(ContentBranch.PictureLikeScore(0.9f), r.Rows[0].Score, 2);
        // The frame that looks most like it, and the moment to open the video at.
        Assert.Equal("a moment at 1:40 looks like a.jpg", r.Rows[1].Why);
        Assert.Equal(100, r.Rows[1].MomentSeconds);
        Assert.Equal("", r.Note);
        Assert.Equal(0, r.NamesMs);
        Assert.True(r.ContentReady);
    }

    [Fact]
    public void ADocumentFindsWhatReadsLikeItAndARecordingSaysWhen()
    {
        using ContentDb db = Open();
        Fill(db,
            (At("lease.txt"), ResultKind.Document, [new Seg(ContentDb.SegText, Cos(1f)), new Seg(ContentDb.SegText, Cos(1f, 2))]),
            (At("tenancy.txt"), ResultKind.Document, [new Seg(ContentDb.SegText, Cos(0.96f), Text: "the tenant shall pay")]),
            (At("call.m4a"), ResultKind.Audio, [new Seg(ContentDb.SegSpeech, Cos(0.94f), 67)]),
            (At("recipe.txt"), ResultKind.Document, [new Seg(ContentDb.SegText, Cos(0.85f))]),
            (At("photo.jpg"), ResultKind.Photo, [new Seg(ContentDb.SegImage, Cos(1f))]));

        SearchResults r = Like(db, At("lease.txt"));

        Assert.Equal(["tenancy.txt", "call.m4a"], r.Rows.Select(x => x.Name));
        Assert.Equal("reads like lease.txt", r.Rows[0].Why);
        Assert.Equal("the tenant shall pay", r.Rows[0].Excerpt);
        Assert.Equal("said around 1:07, reads like lease.txt", r.Rows[1].Why);
        Assert.Equal(67, r.Rows[1].MomentSeconds);
    }

    [Fact]
    public void TheGrammarBesideItFiltersWhatComesBack()
    {
        using ContentDb db = Open();
        Directory.CreateDirectory(At("Crete"));
        Fill(db,
            (At("a.jpg"), ResultKind.Photo, [new Seg(ContentDb.SegImage, Cos(1f))]),
            (At(@"Crete\b.jpg"), ResultKind.Photo, [new Seg(ContentDb.SegImage, Cos(0.9f))]),
            (At("clip.mp4"), ResultKind.Video, [new Seg(ContentDb.SegFrame, Cos(0.8f), 5)]));

        Assert.Equal(["clip.mp4"], Like(db, At("a.jpg"), "type:video").Rows.Select(x => x.Name));
        Assert.Equal(["b.jpg"], Like(db, At("a.jpg"), "-clip").Rows.Select(x => x.Name));
        Assert.Equal(["b.jpg"], Like(db, At("a.jpg"), "in:Crete").Rows.Select(x => x.Name));
        // And the finish every content answer takes: a file the disk no longer has is not offered.
        SearchResults gone = Like(db, At("a.jpg"), stat: (p, d) => p.EndsWith("b.jpg", StringComparison.Ordinal)
            ? ResultMapper.Stat.Missing : ContentBranchTests.Here(p, d));
        Assert.Equal(["clip.mp4"], gone.Rows.Select(x => x.Name));
    }

    [Fact]
    public void TheFileIsFoundAndLeftOutWhateverTheCaseOfItsPath()
    {
        using ContentDb db = Open();
        Fill(db,
            (At("a.jpg"), ResultKind.Photo, [new Seg(ContentDb.SegImage, Cos(1f))]),
            (At("b.jpg"), ResultKind.Photo, [new Seg(ContentDb.SegImage, Cos(0.9f))]));

        SearchResults r = Like(db, At("A.JPG").ToUpperInvariant());
        Assert.Equal(["b.jpg"], r.Rows.Select(x => x.Name));
    }

    [Fact]
    public void ItsOwnRowsCannotCrowdOutTheAnswer()
    {
        // A long video's own frames match it best of all. Asked for only the top few per question,
        // they would fill every place and leave nothing for the file that actually looks like it.
        using ContentDb db = Open();
        Fill(db,
            (At("long.mp4"), ResultKind.Video, [.. Enumerable.Range(0, 40).Select(i => new Seg(ContentDb.SegFrame, Cos(1f), i))]),
            (At("b.jpg"), ResultKind.Photo, [new Seg(ContentDb.SegImage, Cos(0.9f))]));

        Assert.Equal(["b.jpg"], Like(db, At("long.mp4"), max: 1).Rows.Select(x => x.Name));
    }

    [Fact]
    public void WhatIsMissingIsSaidRatherThanShownAsNoMatch()
    {
        using ContentDb db = Open();
        Fill(db,
            (At("a.jpg"), ResultKind.Photo, [new Seg(ContentDb.SegImage, Cos(1f))]),
            (At("far.jpg"), ResultKind.Photo, [new Seg(ContentDb.SegImage, Cos(0.2f))]),
            (At("unread.pdf"), ResultKind.Document, [new Seg(ContentDb.SegText, [], Text: "words only")]));

        Assert.Equal(ContentBranch.LikeNothingElse(pictures: true), Like(db, At("a.jpg")).Note);
        Assert.Equal(ContentBranch.LikeNotReadYet, Like(db, At("unread.pdf")).Note);
        Assert.Equal(ContentBranch.LikeNotReadYet, Like(db, At("never-seen.jpg")).Note);

        // No vector store at all: "Just names", or nothing installed that reads pictures or meaning.
        SearchResults none = ContentBranch.Search(db, SearchQuery.LikeQuery(At("a.jpg")), 20, stat: ContentBranchTests.Here, semantic: null);
        Assert.Empty(none.Rows);
        Assert.Equal(ContentBranch.LikeNothingToCompare, none.Note);
    }

    [Fact]
    public void WhetherAFileHasAnythingToCompareIsOneQuestion()
    {
        using ContentDb db = Open();
        Fill(db,
            (At("a.jpg"), ResultKind.Photo, [new Seg(ContentDb.SegImage, Cos(1f))]),
            (At("unread.pdf"), ResultKind.Document, [new Seg(ContentDb.SegText, [], Text: "words only")]));

        Assert.True(db.HasVectors(At("a.jpg")));
        Assert.True(db.HasVectors(At("a.jpg").ToUpperInvariant()));
        Assert.False(db.HasVectors(At("unread.pdf")));
        Assert.False(db.HasVectors(At("never-seen.jpg")));
    }

    [Fact]
    public void ThePathLookupSeeksRatherThanReadingEveryItem()
    {
        // "More like this" asks it for every highlighted file; the binary-collated path index
        // cannot answer a case-insensitive comparison, and without its own index this read every
        // row of the table.
        using (ContentDb db = Open()) { }
        using var c = new SqliteConnection("Data Source=" + Path.Combine(_dir, "search.db") + ";Pooling=False");
        c.Open();
        using SqliteCommand cmd = c.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN SELECT id FROM items WHERE path = $p COLLATE NOCASE LIMIT 1";
        cmd.Parameters.AddWithValue("$p", @"C:\a.jpg");
        using SqliteDataReader r = cmd.ExecuteReader();
        var plan = new List<string>();
        while (r.Read()) plan.Add(r.GetString(3));
        Assert.Contains(plan, p => p.Contains("items_path_nocase", StringComparison.Ordinal));
    }

    [Fact]
    public void AStoredRowComesBackAsAUnitVectorAndADeletedOneDoesNot()
    {
        long kept, gone;
        using (var w = new VectorStore(VecPath, writer: true))
        {
            kept = w.Append(Cos(0.6f), ContentDb.SegImage);
            gone = w.Append(Cos(0.8f), ContentDb.SegImage);
            w.Tombstone(gone);
            w.Flush();
        }
        using var store = new VectorStore(VecPath);
        float[] v = Assert.IsType<float[]>(store.VectorOf(kept));
        Assert.Equal(1f, System.Numerics.Tensors.TensorPrimitives.Dot(v, v), 2);
        Assert.Equal(0.6f, v[0], 2);
        Assert.Null(store.VectorOf(gone));
        Assert.Null(store.VectorOf(-1));
        Assert.Null(store.VectorOf(99));
    }

    [Fact]
    public void ALongFileIsAskedAboutByItsWholeLength()
    {
        List<int> all = [.. Enumerable.Range(0, 100)];
        List<int> taken = ContentBranch.Evenly(all, ContentBranch.LikeSamples);
        Assert.Equal(ContentBranch.LikeSamples, taken.Count);
        Assert.Equal(0, taken[0]);
        Assert.True(taken[^1] >= 90, $"the last one taken is {taken[^1]} of 100");
        Assert.Equal(taken.Distinct().Count(), taken.Count);
        Assert.Equal([1, 2, 3], ContentBranch.Evenly([1, 2, 3], ContentBranch.LikeSamples));
    }

    [Fact]
    public void EachKindOfMatchExplainsItselfInItsOwnWords()
    {
        ContentDb.SegmentHit Hit(int kind, double t0) => new(1, @"C:\x\y.mp4", ResultKind.Video, kind, t0, -1, "", 0);
        Assert.Equal("looks like a.jpg", ContentBranch.ToLike(Hit(ContentDb.SegImage, -1), 0.5f, "a.jpg").Why);
        Assert.Equal("a moment at 2:05 looks like a.jpg", ContentBranch.ToLike(Hit(ContentDb.SegFrame, 125), 0.5f, "a.jpg").Why);
        Assert.Equal("reads like a.pdf", ContentBranch.ToLike(Hit(ContentDb.SegText, -1), 0.5f, "a.pdf").Why);
        Assert.Equal("said around 1:02:03, reads like a.pdf", ContentBranch.ToLike(Hit(ContentDb.SegSpeech, 3723), 0.5f, "a.pdf").Why);
        Assert.Equal(-1, ContentBranch.ToLike(Hit(ContentDb.SegText, 12), 0.5f, "a.pdf").MomentSeconds);
    }
}
