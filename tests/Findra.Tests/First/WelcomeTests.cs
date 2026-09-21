using Findra;
using SkiaSharp;
using Xunit;

/// <summary>
/// The first-run screen's answered page: where Findra lives, what happens now, who made it.
/// Everything it says is prose laid into fixed bands, so most of what can go wrong is a sentence
/// that no longer fits - measured here in the shipped face, on the terms the chooser is held to.
/// </summary>
[Collection("culture")]
public class WelcomeTests
{
    private static FirstRunState Answered(FirstRunStage stage, bool contentOn, params CapabilityProgress[] bars) => new()
    {
        Chosen = Capabilities.Close([Capability.Photos, Capability.Meaning]),
        HebrewOffered = true,
        ContentOn = contentOn,
        Stage = stage,
        Downloads = bars,
        Hotkey = "Alt+Space",
    };

    private static readonly CapabilityProgress[] Two =
    [
        new(Capability.Photos, 659_000_000, 659_000_000),
        new(Capability.Meaning, 96_000_000, 283_000_000),
    ];

    // ---- what it says -----------------------------------------------------------------------

    [Fact]
    public void TheHotkeyRowNamesTheChordThatRegisteredOrSaysThereIsNone()
    {
        Assert.StartsWith("Ctrl+Alt+F ", Welcome.HotkeyTitle("Ctrl+Alt+F"), StringComparison.Ordinal);

        // A shortcut that does nothing is worse than being told there is none.
        Assert.DoesNotContain("+", Welcome.HotkeyTitle(null), StringComparison.Ordinal);
        Assert.Contains("Settings", Welcome.HotkeyNote(null), StringComparison.Ordinal);
    }

    [Fact]
    public void TheAboutParagraphPromisesOnlyWhatTheUpdateSwitchAllows()
    {
        FirstRunState on = Answered(FirstRunStage.Finished, false) with { CheckUpdates = true };
        FirstRunState off = on with { CheckUpdates = false };

        Assert.Contains("daily check", Welcome.About(on), StringComparison.Ordinal);
        Assert.Contains("no requests", Welcome.About(off), StringComparison.Ordinal);
        Assert.DoesNotContain("daily check", Welcome.About(off), StringComparison.Ordinal);
    }

    [Fact]
    public void ReadingIsDescribedAsTheFirstActLeftIt()
    {
        Assert.Contains("asks", Welcome.Reading(Answered(FirstRunStage.Downloading, true)), StringComparison.Ordinal);
        Assert.Contains("Settings, under Content", Welcome.Reading(Answered(FirstRunStage.Finished, false)), StringComparison.Ordinal);
    }

    [Fact]
    public void NothingToFetchIsNotReportedAsZeroOfZero()
    {
        FirstRunState names = Answered(FirstRunStage.Finished, false) with { Chosen = new HashSet<Capability>() };
        Assert.Equal("", Welcome.Status(names));
        Assert.False(WelcomeLayout.ShowsStatus(names));

        // A problem is news even with nothing on the bars.
        Assert.Contains("no disk", Welcome.Status(names with { Problem = "no disk" }), StringComparison.Ordinal);
    }

    [Fact]
    public void APercentageIsNeverAHundredWhileBytesRemain()
    {
        Assert.Equal("99%", Welcome.Percent(new CapabilityProgress(Capability.Photos, 999, 1000)));
        Assert.Equal("100%", Welcome.Percent(new CapabilityProgress(Capability.Photos, 1000, 1000)));
        Assert.Equal("0%", Welcome.Percent(new CapabilityProgress(Capability.Photos, 0, 0)));
    }

    // ---- what fits --------------------------------------------------------------------------

    [Theory]
    [InlineData("Alt+Space")]
    [InlineData("Ctrl+Shift+Space")]
    [InlineData(null)]
    public void EachPlaceSaysItsPieceOnOneLine(string? chord)
    {
        SKTypeface face = Parts.Face;
        float room = WelcomeLayout.PlaceRect(0).Right - WelcomeLayout.TextLeft;

        foreach ((string title, string note) in new[]
                 {
                     (Welcome.CapsuleTitle, Welcome.CapsuleNote),
                     (Welcome.HotkeyTitle(chord), Welcome.HotkeyNote(chord)),
                     (Welcome.TrayTitle, Welcome.TrayNote),
                 })
        {
            Assert.True(CardText.Measure(title, face, Parts.LabelSize) <= room, $"'{title}' does not fit its line");
            Assert.True(CardText.Measure(note, face, Parts.NoteSize) <= room, $"'{note}' does not fit its line");
        }
    }

    [Fact]
    public void TheWidestChordFitsThePictureColumn()
    {
        // Three keycaps for the last link of the fallback chain, drawn as the painter draws them:
        // each key's width plus its padding, and the gap between them.
        SKTypeface face = Parts.Face;
        string[] keys = "Ctrl+Shift+Space".Split('+');
        float w = keys.Sum(k => CardText.Measure(k, face, Parts.NoteSize) + WelcomePainter.KeyPad)
                + WelcomePainter.KeyGap * (keys.Length - 1);
        Assert.True(w <= WelcomeLayout.IconW, $"the keycaps need {w:0.0}px and the column is {WelcomeLayout.IconW}px");
    }

    [Fact]
    public void TheLongestStatusFitsItsBand()
    {
        // A run that ended badly: the longest capability title, a real network message, and the
        // promise about the tray.
        FirstRunState worst = Answered(FirstRunStage.Downloading, true,
            new CapabilityProgress(Capability.Meaning, 283_000_000, 283_000_000),
            new CapabilityProgress(Capability.Speech, 12_000, 574_000_000),
            new CapabilityProgress(Capability.Hebrew, 0, 1_549_000_000)) with
        {
            Problem = "No such host is known. (huggingface.co:443)",
        };

        SKRect band = WelcomeLayout.StatusRect(worst);
        int lines = Parts.Wrap(Welcome.Status(worst), Parts.Face, Parts.LeadSize, band.Width).Count;
        Assert.True(Parts.LeadHeight(lines) <= band.Height,
            $"the status wraps to {lines} lines, needing {Parts.LeadHeight(lines)}px of the {band.Height}px it has");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void TheProseFitsTheBandsReservedForIt(bool on)
    {
        SKTypeface face = Parts.Face;
        FirstRunState s = Answered(FirstRunStage.Downloading, on, Two) with { CheckUpdates = on };

        SKRect reading = WelcomeLayout.NextRect(s);
        Assert.True(Parts.NoteHeight(Parts.Wrap(Welcome.Reading(s), face, Parts.NoteSize, reading.Width).Count) <= reading.Height,
                    "the reading sentence overflows its band");

        SKRect about = WelcomeLayout.AboutTextRect(s);
        Assert.True(Parts.NoteHeight(Parts.Wrap(Welcome.About(s), face, Parts.NoteSize, about.Width).Count) <= about.Height,
                    "the About paragraph overflows its band");

        Assert.True(CardText.Measure(Welcome.Subtitle(s), face, Parts.LabelSize) <= about.Width, "the subtitle wraps");
    }

    [Fact]
    public void EachLinkFitsItsOwnTarget()
    {
        foreach ((string label, _) in Welcome.Links)
            Assert.True(CardText.Measure(label, Parts.Face, Parts.LabelSize) + 2 * WelcomeLayout.LinkPad <= WelcomeLayout.LinkW,
                        $"'{label}' runs past its link");
    }

    [Fact]
    public void TheLinksGoWhereTheySay()
    {
        foreach ((string label, string url) in Welcome.Links)
        {
            Assert.StartsWith("https://", url, StringComparison.Ordinal);
            Assert.Contains(label.TrimEnd('/'), url, StringComparison.Ordinal);
        }
    }

    // ---- the shape of the page --------------------------------------------------------------

    [Theory]
    [InlineData(FirstRunStage.Downloading, true, 0)]
    [InlineData(FirstRunStage.Downloading, true, 4)]
    [InlineData(FirstRunStage.Finished, true, 2)]
    [InlineData(FirstRunStage.Finished, false, 0)]
    public void EveryBandFollowsTheOneAboveItAndClearsTheButtons(FirstRunStage stage, bool contentOn, int bars)
    {
        CapabilityProgress[] progress =
            [.. Capabilities.All.Take(bars).Select(c => new CapabilityProgress(c, 1, 2))];
        FirstRunState s = Answered(stage, contentOn, progress);
        float h = WelcomeLayout.Height(s);

        float lastPlace = WelcomeLayout.PlaceRect(WelcomeLayout.Places - 1).Bottom;
        Assert.True(WelcomeLayout.NowTop > lastPlace, "what happens now runs into the places");
        if (bars > 0) Assert.True(WelcomeLayout.StatusRect(s).Top >= WelcomeLayout.BarRowRect(bars - 1).Bottom);
        Assert.True(WelcomeLayout.NextRect(s).Top >= WelcomeLayout.StatusRect(s).Bottom);
        Assert.True(WelcomeLayout.AboutTop(s) > WelcomeLayout.NextRect(s).Bottom);
        Assert.True(WelcomeLayout.LinkRect(0, s).Top >= WelcomeLayout.AboutTextRect(s).Bottom);
        Assert.True(WelcomeLayout.LinkRect(0, s).Bottom < WelcomeLayout.SettingsRect(h).Top, "the links run into the buttons");

        // Shorter than the chooser it replaces: it never needs to grow the window past where it was.
        Assert.True(h <= FirstRunLayout.Height, $"the page is {h}px against the chooser's {FirstRunLayout.Height}px");
        Assert.Equal(h, FirstRunLayout.SurfaceHeight(s));
    }

    [Theory]
    [InlineData(FirstRunStage.Downloading)]
    [InlineData(FirstRunStage.Finished)]
    public void ItsControlsAnswerWhileTheDownloadRunsAsWellAsAfter(FirstRunStage stage)
    {
        // "Done" leaves the download running in the tray, which is what the status line says; the
        // old second act answered nothing until the last byte because its only button would have
        // been a second way to close. These each do something the download does not need.
        FirstRunState s = Answered(stage, contentOn: false, Two);
        float h = FirstRunLayout.SurfaceHeight(s);

        SKRect settings = WelcomeLayout.SettingsRect(h);
        SKRect done = FirstRunLayout.ButtonRect(1, h);
        Assert.Equal(FirstRunTarget.Settings, FirstRunLayout.HitTest(settings.MidX, settings.MidY, s).Target);
        Assert.Equal(FirstRunTarget.Go, FirstRunLayout.HitTest(done.MidX, done.MidY, s).Target);

        for (int i = 0; i < Welcome.Links.Count; i++)
        {
            SKRect link = WelcomeLayout.LinkRect(i, s);
            FirstRunHit hit = FirstRunLayout.HitTest(link.MidX, link.MidY, s);
            Assert.Equal(FirstRunTarget.Link, hit.Target);
            Assert.Equal(i, hit.Index);
        }

        // And nothing answers off the bottom of the page.
        Assert.Equal(FirstRunTarget.None, FirstRunLayout.HitTest(done.MidX, h + 5, s).Target);
    }

    [Fact]
    public void EveryTargetOnThePageHasAPointer()
    {
        foreach (FirstRunTarget t in new[] { FirstRunTarget.Settings, FirstRunTarget.Link })
            Assert.Equal(PointerShape.Hand, Pointers.ForFirstRun(t));
    }
}
