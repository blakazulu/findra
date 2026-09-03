using System.Globalization;

using Findra;
using SkiaSharp;
using Xunit;

/// <summary>
/// The capsule's two bands: the bar somebody clicks, and the progress pill under it.
/// </summary>
public class CapsuleTests
{
    [Fact]
    public void ThePlaceholderFitsTheBarItIsDrawnIn()
    {
        // Nothing ellipsises this string - CapsulePainter draws it straight - so a placeholder
        // wider than the bar runs off the end of the capsule and is clipped by the window.
        //
        // It went unseen because the shot drew a DIFFERENT string: the window had
        // "Search files, photos, words..." and --searchshot had "Search 1.5M files", so every
        // render ever reviewed showed a placeholder the product does not use. One constant now.
        SKTypeface face = Parts.Face;
        float budget = CapsuleLayout.Width - (CapsuleLayout.Pad + 34f) - CapsuleLayout.Pad;
        float w = CardText.Measure(CapsulePainter.Placeholder, face, CapsuleLayout.TextSize);

        Assert.True(w <= budget,
            $"'{CapsulePainter.Placeholder}' is {w:0.0}px against a bar that holds {budget:0.0}px");
    }

    [Fact]
    public void TheProgressPillFitsInsideTheCapsuleWithAirUnderIt()
    {
        // It is drawn into the same bitmap the bar is, so a pill that reached the bottom edge
        // would be clipped by the window rather than merely tight. At 128 it ended 4px from the
        // bottom, which reads as cut off.
        SKRect pill = CapsuleLayout.PillRect();

        Assert.True(pill.Top >= CapsuleLayout.BarRect().Bottom, "the pill overlaps the bar");
        Assert.True(pill.Bottom + 8f <= CapsuleLayout.Height,
            $"the pill ends {CapsuleLayout.Height - pill.Bottom:0.0}px from the bottom");
        Assert.True(pill.Left > 0 && pill.Right < CapsuleLayout.Width);
    }

    [Fact]
    public void TheWidestSentenceIsNotCutShortOnEitherSurface()
    {
        // The pill's middle is ellipsised, so an overlong sentence does not clip or throw - it
        // stops mid-word, which is the defect the card's own placeholder had. The widest it gets
        // is the longest label against a machine with a million files to read.
        SKTypeface face = Parts.Face;
        string worst = ProgressPill.Sentence(
            IndexStatus.Pill(true, nameof(ResultKind.Audio), 1_000_000, 999_999, true));

        foreach ((string where, SKRect r) in new[]
                 {
                     ("the capsule", CapsuleLayout.PillRect()),
                     ("the card", SearchCardLayout.ProgressRect(0, hasQuery: false)),
                 })
        {
            float room = r.Right - (r.Left + ProgressPillLayout.Inset + ProgressPillLayout.Ring * 2 + 8f)
                       - ProgressPillLayout.PercentW;
            float w = CardText.Measure(worst, face, ProgressPillLayout.TextSize);
            Assert.True(w <= room, $"on {where}, '{worst}' is {w:0.0}px against {room:0.0}px of room");
        }
    }

    [Fact]
    public void ThePillSaysNothingWhenThereIsNothingToSay()
    {
        // A permanently visible progress pill makes an idle widget feel busy, which is the thing
        // spec 3 says the capsule must not do. Three states, one answer.
        Assert.False(IndexStatus.Pill(contentEnabled: false, "Doc", 1_000, 10, alive: true).Show);
        Assert.False(IndexStatus.Pill(contentEnabled: true, "Doc", 1_000, 10, alive: false).Show);
        Assert.False(IndexStatus.Pill(contentEnabled: true, "Doc", 0, 4_000, alive: true).Show);

        Assert.True(IndexStatus.Pill(contentEnabled: true, "Doc", 1_000, 10, alive: true).Show);
    }

    [Fact]
    public void TheLabelIsAWordAndNeverAnEnumName()
    {
        // The kind arrives as the ResultKind's own ToString off the queue row, and printing that
        // would put an identifier on the desktop.
        Assert.Equal("indexing photos", IndexStatus.Doing(ResultKind.Photo));
        Assert.Equal("indexing recordings", IndexStatus.Doing(ResultKind.Audio));
        Assert.Equal("indexing documents", IndexStatus.Doing(ResultKind.Document));
        Assert.Equal("indexing video", IndexStatus.Doing(ResultKind.Video));

        // A row read mid-write, or one written by a build that had no kind row at all, falls back
        // to the bare verb rather than to a guess.
        Assert.Equal("indexing", IndexStatus.Doing(""));
        Assert.Equal("indexing", IndexStatus.Doing("nonsense"));
        Assert.Equal("indexing", IndexStatus.Doing("99"));
    }

    [Fact]
    public void EveryKindTheQueueCanHoldIsSpeltTheWayTheEnumSpellsIt()
    {
        // THE test this needed. The switch was written on strings - "Photo", "Video", "Audio",
        // "Doc" - and "Doc" is not a member of ResultKind. It is the column heading --searchindex
        // prints, copied from one surface into a comparison on another. Documents are most of what
        // a first pass finds, so the pill read "indexing" with no noun nearly all of the time and
        // looked merely terse rather than broken.
        //
        // Round-tripping every member through the string form is what makes that impossible: a
        // member whose ToString does not reach its own word fails here.
        foreach (ResultKind k in Enum.GetValues<ResultKind>())
            Assert.Equal(IndexStatus.Doing(k), IndexStatus.Doing(k.ToString()));

    }

    [Fact]
    public void EveryKindWhoseContentsAreReadHasAWordForIt()
    {
        // Driven off FileKinds.HasContent rather than a list written out here, because that is the
        // predicate deciding what the indexer can ever be working on. The switch needs a default
        // arm - an enum can hold a value no member names, and the compiler insists - so a seventh
        // kind would fall into it and ship a verb with nothing after it. This is what refuses.
        foreach (ResultKind k in Enum.GetValues<ResultKind>())
        {
            if (!FileKinds.HasContent(k)) continue;
            Assert.NotEqual("indexing", IndexStatus.Doing(k));
            Assert.StartsWith("indexing ", IndexStatus.Doing(k), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void TheCountsReadTheSameOnEveryMachine()
    {
        CultureInfo was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("6,680 of 10,800", IndexStatus.Pill(true, "Photo", 4_120, 6_680, true).Count);
        }
        finally { CultureInfo.CurrentCulture = was; }
    }

    [Fact]
    public void BothOfTheCardsPlaceholdersFitItsFieldWithoutBeingCutShort()
    {
        // The card's field DOES ellipsize, which is why this went unnoticed: an overlong
        // placeholder is not a clipped glyph or a crash, it is a sentence that reads as though the
        // product stopped mid-thought. "Describe a photo, words in a document, speech..." was 45
        // characters against a field sized for the 28-character one beside it.
        //
        // The budget is DrawCapsule's own: text starts at height * 1.05 from the left and stops
        // height * 0.5 short of the right, at height * 0.40.
        SKTypeface face = Parts.Face;
        SKRect r = SearchCardLayout.FieldRect();
        float size = r.Height * 0.40f;
        float budget = r.Right - (r.Left + r.Height * 1.05f) - r.Height * 0.5f;

        foreach (string p in new[] { SearchCardPainter.NamePlaceholder, SearchCardPainter.ContentPlaceholder })
        {
            float w = CardText.Measure(p, face, size);
            Assert.True(w <= budget, $"'{p}' is {w:0.0}px against a field that holds {budget:0.0}px");
            // And the belt-and-braces version of the same thing: what is DRAWN is what was asked
            // for, rather than a shortened copy of it.
            Assert.Equal(p, CardText.Ellipsize(p, face, size, budget));
        }
    }

    [Theory]
    [InlineData(0.5)]
    [InlineData(0.85)]
    [InlineData(1.0)]
    [InlineData(1.7)]
    [InlineData(3.0)]
    public void TheWindowAndItsDrawingAgreeAboutHowBigTheCapsuleIs(double zoom)
    {
        // The window sized itself with the caller's zoom raw and the canvas drew with a clamped
        // copy of it, so outside [0.85, 1.7] the two disagreed. The window is what Windows clips
        // to, and the drawing being the larger of the two takes the bottom edge off - which is
        // exactly where the progress pill sits. They agreed only because the zoom is 1.0.
        //
        // One function, and this asserts that asking twice gives the same answer, which is the
        // whole property a second copy of the number breaks.
        Assert.Equal(CapsulePlacement.Scale(zoom), CapsulePlacement.Scale(zoom));

        // And that the clamp is a clamp: inside the range it is the identity, outside it holds.
        Assert.InRange(CapsulePlacement.Scale(zoom), 0.85, 1.7);
        if (zoom >= 0.85 && zoom <= 1.7) Assert.Equal(zoom, CapsulePlacement.Scale(zoom));
    }
}
