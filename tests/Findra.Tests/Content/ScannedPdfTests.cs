using Findra;
using SkiaSharp;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

/// <summary>
/// A fact that needs the English recogniser Windows installs with the English language, and says
/// so as its skip reason on a machine without one - rather than passing without having looked.
/// </summary>
public sealed class NeedsEnglishOcrFactAttribute : FactAttribute
{
    public NeedsEnglishOcrFactAttribute()
    {
        bool english = false;
        try
        {
            foreach (var l in Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages)
                if (l.LanguageTag.StartsWith("en", StringComparison.OrdinalIgnoreCase)) english = true;
        }
        catch (Exception) { }
        if (!english)
            Skip = "no English OCR recogniser on this machine (Settings > Time & language > Language & region: English, with its optical character recognition feature)";
    }
}

/// <summary>
/// The pages of a PDF that are pictures of pages, read by OCR. Which pages is a pure rule and is
/// tested as one; that the words really come out is tested end to end, on a PDF built here whose
/// only content is a picture of text.
/// </summary>
public sealed class ScannedPdfTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-scanned-" + Guid.NewGuid().ToString("N"));

    private string Under(string name)
    {
        Directory.CreateDirectory(_dir);
        return Path.Combine(_dir, name);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    // ---- which pages ----

    [Fact]
    public void OnlyThePagesWithoutTextOfTheirOwnAreRead()
    {
        // An ordinary PDF costs exactly what it did: a page with a text layer is never rendered.
        Assert.Empty(ScannedPdf.PagesToRead([250, 300, 180]));
        Assert.Equal([1, 3], ScannedPdf.PagesToRead([250, 0, 180, 2]).Order());
    }

    [Fact]
    public void AFewWordsStampedOnAScanDoNotMakeItAPageOfText()
    {
        // "Scanned with ..." or a page number, added by the scanner over the picture.
        Assert.Contains(0, ScannedPdf.PagesToRead([ScannedPdf.MinWords - 1]));
        Assert.DoesNotContain(0, ScannedPdf.PagesToRead([ScannedPdf.MinWords]));
    }

    [Fact]
    public void AtMostAHundredPagesOfOneFileAreRead()
    {
        // The first ones, which are what somebody remembers of a scanned book.
        int[] words = new int[250];
        words[3] = 400;
        IReadOnlySet<int> pages = ScannedPdf.PagesToRead(words);

        Assert.Equal(ScannedPdf.MaxPages, pages.Count);
        Assert.Contains(0, pages);
        Assert.DoesNotContain(3, pages);
        Assert.Equal(ScannedPdf.MaxPages, pages.Max());     // 0..100 less the text page
    }

    // ---- end to end ----

    /// <summary>A letter-sized page image with clear black type on white, the way a scanner
    /// would hand it over.</summary>
    private static byte[] PageOfText(params string[] lines)
    {
        using var bmp = new SKBitmap(1275, 1650);           // 150 dpi over 8.5 x 11 inches
        using (var canvas = new SKCanvas(bmp))
        using (var font = new SKFont(SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default, 44))
        using (var ink = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        {
            canvas.Clear(SKColors.White);
            for (int i = 0; i < lines.Length; i++)
                canvas.DrawText(lines[i], 110, 200 + i * 90, SKTextAlign.Left, font, ink);
        }
        using SKImage img = SKImage.FromBitmap(bmp);
        using SKData png = img.Encode(SKEncodedImageFormat.Png, 100);
        return png.ToArray();
    }

    [NeedsEnglishOcrFact]
    public void AScannedPdfIsReadByOcrAndItsWordsAreFound()
    {
        var builder = new PdfDocumentBuilder();
        PdfPageBuilder page = builder.AddPage(PageSize.Letter);
        page.AddPng(PageOfText("The quarterly lease agreement", "was signed in March", "by both tenants"),
                    new PdfRectangle(0, 0, 612, 792));
        string pdf = Under("scan.pdf");
        File.WriteAllBytes(pdf, builder.Build());

        int beats = 0;
        string text = DocText.Extract(pdf, () => beats++);

        Assert.Contains("lease", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("agreement", text, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("March", text, StringComparison.OrdinalIgnoreCase);
        Assert.True(beats >= 2, $"expected a beat for the page and one for reading it, got {beats}");
    }

    [NeedsEnglishOcrFact]
    public void APartlyScannedPdfKeepsItsTextPagesAndReadsItsScannedOneInPageOrder()
    {
        var builder = new PdfDocumentBuilder();
        PdfDocumentBuilder.AddedFont helvetica = builder.AddStandard14Font(Standard14Font.Helvetica);
        PdfPageBuilder typed = builder.AddPage(PageSize.Letter);
        typed.AddText("This first page was typed and carries a text layer of its own words", 12, new PdfPoint(72, 700), helvetica);
        PdfPageBuilder scanned = builder.AddPage(PageSize.Letter);
        scanned.AddPng(PageOfText("The signed invoice", "arrived on Tuesday"), new PdfRectangle(0, 0, 612, 792));
        string pdf = Under("mixed.pdf");
        File.WriteAllBytes(pdf, builder.Build());

        string text = DocText.Extract(pdf);

        int typedAt = text.IndexOf("typed", StringComparison.Ordinal);
        int scannedAt = text.IndexOf("invoice", StringComparison.OrdinalIgnoreCase);
        Assert.True(typedAt >= 0, "the text layer's words are kept");
        Assert.True(scannedAt > typedAt, $"the scanned page's words follow the typed page's: {text}");
        Assert.Contains("Tuesday", text, StringComparison.OrdinalIgnoreCase);
    }

    [NeedsEnglishOcrFact]
    public void AScannedPdfIsIndexedRatherThanSkippedAsHavingNoText()
    {
        // Through the real decoders, where a PDF that yielded no text used to end as "no text".
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.Letter).AddPng(PageOfText("The quarterly lease agreement", "was signed in March"),
                                                new PdfRectangle(0, 0, 612, 792));
        string pdf = Under("lease.pdf");
        File.WriteAllBytes(pdf, builder.Build());

        using var db = new ContentDb(Under("search.db"));
        using var vectors = new VectorStore(Under("vectors.bin"), writer: true);
        using var decoders = new Decoders(() => CapabilitySet.None, vectors, modelDir: _dir);
        db.Enqueue("C", 1, pdf, ResultKind.Document, "probe");
        Indexer.DrainOnce(db, _ => { }, decoders);

        Assert.Equal(1, db.IndexedCount());
        Assert.NotEmpty(db.Fts("lease", 10));
    }
}
