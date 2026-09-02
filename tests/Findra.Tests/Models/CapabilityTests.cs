using Findra;

using Xunit;

/// <summary>
/// The capability graph and every number that comes out of it. Spec §6: the capabilities are not
/// peers, and every size shown anywhere is the MARGINAL one given what is already selected.
/// </summary>
public class CapabilityTests
{
    private static CapabilitySet Set(params Capability[] c) => new(new HashSet<Capability>(c));

    // ---- the graph ----

    [Fact]
    public void PhotosNeedNothingButTheirOwnThreeFiles()
    {
        Assert.Equal([Capability.Photos], Capabilities.Close([Capability.Photos]).Order().ToArray());
        Assert.Equal(
            ["siglip2-vision.onnx", "siglip2-text-q.onnx", "siglip2.spm"],
            Capabilities.OwnModels(Capability.Photos).Select(m => m.File).ToArray());
    }

    [Fact]
    public void SpeechPullsInTheDocumentModelsBecauseATranscriptIsADocument()
    {
        // Spec §6: a transcript is embedded and searched exactly like a document, so taking
        // Speech takes the same e5 pair "meaning in documents" uses. A closure that hands back
        // what it was given passes nothing here.
        IReadOnlySet<Capability> closed = Capabilities.Close([Capability.Speech]);
        Assert.Contains(Capability.Speech, closed);
        Assert.Contains(Capability.Meaning, closed);
        Assert.Equal(2, closed.Count);
    }

    [Fact]
    public void HebrewCannotBeTakenWithoutTheGeneralModelItSecondPasses()
    {
        // Hebrew is a SECOND PASS, never an alternative: turbo runs first for language
        // detection and only the files it calls Hebrew are re-run. A closure that walks one
        // level - the obvious first implementation - returns {Hebrew, Speech} and misses the
        // e5 pair that Speech itself drags in, which is a download set that cannot work.
        IReadOnlySet<Capability> closed = Capabilities.Close([Capability.Hebrew]);

        Assert.Equal([Capability.Meaning, Capability.Speech, Capability.Hebrew],
                     closed.OrderBy(c => (int)c).ToArray());
        Assert.Equal(3, closed.Count);
    }

    [Fact]
    public void ClosingAnAlreadyClosedSetChangesNothing()
    {
        // Idempotence, because the closure runs at every UI click and at every startup, and a
        // closure that grows on each pass would eventually select everything.
        IReadOnlySet<Capability> once = Capabilities.Close([Capability.Hebrew, Capability.Photos]);
        IReadOnlySet<Capability> twice = Capabilities.Close(once);
        Assert.Equal(once.OrderBy(c => (int)c), twice.OrderBy(c => (int)c));
    }

    [Fact]
    public void DroppingSomethingDropsWhateverDependedOnIt()
    {
        // Untick Speech with Hebrew ticked and Hebrew must go too. A naive Remove leaves a
        // selection that asks for the 1.5 GB fine-tune with no general model to detect
        // language with - a download set that installs and then does nothing.
        IReadOnlySet<Capability> after = Capabilities.Drop(
            [Capability.Meaning, Capability.Speech, Capability.Hebrew], Capability.Speech);
        Assert.Equal([Capability.Meaning], after.ToArray());
    }

    [Fact]
    public void DroppingSomethingLeavesWhatMerelySharesFilesWithIt()
    {
        // Speech and Meaning share the e5 pair, but Meaning does not DEPEND on Speech. Untick
        // Speech and documents keep their meaning - and keep their models.
        IReadOnlySet<Capability> after = Capabilities.Drop(
            [Capability.Meaning, Capability.Speech], Capability.Speech);
        Assert.Equal([Capability.Meaning], after.ToArray());
        Assert.Equal(283_639_807L, Capabilities.TotalBytes(after));
    }

    // ---- the arithmetic ----

    [Fact]
    public void TheSizeBesideARowIsWhatItAddsToWhatIsAlreadyChosen()
    {
        // The whole reason the spec forbids a fixed per-row table: Speech costs 818 MB on its
        // own and 547 MB once documents have already brought the e5 pair in. A fixed table
        // shows one of those two numbers and makes the total visibly fail to add up.
        Assert.Equal(857_630_309L, Capabilities.MarginalBytes(Capability.Speech, []));
        Assert.Equal(573_990_502L, Capabilities.MarginalBytes(Capability.Speech, [Capability.Meaning]));
        Assert.Equal("818 MB", Sizes.Human(Capabilities.MarginalBytes(Capability.Speech, [])));
        Assert.Equal("547 MB", Sizes.Human(Capabilities.MarginalBytes(Capability.Speech, [Capability.Meaning])));
    }

    [Fact]
    public void TheMarginalCostOfSomethingAlreadyChosenIsNothing()
    {
        Assert.Equal(0L, Capabilities.MarginalBytes(Capability.Photos, [Capability.Photos]));
        Assert.Equal(0L, Capabilities.MarginalBytes(Capability.Meaning, [Capability.Speech]));
    }

    [Fact]
    public void HebrewsMarginalCostIsTheFineTuneAloneOnceSpeechIsThere()
    {
        Assert.Equal(1_624_558_796L, Capabilities.MarginalBytes(Capability.Hebrew, [Capability.Speech]));
        // and from nothing it is the fine-tune plus everything Speech drags in
        Assert.Equal(1_624_558_796L + 857_630_309L, Capabilities.MarginalBytes(Capability.Hebrew, []));
    }

    [Fact]
    public void ATotalCountsAModelSharedByTwoCapabilitiesOnce()
    {
        // Adding the numbers shown beside the rows is the arithmetic a person does in their
        // head and it is WRONG here, because Meaning and Speech share 270 MB. The total is the
        // union of the files, not the sum of the rows.
        Assert.Equal(857_630_309L, Capabilities.TotalBytes([Capability.Meaning, Capability.Speech]));
        Assert.NotEqual(283_639_807L + 857_630_309L,
                        Capabilities.TotalBytes([Capability.Meaning, Capability.Speech]));
    }

    [Fact]
    public void EverythingIsTheNumberOnTheReadme()
    {
        Assert.Equal(3_141_848_265L, Capabilities.TotalBytes(Capabilities.All));
        Assert.Equal("2.93 GB", Sizes.Human(Capabilities.TotalBytes(Capabilities.All)));
    }

    // ---- the presets ----

    [Fact]
    public void TheThreePresetsAreTheOnesOnTheFirstScreen()
    {
        Assert.Empty(Presets.JustNames);
        Assert.Equal(0L, Capabilities.TotalBytes(Presets.JustNames));

        Assert.Equal([Capability.Photos, Capability.Meaning],
                     Presets.Recommended.OrderBy(c => (int)c).ToArray());
        Assert.Equal("900 MB", Sizes.Human(Capabilities.TotalBytes(Presets.Recommended)));

        Assert.Equal(4, Presets.Everything.Count);
        Assert.Equal("2.93 GB", Sizes.Human(Capabilities.TotalBytes(Presets.Everything)));
    }

    [Fact]
    public void ASelectionThatIsNoPresetIsCustom()
    {
        Assert.Equal(Preset.Recommended, Presets.Match(Presets.Recommended));
        Assert.Equal(Preset.JustNames, Presets.Match(Presets.JustNames));
        Assert.Equal(Preset.Everything, Presets.Match(Presets.Everything));
        Assert.Equal(Preset.Custom, Presets.Match(new HashSet<Capability> { Capability.Photos }));
    }

    // ---- what a capability covers ----

    [Fact]
    public void EnablingACapabilityCoversExactlyTheKindsItCanRead()
    {
        // This is what a newly installed capability re-queues. A capability that claims every
        // kind re-indexes a finished disk, which spec §2a calls the worst thing this product
        // can do to someone.
        Assert.Equal([(int)ResultKind.Photo, (int)ResultKind.Video], Capabilities.KindsCovered(Capability.Photos));
        Assert.Equal([(int)ResultKind.Document], Capabilities.KindsCovered(Capability.Meaning));
        Assert.Equal([(int)ResultKind.Audio, (int)ResultKind.Video], Capabilities.KindsCovered(Capability.Speech));
        Assert.Equal([(int)ResultKind.Audio, (int)ResultKind.Video], Capabilities.KindsCovered(Capability.Hebrew));
    }

    [Fact]
    public void NoCapabilityClaimsAKindWithNoContentToRead()
    {
        foreach (Capability c in Capabilities.All)
            foreach (int k in Capabilities.KindsCovered(c))
                Assert.True(FileKinds.HasContent((ResultKind)k),
                    $"{c} claims to cover {(ResultKind)k}, which has nothing inside it to read");
    }

    // ---- the Hebrew row ----

    [Theory]
    [InlineData(new[] { "en-US" }, false)]
    [InlineData(new string[0], false)]
    [InlineData(new[] { "th-TH", "nb-NO" }, false)]   // a substring match on "he" says true here
    [InlineData(new[] { "en-US", "he-IL" }, true)]
    [InlineData(new[] { "he" }, true)]
    [InlineData(new[] { "HE-il" }, true)]
    public void HebrewIsOfferedOnlyWhereHebrewIsInstalled(string[] tags, bool want)
        => Assert.Equal(want, Capabilities.HebrewIsOffered(tags));

    // ---- the offer on the card ----

    [Fact]
    public void AQueryForPicturesOffersThePictureCapabilityAndItsPrice()
    {
        Offer? o = Capabilities.OfferFor(new SearchQuery("sunset type:photo"), CapabilitySet.None);
        Assert.NotNull(o);
        Assert.Equal(Capability.Photos, o!.Value.Capability);
        Assert.Equal(659_659_160L, o.Value.MarginalBytes);
        Assert.Contains("629 MB", o.Value.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void NothingIsOfferedForACapabilityThatIsAlreadyInstalled()
    {
        // The control that stops an unconditional offer passing the test above. A pill that
        // asks a paying customer to buy what they already own is worse than silence.
        Assert.Null(Capabilities.OfferFor(new SearchQuery("sunset type:photo"), Set(Capability.Photos)));
    }

    [Fact]
    public void AQueryForSoundOffersSpeechAtWhatItWouldActuallyCostThisMachine()
    {
        // Marginal again: on a machine that already has documents' meaning, Speech is 547 MB
        // and the offer must say so rather than quoting the standalone 818.
        Offer? bare = Capabilities.OfferFor(new SearchQuery("what she said type:audio"), CapabilitySet.None);
        Offer? withDocs = Capabilities.OfferFor(new SearchQuery("what she said type:audio"), Set(Capability.Meaning));
        Assert.Equal(Capability.Speech, bare!.Value.Capability);
        Assert.Contains("818 MB", bare.Value.Text, StringComparison.Ordinal);
        Assert.Contains("547 MB", withDocs!.Value.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOrdinaryWordQueryOffersMeaningAndNotThePictureModels()
    {
        Offer? o = Capabilities.OfferFor(new SearchQuery("the lease"), CapabilitySet.None);
        Assert.Equal(Capability.Meaning, o!.Value.Capability);
    }

    [Fact]
    public void WithEverythingInstalledThereIsNothingToOffer()
    {
        var everything = new CapabilitySet(Presets.Everything);
        Assert.Null(Capabilities.OfferFor(new SearchQuery("sunset type:photo"), everything));
        Assert.Null(Capabilities.OfferFor(new SearchQuery("the lease"), everything));
        Assert.Null(Capabilities.OfferFor(new SearchQuery("said type:audio"), everything));
    }

    [Fact]
    public void HebrewIsNeverOfferedFromTheCard()
    {
        // It is a refinement of a capability somebody already chose, on a machine whose
        // language list says it is worth having. Offering a 1.5 GB download off the back of a
        // query is not a decision to make in a search box.
        foreach (string q in new[] { "שלום", "type:audio שלום", "shalom" })
            Assert.NotEqual(Capability.Hebrew,
                Capabilities.OfferFor(new SearchQuery(q), CapabilitySet.None)?.Capability);
    }

    [Fact]
    public void AnInstalledSetIsReadFromTheFilesOnDiskAndNotFromASetting()
    {
        // What is installed is a fact about the disk. Reading it from config.json would let a
        // hand-edited file claim a capability whose 1.5 GB is not there, and every load would
        // then fail at the first file a query touched.
        //
        // Meaning rather than Photos: the rule is the same and the fixture is 254 MB instead of
        // 603 MB. See Fill below.
        string dir = Path.Combine(Path.GetTempPath(), "findra-caps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Assert.False(CapabilitySet.Installed(dir).Has(Capability.Meaning));
            foreach (Model m in Capabilities.OwnModels(Capability.Meaning))
                Fill(m, dir);
            Assert.True(CapabilitySet.Installed(dir).Has(Capability.Meaning));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void ACapabilityWhoseFilesArePartlyThereIsNotInstalled()
    {
        // One of the two e5 files is not "meaning works". An Any() here rather than an All()
        // would light the capability up and then fail on the first query.
        string dir = Path.Combine(Path.GetTempPath(), "findra-caps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Fill(ModelStore.E5Spm, dir);          // the small half only
            Assert.False(CapabilitySet.Installed(dir).Has(Capability.Meaning));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    [Fact]
    public void SpeechIsNotInstalledWhileTheDocumentModelsItNeedsAreMissing()
    {
        // Installed-ness follows the same graph the download does. A machine holding half the e5
        // pair and nothing else cannot answer a speech search, because a transcript is searched
        // as a document - and it must not report Speech as ready.
        string dir = Path.Combine(Path.GetTempPath(), "findra-caps-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            Fill(ModelStore.E5Spm, dir);
            Assert.False(CapabilitySet.Installed(dir).Has(Capability.Speech));
            Assert.False(CapabilitySet.Installed(dir).Has(Capability.Meaning));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }

    /// <summary>
    /// A file just long enough to count as one of its model's.
    ///
    /// <para><c>SetLength</c> allocates the clusters on NTFS, so a fixture built on a large
    /// model's floor writes hundreds of megabytes of temporary disk for an assertion about a
    /// boolean - and on a small or full disk <c>dotnet test</c> then fails for a reason nobody
    /// connects to this test. The three tests above prove their rules with the e5 pair (254 MB
    /// between them, and 4 MB for the two that need only one file); the SigLIP trio would cost
    /// 603 MB apiece for exactly the same assertions.</para>
    /// </summary>
    private static void Fill(Model m, string dir)
    {
        using var fs = new FileStream(ModelStore.PathOf(m, dir), FileMode.Create);
        fs.SetLength(m.MinBytes);
    }
}
