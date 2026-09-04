using Findra;
using Xunit;

/// <summary>
/// What a capability costs on a machine that holds SOME of the files.
///
/// <para>Every surface but the first-run screen priced this from installed capabilities, and a
/// capability is all-or-nothing: whisper-turbo with no e5 pair beside it leaves Speech
/// uninstalled, so 550 MB on the disk counted for nothing and four surfaces quoted 818 MB for a
/// 270 MB download. That folder is ordinary rather than hypothetical - a download run carries on
/// past a file that failed, so one bad leg of a Speech install leaves exactly it.</para>
/// </summary>
public class CapabilityPricingTests
{
    private static IReadOnlySet<string> Files(params Model[] models)
        => new HashSet<string>(models.Select(m => m.File), StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void SpeechCostsWhatIsMissingWhenWhisperIsAlreadyOnTheDisk()
    {
        // Whisper is here, the e5 pair is not, so no capability at all is installed - and the
        // honest number is the e5 pair, not the e5 pair plus a file that is already there.
        var half = new CapabilitySet(new HashSet<Capability>(), Files(ModelStore.WhisperTurbo));

        Assert.False(half.Has(Capability.Speech));
        Assert.Equal(Capabilities.TotalBytes([Capability.Meaning]),
                     Capabilities.MarginalBytes(Capability.Speech, half));
    }

    [Fact]
    public void HalfOfAPairIsPricedAtTheOtherHalf()
    {
        var half = new CapabilitySet(new HashSet<Capability>(), Files(ModelStore.E5Base));
        Assert.Equal(ModelStore.E5Spm.Bytes, Capabilities.MarginalBytes(Capability.Meaning, half));
    }

    [Fact]
    public void ASetThatNamesCapabilitiesAndNoFilesPricesThemAsPresent()
    {
        // The old arithmetic, kept exactly where it was right: a set built by hand saying Meaning
        // is installed is saying its two e5 files are on the disk, so Speech is the whisper file
        // alone. Only a real disk that no capability can describe changes the answer.
        var byName = new CapabilitySet(new HashSet<Capability> { Capability.Meaning });

        Assert.Equal(Capabilities.MarginalBytes(Capability.Speech, [Capability.Meaning]),
                     Capabilities.MarginalBytes(Capability.Speech, byName));
        Assert.Equal(ModelStore.WhisperTurbo.Bytes, Capabilities.MarginalBytes(Capability.Speech, byName));
    }

    [Fact]
    public void AnEmptyDiskCostsTheWholeClosedSet()
    {
        Assert.Equal(Capabilities.TotalBytes([Capability.Speech]),
                     Capabilities.MarginalBytes(Capability.Speech, CapabilitySet.None));
    }

    [Fact]
    public void AnInstalledCapabilityCostsNothingMore()
    {
        var have = new CapabilitySet(new HashSet<Capability> { Capability.Photos });
        Assert.Equal(0L, Capabilities.MarginalBytes(Capability.Photos, have));
    }
}
