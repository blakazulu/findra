using Findra;

using Xunit;

/// <summary>
/// The two score scales, held to what was measured rather than to what was assumed.
///
/// <para>A content search runs two passes over two different models and puts their answers in one
/// list. The numbers only mean the same thing if each scale is anchored to its OWN band: the floor
/// just above what that model gives unrelated content, the span ending just past what it gives its
/// best real match. Anchored differently, the two kinds are not ranked against each other at all -
/// whichever scale is more generous simply wins.</para>
///
/// <para><b>Where these numbers come from.</b> One machine, one index of 6,165 real files, the
/// shipped models. The unrelated band is 31 samples of real documents scored against queries with
/// nothing to do with them; the real band is the best segment of the top document for seven
/// queries whose answers were checked by hand. The photo figures come from the same index. They
/// belong to these models on this corpus - a floor belongs to the model it was measured on, and
/// re-measuring is the price of changing either model.</para>
/// </summary>
public class ScoreScaleTests
{
    // ---- measured, September 2026, 6,165 files -------------------------------------------------
    private const float UnrelatedTextMax = 0.798f;   // an advertising doc against "sourdough bread"
    private const float RealTextMin = 0.838f;        // weakest of seven hand-checked best answers
    private const float RealTextMax = 0.868f;        // strongest of them

    private const float UnrelatedPhotoMax = 0.093f;  // a screenshot against "hebrew poetry"
    private const float RealPhotoMin = 0.105f;
    private const float RealPhotoMax = 0.150f;       // a code-editor screenshot, "a screenshot of a code editor"

    [Fact]
    public void UnrelatedTextScoresNothingAtAll()
    {
        // The defect this replaces: the floor sat at 0.780, which was the MEAN of the unrelated
        // band. Half of every unrelated document in the index cleared it and arrived carrying a
        // real number, and a floor inside the noise removes the evidence of its own wrongness.
        Assert.Equal(0f, ContentBranch.TextScore(UnrelatedTextMax));
        Assert.Equal(0f, ContentBranch.TextScore(0.78f));
    }

    [Fact]
    public void ARealTextMatchStillClearsTheFloorWithRoom()
    {
        // The other half of choosing a floor. It sits in the empty band between 0.798 and 0.838,
        // nearer the bottom of it, because losing a real answer costs more than admitting a weak
        // one that the words branch would have found anyway.
        Assert.True(ContentBranch.TextScore(RealTextMin) > 0.3f,
                    $"the weakest hand-checked answer scored {ContentBranch.TextScore(RealTextMin)}");
    }

    [Fact]
    public void EachScaleEndsJustPastItsOwnBestRealMatch()
    {
        // "As good as this kind of match gets" has to mean the same number on both scales, or the
        // ranking between them is decided by the arithmetic instead of by the match.
        Assert.True(ContentBranch.TextScore(RealTextMax) >= 0.95f * ContentBranch.TextCeiling,
                    $"the best document reached only {ContentBranch.TextScore(RealTextMax)} of {ContentBranch.TextCeiling}");
        Assert.True(ContentBranch.PhotoScore(RealPhotoMax) >= 0.95f * ContentBranch.PhotoCeiling,
                    $"the best picture reached only {ContentBranch.PhotoScore(RealPhotoMax)} of {ContentBranch.PhotoCeiling}");
    }

    [Fact]
    public void TheBestOfEachKindLandsInTheSamePlace()
    {
        // The failure in one line. Measured before this change: a picture matching as well as a
        // picture can scored 0.92 while a document matching as well as a document can scored 0.66,
        // so a screenshot out-ranked the one document that answered the question.
        float text = ContentBranch.TextScore(RealTextMax);
        float photo = ContentBranch.PhotoScore(RealPhotoMax);
        Assert.True(Math.Abs(text - photo) <= 0.06f,
                    $"the best document scores {text} and the best picture {photo}");
    }

    [Fact]
    public void AnUnrelatedPictureIsWorthAlmostNothing()
    {
        // The photo floor was measured first and is left alone: it already sits just above the
        // unrelated band. The one sample that clears it does so by 0.003 and earns almost nothing.
        Assert.True(ContentBranch.PhotoScore(UnrelatedPhotoMax) < 0.06f,
                    $"an unrelated picture scored {ContentBranch.PhotoScore(UnrelatedPhotoMax)}");
        Assert.True(ContentBranch.PhotoScore(RealPhotoMin) > 0.1f);
    }

    [Fact]
    public void BothSpansAreTheWidthOfTheirOwnBand()
    {
        // Stated as an invariant rather than as two numbers: the span is the distance from the
        // floor to just past the best real match. Whoever changes a model has to re-measure both
        // ends, and this is what will fail if they change one and not the other.
        Assert.InRange(ContentBranch.TextFloor + ContentBranch.TextSpan, RealTextMax, RealTextMax + 0.02f);
        Assert.InRange(ContentBranch.PhotoFloor + ContentBranch.PhotoSpan, RealPhotoMax - 0.001f, RealPhotoMax + 0.02f);
    }

    [Fact]
    public void TheFloorsSitInTheEmptyBandBetweenNoiseAndAnswers()
    {
        Assert.InRange(ContentBranch.TextFloor, UnrelatedTextMax, RealTextMin);
        Assert.InRange(ContentBranch.PhotoFloor, UnrelatedPhotoMax - 0.01f, RealPhotoMin);
    }

    // ---- "More like this": a file against a file, measured 30 September 2026 ----------------
    //
    // One machine. Pictures: Findra's own vision encoder and preprocessing over 675 real pictures
    // (375 family photographs from 15 events, 120 stock photographs, 120 screenshots, 40 interface
    // icons, 10 pictures kept twice). Passages: the real index's 72,046 embedded passages from
    // 4,600 documents, each source compared the way ContentBranch compares it - sixteen passages
    // at most, the best pair per file - over 90 sampled sources, 55 of them judged by eye.

    private const float CopyLike = 1.000f;                 // a picture or a document kept twice
    private const float SameEventPhotoMedian = 0.718f;     // photographs of one event
    private const float OtherEventPhotoMedian = 0.610f;    // photographs of different events
    private const float PhotoVsScreenshotP99 = 0.644f;     // a photograph against a screenshot
    private const float UnrelatedStockSeen = 0.698f;       // banknotes beside an office desk
    private const float RelatedStockSeen = 0.719f;         // an office chair beside an office desk

    private const float UnrelatedPassageMedian = 0.841f;   // the median file, per source
    private const float UnrelatedPassageP99 = 0.905f;      // the 99th percentile file, per source
    private const float MostlyRelatedPassage = 0.92f;      // where nearly every file judged was on topic
    private const float BestSiblingPassage = 0.955f;       // median best file that is not a copy

    [Fact]
    public void AFileAgainstAFileIsNotHeldToTheTypedQueryFloors()
    {
        // The typed-query floors would let nearly everything in: two passages share far more than
        // a query and a passage do, and two pictures sit far higher than a sentence and a picture.
        Assert.True(ContentBranch.TextFloor < UnrelatedPassageMedian);
        Assert.True(ContentBranch.PhotoFloor < PhotoVsScreenshotP99);
        Assert.True(ContentBranch.PassageLikeFloor > UnrelatedPassageMedian);
        Assert.True(ContentBranch.PictureLikeFloor > OtherEventPhotoMedian);
    }

    [Fact]
    public void ThePictureFloorKeepsScreenshotsAwayFromPhotographsAndKeepsTheSameScene()
    {
        Assert.Equal(0f, ContentBranch.PictureLikeScore(PhotoVsScreenshotP99));
        Assert.Equal(0f, ContentBranch.PictureLikeScore(OtherEventPhotoMedian));
        Assert.Equal(0f, ContentBranch.PictureLikeScore(UnrelatedStockSeen));
        Assert.True(ContentBranch.PictureLikeScore(RelatedStockSeen) > 0f);
        Assert.True(ContentBranch.PictureLikeScore(SameEventPhotoMedian) > 0f);
    }

    [Fact]
    public void ThePassageFloorSitsAboveTheNoiseAndBelowWhatWasOnTopic()
    {
        Assert.Equal(0f, ContentBranch.PassageLikeScore(UnrelatedPassageMedian));
        Assert.InRange(ContentBranch.PassageLikeFloor, UnrelatedPassageP99, MostlyRelatedPassage);
        Assert.True(ContentBranch.PassageLikeScore(BestSiblingPassage) > 0.3f);
    }

    [Fact]
    public void BothLikeScalesEndAtACopyTheirBestRealMatch()
    {
        // A copy is the best real match a file can have, so each scale ends there, and a close
        // sibling lands near the middle on both.
        Assert.Equal(CopyLike, ContentBranch.PictureLikeFloor + ContentBranch.PictureLikeSpan, 3);
        Assert.Equal(CopyLike, ContentBranch.PassageLikeFloor + ContentBranch.PassageLikeSpan, 3);
        Assert.Equal(ContentBranch.PhotoCeiling, ContentBranch.PictureLikeScore(CopyLike), 3);
        Assert.Equal(ContentBranch.TextCeiling, ContentBranch.PassageLikeScore(CopyLike), 3);
        Assert.InRange(ContentBranch.PassageLikeScore(BestSiblingPassage), 0.3f, 0.6f);
    }
}
