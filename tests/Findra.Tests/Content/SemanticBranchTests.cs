using Findra;
using Xunit;

public class SemanticBranchTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-sem-" + Guid.NewGuid().ToString("N"));

    public SemanticBranchTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private ContentDb Open() => new(Path.Combine(_dir, "search.db"));
    private string VecPath => Path.Combine(_dir, "vectors.bin");

    private static float[] Axis(int i)
    {
        var v = new float[VectorStore.Dim];
        v[i] = 1f;
        return v;
    }

    /// <summary>An item with one segment pointing at one vector row.</summary>
    private static void Put(ContentDb db, ulong frn, string path, ResultKind kind, int segKind, long vec, string text)
    {
        using var tx = db.Begin();
        db.Upsert("C", frn, path, kind, 0, 100, ContentDb.StateIndexed, null,
                  [new ContentDb.Segment(segKind, -1, -1, vec, text)], tx);
        tx.Commit();
    }

    private static CapabilitySet Set(params Capability[] c) => new(new HashSet<Capability>(c));

    // ---- the score bands ----

    [Fact]
    public void ThePictureBandStretchesTheNarrowRangeTheModelActuallyUses()
    {
        // SigLIP-2 is a sigmoid model and its cosines sit LOW: unrelated is near 0 and
        // "obviously this" is around 0.10 to 0.12. Handing the raw cosine to the card - which
        // is what a straight port of the vector search does - scores every photo about 0.1, so
        // no photo ever ranks against anything and half of them tie.
        Assert.Equal(0f, ContentBranch.PhotoScore(0.05f), 3);
        Assert.Equal(0.92f, ContentBranch.PhotoScore(0.20f), 3);
        Assert.Equal(0.92f, ContentBranch.PhotoScore(0.90f), 3);   // clamped, never above the ceiling
        Assert.True(ContentBranch.PhotoScore(0.11f) > 0.3f);
    }

    [Fact]
    public void TheTextBandStartsWhereTheModelStopsSayingEverythingIsSimilar()
    {
        // e5 puts unrelated text near 0.75 and a paraphrase near 0.9. A floor at 0 would make
        // every document in the index a weak match for every query.
        Assert.Equal(0f, ContentBranch.TextScore(0.78f), 3);
        Assert.Equal(0.9f, ContentBranch.TextScore(0.90f), 3);
        Assert.Equal(0.9f, ContentBranch.TextScore(0.99f), 3);
    }

    // ---- meaning finds what words cannot ----

    [Fact]
    public void AFileFoundOnlyByMeaningIsInTheAnswer()
    {
        // The document never contains the word "lease", so the full-text branch cannot find it
        // and this test cannot pass by accident. A build with no vector branch returns nothing.
        using ContentDb db = Open();
        using (var w = new VectorStore(VecPath, writer: true)) { w.Append(Axis(3), ContentDb.SegText); w.Flush(); }
        Put(db, 1, Path.Combine(_dir, "tenancy.txt"), ResultKind.Document, ContentDb.SegText, 0,
            "the tenant shall pay the sum monthly in advance");

        using var vectors = new VectorStore(VecPath);
        var semantic = new Semantic(vectors, text: _ => Axis(3), image: null);

        SearchResults r = ContentBranch.Search(db, "lease", 10, semantic: semantic,
                                               installed: Set(Capability.Meaning));

        Assert.Single(r.Rows);
        Assert.Equal("tenancy.txt", r.Rows[0].Name);
        Assert.Contains("like it", r.Rows[0].Why, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoMeaningModelTheSameQueryFindsNothingAndOffersTheDownload()
    {
        // Spec §6, both halves: an absent capability contributes no candidates and is not an
        // error, AND the card offers it. A branch that throws on a null encoder fails the
        // first; one that says nothing fails the second.
        using ContentDb db = Open();
        using (var w = new VectorStore(VecPath, writer: true)) { w.Append(Axis(3), ContentDb.SegText); w.Flush(); }
        Put(db, 1, Path.Combine(_dir, "tenancy.txt"), ResultKind.Document, ContentDb.SegText, 0,
            "the tenant shall pay the sum monthly in advance");

        using var vectors = new VectorStore(VecPath);
        var semantic = new Semantic(vectors, text: null, image: null);

        SearchResults r = ContentBranch.Search(db, "lease", 10, semantic: semantic,
                                               installed: CapabilitySet.None);

        Assert.Empty(r.Rows);
        Assert.Contains("270 MB", r.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void WithNoSemanticStoreAtAllTheWordsStillAnswer()
    {
        // The free capability, through the same call. Passing null for the whole Semantic is
        // what the card does on a machine that took nothing, and it must be an ordinary answer.
        using ContentDb db = Open();
        Put(db, 1, Path.Combine(_dir, "notes.txt"), ResultKind.Document, ContentDb.SegText, -1,
            "the quarterly lease agreement and its deposit");

        SearchResults r = ContentBranch.Search(db, "deposit", 10, semantic: null, installed: CapabilitySet.None);

        Assert.Single(r.Rows);
    }

    [Fact]
    public void PicturesContributeNothingWhenTheirModelIsAbsentAndThatIsNotAnError()
    {
        using ContentDb db = Open();
        using (var w = new VectorStore(VecPath, writer: true)) { w.Append(Axis(1), ContentDb.SegImage); w.Flush(); }
        Put(db, 1, Path.Combine(_dir, "holiday.jpg"), ResultKind.Photo, ContentDb.SegImage, 0, "");
        Put(db, 2, Path.Combine(_dir, "notes.txt"), ResultKind.Document, ContentDb.SegText, -1,
            "the quarterly lease agreement");

        using var vectors = new VectorStore(VecPath);
        // The text query is the SAME vector the photo was stored under, deliberately. A correct
        // build still never returns the photo, because the text pass is restricted to the word
        // kinds and the picture pass has no encoder to run - but a build that lets the text
        // encoder stand in for the absent picture one scores that photo 1.0 and returns it. An
        // orthogonal vector here would be caught by the floor instead, and the null encoder would
        // go untested.
        var semantic = new Semantic(vectors, text: _ => Axis(1), image: null);   // no picture encoder

        SearchResults r = ContentBranch.Search(db, "lease", 10, semantic: semantic,
                                               installed: Set(Capability.Meaning));

        Assert.Single(r.Rows);                       // the document, and no photo
        Assert.Equal("notes.txt", r.Rows[0].Name);
    }

    [Fact]
    public void APictureThatMerelyResemblesTheQueryALittleIsNotAMatch()
    {
        // Below the floor, and it must not appear at all. Without the floor every photo in the
        // library is a weak match for every query, which is the state the source's comment
        // describes measuring its way out of.
        using ContentDb db = Open();
        using (var w = new VectorStore(VecPath, writer: true))
        {
            var faint = new float[VectorStore.Dim];
            faint[0] = 0.03f; faint[1] = 0.9995f;          // ~0.03 against Axis(0)
            VectorStore.Normalise(faint);
            w.Append(faint, ContentDb.SegImage);
            w.Flush();
        }
        Put(db, 1, Path.Combine(_dir, "holiday.jpg"), ResultKind.Photo, ContentDb.SegImage, 0, "");

        using var vectors = new VectorStore(VecPath);
        var semantic = new Semantic(vectors, text: null, image: _ => Axis(0));

        SearchResults r = ContentBranch.Search(db, "a sunset", 10, semantic: semantic,
                                               installed: Set(Capability.Photos));

        Assert.Empty(r.Rows);
    }

    [Fact]
    public void AFileThatMatchesBothWordsAndMeaningOutranksOneThatMatchesOnlyMeaning()
    {
        // Exact words are what the person typed. A file found both ways gets a bonus on top of
        // its vector score; dropping the bonus lets a paraphrase outrank the actual phrase.
        using ContentDb db = Open();
        using (var w = new VectorStore(VecPath, writer: true))
        {
            w.Append(Axis(3), ContentDb.SegText);      // row 0 - the one that says "lease"
            w.Append(Axis(3), ContentDb.SegText);      // row 1 - the paraphrase
            w.Flush();
        }
        Put(db, 1, Path.Combine(_dir, "both.txt"), ResultKind.Document, ContentDb.SegText, 0,
            "the lease agreement is signed");
        Put(db, 2, Path.Combine(_dir, "meaning-only.txt"), ResultKind.Document, ContentDb.SegText, 1,
            "the tenant shall pay monthly");

        using var vectors = new VectorStore(VecPath);
        var semantic = new Semantic(vectors, text: _ => Axis(3), image: null);

        SearchResults r = ContentBranch.Search(db, "lease", 10, semantic: semantic,
                                               installed: Set(Capability.Meaning));

        Assert.Equal(2, r.Rows.Count);
        Assert.Equal("both.txt", r.Rows[0].Name);
        Assert.True(r.Rows[0].Score > r.Rows[1].Score);
    }

    [Fact]
    public void OneRowPerFileEvenWhenBothBranchesFindTheSameFile()
    {
        using ContentDb db = Open();
        using (var w = new VectorStore(VecPath, writer: true)) { w.Append(Axis(3), ContentDb.SegText); w.Flush(); }
        Put(db, 1, Path.Combine(_dir, "both.txt"), ResultKind.Document, ContentDb.SegText, 0,
            "the lease agreement is signed");

        using var vectors = new VectorStore(VecPath);
        SearchResults r = ContentBranch.Search(db, "lease", 10,
                                               semantic: new Semantic(vectors, _ => Axis(3), null),
                                               installed: Set(Capability.Meaning));

        Assert.Single(r.Rows);
    }

    [Fact]
    public void AMomentInATranscriptCarriesTheTimeItWasSaid()
    {
        // A speech segment's answer has to be seekable: the row says when, and the card's stage
        // opens the file there. A transcript row with MomentSeconds of -1 is a search result
        // that makes somebody scrub through an hour of audio by hand.
        using ContentDb db = Open();
        using (var w = new VectorStore(VecPath, writer: true)) { w.Append(Axis(5), ContentDb.SegSpeech); w.Flush(); }
        using (var tx = db.Begin())
        {
            db.Upsert("C", 1, Path.Combine(_dir, "call.m4a"), ResultKind.Audio, 0, 100,
                      ContentDb.StateIndexed, null,
                      [new ContentDb.Segment(ContentDb.SegSpeech, 154.0, 172.0, 0, "we agreed on the deposit")], tx);
            tx.Commit();
        }

        using var vectors = new VectorStore(VecPath);
        SearchResults r = ContentBranch.Search(db, "deposit", 10,
                                               semantic: new Semantic(vectors, _ => Axis(5), null),
                                               installed: Set(Capability.Speech, Capability.Meaning));

        Assert.Single(r.Rows);
        Assert.Equal(154.0, r.Rows[0].MomentSeconds);
        Assert.Contains("2:34", r.Rows[0].Why, StringComparison.Ordinal);
        // "Around" belongs to the vector branch and "at" to the full-text one. This transcript is
        // found BOTH ways, so the whole word is the assertion: a build that REPLACES the vector
        // row with a fresh word row instead of raising its score says "said at" here. The two
        // assertions above cannot tell those apart, because this file has one segment and both
        // branches land on it - on a transcript whose matching chunk is not the one the words hit,
        // the same replacement silently loses the moment as well.
        Assert.Equal("said around 2:34", r.Rows[0].Why);
    }

    [Fact]
    public void TheGrammarStillAppliesToWhatMeaningFound()
    {
        // `lease ext:pdf` still means the pdf, on both branches. Skipping the filter on the
        // vector half makes the pill quietly ignore half the query language the card advertises.
        using ContentDb db = Open();
        using (var w = new VectorStore(VecPath, writer: true))
        {
            w.Append(Axis(3), ContentDb.SegText);
            w.Append(Axis(3), ContentDb.SegText);
            w.Flush();
        }
        Put(db, 1, Path.Combine(_dir, "a.txt"), ResultKind.Document, ContentDb.SegText, 0, "the tenant pays");
        Put(db, 2, Path.Combine(_dir, "b.pdf"), ResultKind.Document, ContentDb.SegText, 1, "the tenant pays");

        using var vectors = new VectorStore(VecPath);
        SearchResults r = ContentBranch.Search(db, "lease ext:pdf", 10,
                                               semantic: new Semantic(vectors, _ => Axis(3), null),
                                               installed: Set(Capability.Meaning));

        Assert.Single(r.Rows);
        Assert.Equal("b.pdf", r.Rows[0].Name);
    }

    [Fact]
    public void AnEmptyIndexStillSaysSoRatherThanOfferingADownload()
    {
        // Two different notes, and the wrong one is a lie. "Nothing indexed yet" is about the
        // machine; "this needs 270 MB" is about a capability. An index with nothing in it must
        // not be explained by a missing model.
        using ContentDb db = Open();
        SearchResults r = ContentBranch.Search(db, "lease", 10, semantic: null, installed: CapabilitySet.None);

        Assert.Empty(r.Rows);
        Assert.Contains("Nothing indexed yet", r.Note, StringComparison.Ordinal);
        Assert.DoesNotContain("MB", r.Note, StringComparison.Ordinal);
    }
}
