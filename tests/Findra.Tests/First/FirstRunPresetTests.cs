using Findra;
using Xunit;

/// <summary>
/// The preset tiles on a machine that is not offered Hebrew.
///
/// <para>The rows hide the Hebrew row where it is not offered, and <c>AlreadyChosen</c> has always
/// dropped Hebrew from a selection for exactly this reason - but the tiles did not. Pressing
/// "Everything" on a machine that reads no Hebrew selected a 1.5 GB capability with nothing on
/// screen to name it: the three visible rows added to 1.45 GB, the tile and the bottom line both
/// said 2.93 GB, and the download drew a fourth progress bar for a row that did not exist.
/// </para>
/// </summary>
public class FirstRunPresetTests
{
    private static FirstRunState Offered(bool hebrew) => new()
    {
        Stage = FirstRunStage.Choosing,
        HebrewOffered = hebrew,
        OnDisk = new HashSet<string>(StringComparer.OrdinalIgnoreCase),
    };

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void NoTileEverSelectsACapabilityWithNoRow(int tile)
    {
        FirstRunState after = FirstRun.Apply(Offered(hebrew: false), new FirstRunHit(FirstRunTarget.Preset, tile));

        Assert.DoesNotContain(Capability.Hebrew, after.Chosen);
        // And the tile the person just pressed lights up. Presets.Match compares against the
        // closed sets, and Everything holds Hebrew - so on a machine that is not offered it, the
        // selection matched no preset, and the one thing on the screen that did not respond to the
        // click was the tile that took it.
        Assert.Equal((Preset)tile, FirstRun.Match(after.Chosen, hebrewOffered: false));
        // And every capability it DID select has a row somebody can see.
        var rows = FirstRun.Rows(after).Where(r => r.Capability is not null).Select(r => r.Capability!.Value).ToHashSet();
        foreach (Capability c in after.Chosen) Assert.Contains(c, rows);
    }

    [Fact]
    public void EverythingStillMeansEverythingWhereHebrewIsOffered()
    {
        FirstRunState after = FirstRun.Apply(Offered(hebrew: true), new FirstRunHit(FirstRunTarget.Preset, 2));
        Assert.Contains(Capability.Hebrew, after.Chosen);
    }

    [Fact]
    public void TheTilePricesWhatItWouldActuallyFetch()
    {
        // The tile and the selection are one number or the screen contradicts itself: 2.93 GB on
        // a tile that fetches 1.45 GB is the same lie as a row that prices a file already on disk.
        FirstRunState after = FirstRun.Apply(Offered(hebrew: false), new FirstRunHit(FirstRunTarget.Preset, 2));

        Assert.Equal(Sizes.Human(Capabilities.TotalBytes(after.Chosen)),
                     FirstRun.PresetSize(Preset.Everything, after.OnDisk, hebrewOffered: false));
        Assert.NotEqual(FirstRun.PresetSize(Preset.Everything, after.OnDisk, hebrewOffered: true),
                        FirstRun.PresetSize(Preset.Everything, after.OnDisk, hebrewOffered: false));
    }

    [Fact]
    public void NothingIsFetchedForACapabilityWithNoRow()
    {
        FirstRunState after = FirstRun.Apply(Offered(hebrew: false), new FirstRunHit(FirstRunTarget.Preset, 2));
        Assert.DoesNotContain(FirstRun.Wanted(after), m => m.File == ModelStore.WhisperHebrew.File);
    }
}
