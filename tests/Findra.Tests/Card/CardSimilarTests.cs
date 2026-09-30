using Findra;
using Findra.Diagnostics;
using SkiaSharp;
using Xunit;

/// <summary>
/// "More like this" on the stage: a wide button directly above Open, Reveal and Copy path, drawn
/// for a file whose inside Findra reads, pressable once that file has something to compare.
/// </summary>
public class CardSimilarTests
{
    private static SearchCardState With(ResultKind kind, bool ready, int rows = 8)
    {
        var list = Enumerable.Range(0, rows)
            .Select(i => new SearchResult(kind, $"f{i}.x", $@"C:\a\f{i}.x", 0.8f, "in the name"))
            .ToList();
        var r = new SearchResults("f", list, 1, 0, false);
        return SearchCardState.Empty with { Query = "f", Results = r, Rows = list, SimilarReady = ready };
    }

    [Fact]
    public void ItIsAsWideAsTheThreeUnderItAndSitsDirectlyAboveThem()
    {
        foreach (int rows in new[] { 1, 6, 8 })
        {
            SKRect similar = SearchCardLayout.SimilarRect(rows, true);
            SKRect open = SearchCardLayout.ActionRect(rows, true, 0), copy = SearchCardLayout.ActionRect(rows, true, 2);
            Assert.Equal(open.Left, similar.Left, 3);
            Assert.Equal(copy.Right, similar.Right, 3);
            Assert.Equal(SearchCardLayout.SimilarGap, open.Top - similar.Bottom, 3);
            Assert.Equal(SearchCardLayout.SimilarH, similar.Height, 3);
            Assert.True(SearchCardLayout.StageRect(rows, true).Contains(similar), $"{rows} rows: the button leaves the stage");
        }
    }

    [Theory]
    [InlineData(ResultKind.Photo, true)]
    [InlineData(ResultKind.Video, true)]
    [InlineData(ResultKind.Document, true)]
    [InlineData(ResultKind.Audio, true)]
    [InlineData(ResultKind.File, false)]
    [InlineData(ResultKind.Folder, false)]
    public void ItIsOfferedForAFileWhoseInsideFindraReads(ResultKind kind, bool offered)
    {
        SearchCardState s = With(kind, ready: true);
        Assert.Equal(offered, s.OffersSimilar);
        SKRect r = SearchCardLayout.SimilarRect(s.Rows.Count, true);
        SearchHit hit = SearchCardLayout.HitTest(r.MidX, r.MidY, s.Rows.Count, 0, true, false, s.OffersSimilar);
        // Where it is not drawn, its room is the stage's, and a press there is a press on the stage.
        Assert.Equal(offered ? SearchTarget.Similar : SearchTarget.Stage, hit.Target);
        Assert.Equal(offered, CardAccess.Nodes(s).Any(n => n.Key == "similar"));
        Assert.False(SearchCardState.Empty.OffersSimilar);
    }

    [Fact]
    public void AScreenReaderIsToldWhenItCannotBePressedYet()
    {
        AccessNode waiting = CardAccess.Nodes(With(ResultKind.Photo, ready: false)).Single(n => n.Key == "similar");
        Assert.False(waiting.Enabled);
        Assert.Equal(SearchCardPainter.SimilarLabel, waiting.Name);
        Assert.Contains("unavailable", waiting.Spoken, StringComparison.Ordinal);

        AccessNode ready = CardAccess.Nodes(With(ResultKind.Photo, ready: true)).Single(n => n.Key == "similar");
        Assert.True(ready.Actionable);
        Assert.Equal(AccessRole.Button, ready.Role);
        Assert.Contains("f0.x", ready.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryStageLabelFitsItsButtonWithRoomToSpare()
    {
        // Centred and never cut: a label wider than its button is drawn over both ends of it.
        IReadOnlyList<string> labels = SearchCardPainter.ActionLabels;
        for (int a = 0; a < labels.Count; a++)
        {
            float w = CardText.Measure(labels[a], Parts.Face, SearchCardPainter.ActionTextSize);
            float room = SearchCardLayout.ActionRect(8, true, a).Width - 2 * 12;
            Assert.True(w <= room, $"'{labels[a]}' is {w:0.0} px in {room:0.0}");
        }
        float group = SearchCardPainter.SimilarIcon + SearchCardPainter.SimilarIconGap
                      + CardText.Measure(SearchCardPainter.SimilarLabel, Parts.Face, SearchCardPainter.ActionTextSize);
        float wide = SearchCardLayout.SimilarRect(8, true).Width - 2 * 16;
        Assert.True(group <= wide, $"the lens and '{SearchCardPainter.SimilarLabel}' are {group:0.0} px in {wide:0.0}");
    }

    [Fact]
    public void TheHeaderNamesTheFileTheRowsLookLike()
    {
        SearchCardState s = SearchShot.Build("similar");
        (string left, string right) = SearchCardPainter.Header(s, s.Rows.Count);
        Assert.Equal("8 like \u201cIMG_4471.HEIC\u201d", left);
        Assert.StartsWith("content ", right, StringComparison.Ordinal);
        Assert.Equal("IMG_4471.HEIC", SearchCardPainter.LikeName(@"D:\Photos\2025\08 Crete\IMG_4471.HEIC"));
    }

    [Fact]
    public void TheFooterTeachesTheKey()
    {
        Assert.Contains("Ctrl+L more like this", SearchCardLayout.FooterHint, StringComparison.Ordinal);
    }

    [Fact]
    public void TheShotsDrawItPressableHoveredAndNotOffering()
    {
        // Where the painter branches on data a state supplies, some state has to supply it.
        SearchCardState similar = SearchShot.Build("similar");
        Assert.True(similar.OffersSimilar && similar.SimilarReady);
        Assert.Equal(SearchTarget.Similar, similar.HoverTarget);
        Assert.True(SearchShot.Build("results").SimilarReady);
        SearchCardState many = SearchShot.Build("many");
        Assert.True(many.OffersSimilar);
        Assert.False(many.SimilarReady);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ItIsDrawnAsASolidAccentOnlyWhenItCanBePressed(bool ready)
    {
        SearchCardState s = With(ResultKind.Photo, ready);
        Derived d = Derived.From(Palette.Mond);
        int w = (int)Math.Ceiling(SearchCardLayout.WindowWidth);
        int h = (int)Math.Ceiling(SearchCardLayout.WindowHeight(s.Rows.Count, true));
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp)) SearchCardPainter.Paint(canvas, s, d, Parts.Face);

        SKRect r = SearchCardLayout.SimilarRect(s.Rows.Count, true);
        // Inside the button, clear of the lens and the words.
        SKColor fill = bmp.GetPixel((int)r.Left + 12, (int)(r.MidY + SearchCardLayout.Overhang));
        Assert.Equal(ready, fill == d.Accent);
    }
}
