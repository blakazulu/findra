using Findra;

using Xunit;

/// <summary>
/// Which adapter the accelerated work goes to.
///
/// <para>These run on whatever machine they are run on - a laptop with one integrated chip, this
/// desktop with three adapters, a CI runner with a software rasteriser - so nothing here asserts
/// that a particular card exists. What is asserted is the part that would be wrong everywhere: the
/// choice must never be a software adapter, must never be made when there is nothing to choose
/// between, and must never throw on a machine that cannot answer.</para>
/// </summary>
public class GpuAdapterTests
{
    [Fact]
    public void EnumeratingNeverThrowsAndIndicesAreDxgisOwn()
    {
        // The indices are handed straight to AppendExecutionProvider_DML, so they have to be DXGI's
        // numbering rather than a position in our list. Identical until something is filtered out,
        // and that is exactly when it would go wrong.
        IReadOnlyList<GpuChoice> all = GpuAdapter.All();
        for (int i = 0; i < all.Count; i++) Assert.Equal(i, all[i].Index);
    }

    [Fact]
    public void ASoftwareRasteriserIsNeverChosen()
    {
        // WARP loads, initialises and answers correctly, and is slower than the CPU provider it
        // would be chosen over. It is the one adapter that must lose to falling back.
        GpuChoice? best = GpuAdapter.Best();
        if (best is not null) Assert.False(best.Software);
    }

    [Fact]
    public void NothingIsChosenWhenThereIsNothingToChooseBetween()
    {
        // One adapter is the common case and the caller's default is already right for it. Saying
        // null rather than "[0]" is what lets a log line tell "picked the discrete card" apart from
        // "there was only ever one".
        if (GpuAdapter.All().Count <= 1) Assert.Null(GpuAdapter.Best());
    }

    [Fact]
    public void TheChoiceIsTheMostDedicatedMemoryAmongTheRealOnes()
    {
        // The vendor-neutral discriminator: integrated graphics carve their memory out of system
        // RAM and report little or none of their own. A list of known-good device names would be a
        // vendor preference, which spec 7 forbids.
        IReadOnlyList<GpuChoice> all = GpuAdapter.All();
        GpuChoice? best = GpuAdapter.Best();
        if (best is null) return;
        foreach (GpuChoice a in all)
            if (!a.Software)
                Assert.True(a.DedicatedBytes <= best.DedicatedBytes,
                            $"[{a.Index}] {a.Name} has more dedicated memory than the chosen [{best.Index}] {best.Name}");
    }

    [Fact]
    public void DescribeAlwaysSaysSomething()
    {
        // It goes in a log line and in --searchmodels. A diagnostic that returns an empty string on
        // the machine somebody is filing a report from is worse than none.
        Assert.False(string.IsNullOrWhiteSpace(GpuAdapter.Describe()));
    }
}
