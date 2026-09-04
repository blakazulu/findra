using Findra;
using Xunit;

/// <summary>
/// Where a picture stops being unrelated.
///
/// <para>The floor was 0.05 and that is inside the noise. Measured on a real 3,097-picture library
/// with the query "headphones": the two images that actually showed headphones scored 0.130 and
/// 0.132, ten unrelated screenshots scored 0.03 to 0.066, and the band between was empty. A floor
/// of 0.05 admitted the top of the noise cluster and called it a match.</para>
/// </summary>
public class PhotoFloorTests
{
    // The measurement the floor is set from. Kept as data rather than prose so that moving the
    // floor without re-measuring fails here rather than passing quietly.
    private const float RealMatchLow = 0.130f;    // an actual picture of headphones
    private const float WorstNoise = 0.066f;      // the highest-scoring unrelated screenshot

    [Fact]
    public void TheFloorSitsInTheGapRatherThanInTheNoise()
    {
        Assert.True(ContentBranch.PhotoFloor > WorstNoise,
            $"the floor {ContentBranch.PhotoFloor} admits noise measured at {WorstNoise}");
        Assert.True(ContentBranch.PhotoFloor < RealMatchLow,
            $"the floor {ContentBranch.PhotoFloor} rejects a real match measured at {RealMatchLow}");
    }

    [Fact]
    public void EveryUnrelatedImageMeasuredIsRejectedAndEveryRealOneIsKept()
    {
        float[] noise = [0.030f, 0.035f, 0.040f, 0.041f, 0.042f, 0.043f, 0.051f, 0.052f, 0.060f, 0.066f];
        float[] real = [0.130f, 0.132f];

        foreach (float c in noise)
            Assert.Equal(0f, ContentBranch.PhotoScore(c));
        foreach (float c in real)
            Assert.True(ContentBranch.PhotoScore(c) > 0f, $"a real match at {c} scored nothing");
    }

    [Fact]
    public void TheBandStillSpreadsRealMatchesAcrossItsRange()
    {
        // A span left reaching 0.15 above a risen floor would squeeze every real match into the
        // bottom third of the scale, which turns ranking within the photos into noise of its own.
        float atRealMatch = ContentBranch.PhotoScore(RealMatchLow);
        Assert.InRange(atRealMatch, 0.45f, ContentBranch.PhotoCeiling);
        Assert.Equal(ContentBranch.PhotoCeiling, ContentBranch.PhotoScore(0.20f), 3);
    }

    [Fact]
    public void TheCalibrationSaysTheOldFloorWasRejectingNothing()
    {
        // The same two numbers in the units the model was actually trained in. A raw cosine gap
        // that looks like a factor of two and a half is a factor of thousands once the learned
        // scale and bias are applied - which is why the old floor looked defensible and was not.
        double atOldFloor = ModelStore.Siglip2Probability(0.05);
        double atRealMatch = ModelStore.Siglip2Probability(RealMatchLow);
        double atNewFloor = ModelStore.Siglip2Probability(ContentBranch.PhotoFloor);

        Assert.True(atOldFloor < 1e-4, $"the old floor was p={atOldFloor}");
        Assert.True(atRealMatch > 0.05, $"a real match is only p={atRealMatch}");
        Assert.True(atNewFloor > atOldFloor * 50, "the new floor is not meaningfully stricter");
    }

    [Fact]
    public void TheProbabilityIsMonotoneSoItCanNeverReorderAnything()
    {
        // Stated as a test because it is the correction to an easy and appealing mistake: applying
        // the sigmoid does NOT change which pictures come back first. It changes only whether a
        // threshold can be argued about. Anything claiming otherwise is wrong.
        // Strictly increasing across the range the model actually produces. Beyond about 0.35 the
        // sigmoid saturates to exactly 1.0 in double precision - which is still non-decreasing and
        // still cannot reorder anything, but is not STRICTLY increasing, so the two halves are
        // asserted separately rather than with one loop that would be quietly wrong.
        double last = -1;
        for (double c = -0.20; c <= 0.30; c += 0.005)
        {
            double p = ModelStore.Siglip2Probability(c);
            Assert.True(p > last, $"the calibration is not monotone at {c}");
            last = p;
        }
        for (double c = 0.30; c <= 1.00; c += 0.01)
        {
            double p = ModelStore.Siglip2Probability(c);
            Assert.True(p >= last, $"the calibration decreases at {c}");
            last = p;
        }
    }
}
