using Findra;
using SkiaSharp;
using Xunit;

/// <summary>
/// The stage beside a short list is only as tall as the list's minimum, and it holds a picture,
/// the name, up to four lines about the file, the wide "More like this" button and the three
/// under it. The picture gives way: the text and the buttons are the parts that have to be read
/// and pressed.
/// </summary>
public class CardStageTests
{
    private static SearchCardState Short(int rows, ResultKind kind)
    {
        var list = Enumerable.Range(0, rows)
            .Select(i => new SearchResult(kind, $"trip-{i}.docx", $@"C:\Users\rae\Documents\Travel\trip-{i}.docx", 0.86f,
                                          "contains the words", Excerpt: "took the dog down to the beach"))
            .ToList();
        var r = new SearchResults("a dog on a beach", list, 0, 38, true);
        return SearchCardState.Empty with
        {
            Query = "a dog on a beach", Results = r, Rows = list, Highlight = 0, Clock = 0.2,
            // The fourth line: "file" is drawn only when there is something to say about it.
            StageDetail = "48 KB · 3 Aug 2025 21:10",
        };
    }

    [Theory]
    [InlineData(1, ResultKind.Document)]
    [InlineData(3, ResultKind.Document)]
    [InlineData(6, ResultKind.Document)]
    [InlineData(3, ResultKind.Video)]
    [InlineData(8, ResultKind.Document)]
    [InlineData(3, ResultKind.File)]
    [InlineData(8, ResultKind.Photo)]
    public void NoLineAboutTheFileRunsIntoTheButtons(int rows, ResultKind kind)
    {
        // Rendered, because the defect was a painter placing text by one sum and the buttons by
        // another: "score" was drawn a pixel above Open's top edge on every list of six or fewer.
        // A band of the stage just above the buttons has to be bare, left to right under the text.
        SearchCardState s = Short(rows, kind);
        int w = (int)Math.Ceiling(SearchCardLayout.WindowWidth);
        int h = (int)Math.Ceiling(SearchCardLayout.WindowHeight(rows, true));
        using var bmp = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul));
        using (var canvas = new SKCanvas(bmp))
            SearchCardPainter.Paint(canvas, s, Derived.From(Palette.Mond), Parts.Face);

        // The top button is "More like this", and its room is kept whether or not the row gets
        // one (a plain file does not), so the band above it is bare either way.
        SKRect topButton = SearchCardLayout.SimilarRect(rows, true);
        SKRect stage = SearchCardLayout.StageRect(rows, true);
        int top = (int)(topButton.Top + SearchCardLayout.Overhang);
        SKColor ground = bmp.GetPixel((int)stage.Right - 4, top - 3);
        // Up to two pixels short of the button: its outline is stroked on its edge and
        // anti-aliased, so the row just above it carries the button's own ink.
        for (int y = top - 7; y < top - 2; y++)
            for (int x = (int)stage.Left + 8; x < (int)stage.Left + 160; x++)
                Assert.True(bmp.GetPixel(x, y) == ground,
                    $"{rows} row(s), {kind}: ink at ({x},{y}), just above the buttons at {top}");
    }

    [Theory]
    [InlineData(ResultKind.Document)]
    [InlineData(ResultKind.Video)]
    public void BesideAFullListThePictureKeepsItsWholeSize(ResultKind kind)
    {
        // Only a short list costs the picture anything. A full one leaves the stage room to spare,
        // and a picture shrunk there would be a smaller preview for no reason.
        SKRect stage = SearchCardLayout.StageRect(SearchCardLayout.MaxRows, true);
        float whole = kind == ResultKind.Video ? (stage.Width - 16) * 9 / 16 : Math.Min(stage.Width - 16, 190);

        Assert.Equal(whole, SearchCardLayout.StagePicture(SearchCardLayout.MaxRows, true, kind == ResultKind.Video, lines: 4).Height, 2);
    }
}
