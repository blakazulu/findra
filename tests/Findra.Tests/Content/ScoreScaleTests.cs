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
}
