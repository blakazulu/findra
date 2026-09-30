using System.IO.Compression;
using System.Text;
using Findra;
using Xunit;

/// <summary>
/// The document formats read inside by their own small readers - RTF and the three OpenDocument
/// zips - and the limit every zip-based format reads under. Every fixture is built here, in code:
/// nothing about these formats needs a real application to produce a file that shows the rule.
/// </summary>
public sealed class DocReadersTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-docreaders-" + Guid.NewGuid().ToString("N"));

    private string Under(string name)
    {
        Directory.CreateDirectory(_dir);
        return Path.Combine(_dir, name);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private static string Rtf(string body) => RtfText.Read(new MemoryStream(Encoding.ASCII.GetBytes(body)));

    // ---- RTF ----

    [Fact]
    public void AnRtfFileGivesItsWordsAndNoneOfItsControlWords()
    {
        string text = Rtf(@"{\rtf1\ansi\deff0{\fonttbl{\f0\froman Times New Roman;}}{\colortbl;\red0\green0\blue0;}"
                          + @"{\stylesheet{\s0 Normal;}}{\info{\title Secret title}{\author Somebody}}"
                          + @"\pard\plain\f0\fs24 The quarterly \b lease\b0  agreement\par was signed.\par}");

        Assert.Contains("The quarterly lease agreement", text);
        Assert.Contains("was signed.", text);
        Assert.Contains('\n', text);
        foreach (string noise in new[] { "rtf1", "fonttbl", "Times New Roman", "red0", "Normal", "Secret title", "Somebody", "pard", "fs24" })
            Assert.DoesNotContain(noise, text);
    }

    [Fact]
    public void NestedAndIgnorableDestinationsAreSkippedWholeWhateverIsInsideThem()
    {
        // A picture's hex, a field's instruction, and a \* group this reader has never heard of:
        // each would otherwise index its contents as words. The field's RESULT is what a reader
        // sees on the page, so that stays.
        string text = Rtf(@"{\rtf1\ansi before "
                          + @"{\pict\pngblip 89504e470d0a1a0a{\*\blipuid 0123abcd}}"
                          + @"{\*\generator Riched20 10.0}{\*\somethingnew {nested {deeper words}} more}"
                          + @"{\field{\*\fldinst HYPERLINK ""http://example.com""}{\fldrslt the link text}}"
                          + @" after\par}");

        Assert.Contains("before", text);
        Assert.Contains("the link text", text);
        Assert.Contains("after", text);
        foreach (string noise in new[] { "89504e47", "blipuid", "Riched20", "nested", "deeper", "HYPERLINK", "example.com" })
            Assert.DoesNotContain(noise, text);
    }

    [Fact]
    public void HebrewWrittenAsBytesInTheDocumentsCodePageIsDecodedInIt()
    {
        // \ansicpg1255 is Windows Hebrew: e0 is alef, e1 bet, and so on. Read as the default
        // western page these bytes are accented Latin letters, which no Hebrew query matches.
        string text = Rtf(@"{\rtf1\ansi\ansicpg1255\deff0 \'f9\'ec\'e5\'ed world\par}");

        Assert.Equal("שלום world\n", text);
    }

    [Fact]
    public void HebrewWrittenAsBytesInAFontsCharsetIsDecodedInThatFontsCodePage()
    {
        // Word's own habit: a western document whose Hebrew runs switch to a font declared with
        // charset 177, and the bytes in those runs are in that font's page.
        string text = Rtf(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss Arial;}{\f1\fswiss\fcharset177 Arial;}}"
                          + @"caf\'e9 {\f1 \'f9\'ec\'e5\'ed}\par}");

        Assert.Contains("café", text);
        Assert.Contains("שלום", text);
    }

    [Fact]
    public void HebrewWrittenAsUnicodeIsReadAndItsFallbackIsNotReadTwice()
    {
        // \uN is the character and what follows (\uc1: one character, a \'hh counts as one) is a
        // fallback for readers that cannot show it - read both and every letter appears twice,
        // once as itself and once as a question mark or a western byte.
        string text = Rtf(@"{\rtf1\ansi\uc1 \u1513\'3f\u1500?\u1493\'3f\u1501? world\par}");

        Assert.Equal("שלום world\n", text);
        Assert.DoesNotContain("?", text);
    }

    [Fact]
    public void ANegativeUnicodeNumberIsTheCharacterAbove32767AndUc0SkipsNothing()
    {
        // RTF numbers are signed sixteen-bit, so U+FB2A (a Hebrew presentation form) is written
        // -1238, and a document may say its \u characters have no fallback at all.
        string text = Rtf(@"{\rtf1\uc0 \u-1238 x\par}");

        Assert.Contains("\uFB2A", text);
        Assert.Contains("x", text);
    }

    [Fact]
    public void EscapedBracesAndBackslashesAreTextAndBinaryDataIsSkippedByItsLength()
    {
        // \bin is followed by raw bytes that may contain braces: counting them as groups would
        // leave the rest of the document inside a destination that is being skipped.
        string text = Rtf(@"{\rtf1 a \{literal\} and \\ path {\*\objdata \bin6 }}}}}} } after\par}");

        Assert.Contains("{literal}", text);
        Assert.Contains(@"\ path", text);
        Assert.Contains("after", text);
    }

    [Fact]
    public void ParagraphsLinesAndTabsBecomeBreaksAndSpaces()
    {
        string text = Rtf(@"{\rtf1 one\par two\line three\tab four\par}");

        Assert.Equal("one\ntwo\nthree\tfour\n", text);
    }

    [Theory]
    [InlineData(@"{\rtf1 unfinished words")]
    [InlineData(@"{\rtf1 words}}}}} more")]
    [InlineData(@"\")]
    [InlineData(@"{\rtf1 \'")]
    [InlineData(@"{\rtf1 \'zz words}")]
    [InlineData(@"{\rtf1 \u")]
    [InlineData(@"{\rtf1 \bin99999999 x}")]
    [InlineData("not an rtf file at all")]
    public void BrokenRtfNeverThrowsAndKeepsWhatItRead(string body)
    {
        string text = Rtf(body);
        Assert.NotNull(text);
        if (body.Contains("words")) Assert.Contains("words", text);
    }

    [Fact]
    public void AFileOfNothingButOpeningBracesDoesNotGrowWithoutLimit()
    {
        string text = Rtf(new string('{', 200_000) + @"\rtf1 deep words" + new string('}', 200_000));
        Assert.Contains("deep words", text);
    }

    [Fact]
    public void TheBeatFiresPerParagraphInAnRtfFile()
    {
        string rtf = Under("notes.rtf");
        File.WriteAllText(rtf, @"{\rtf1 " + string.Concat(Enumerable.Range(0, 20).Select(i => $"paragraph {i}\\par ")) + "}");

        int beats = 0;
        string text = DocText.Extract(rtf, () => beats++);

        Assert.True(beats > 1, $"expected more than one beat over twenty paragraphs, got {beats}");
        Assert.Contains("paragraph 19", text);
    }

    // ---- OpenDocument ----

    private const string Office = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private const string TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const string Table = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string Draw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private const string Presentation = "urn:oasis:names:tc:opendocument:xmlns:presentation:1.0";

    private string Odf(string name, string body)
    {
        string path = Under(name);
        using FileStream fs = File.Create(path);
        using var zip = new ZipArchive(fs, ZipArchiveMode.Create);
        using (var w = new StreamWriter(zip.CreateEntry("mimetype").Open())) w.Write("application/vnd.oasis.opendocument");
        using (var w = new StreamWriter(zip.CreateEntry("content.xml").Open()))
            w.Write($"<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-content xmlns:office=\"{Office}\" "
                    + $"xmlns:text=\"{TextNs}\" xmlns:table=\"{Table}\" xmlns:draw=\"{Draw}\" xmlns:presentation=\"{Presentation}\" "
                    + "xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:style=\"urn:oasis:names:tc:opendocument:xmlns:style:1.0\">"
                    + "<office:automatic-styles><style:style style:name=\"T1\"/></office:automatic-styles>"
                    + $"<office:body>{body}</office:body></office:document-content>");
        return path;
    }

    [Fact]
    public void AnOpenDocumentTextFileGivesItsWordsWithSpansSpacesAndBreaksWhereTheyBelong()
    {
        string odt = Odf("contract.odt",
            "<office:text>"
            + "<text:h text:outline-level=\"1\">Lease terms</text:h>"
            + "<text:p>The <text:span text:style-name=\"T1\">quarterly</text:span> <text:span>lease</text:span>"
            + "<text:s text:c=\"3\"/>agreement<text:tab/>signed<text:line-break/>in March</text:p>"
            + "<text:p>Hyphen<text:span>ated</text:span> stays one word.</text:p>"
            + "<text:list><text:list-item><text:p>first item</text:p></text:list-item></text:list>"
            + "<table:table><table:table-row><table:table-cell><text:p>Total</text:p></table:table-cell>"
            + "<table:table-cell><text:p>12</text:p></table:table-cell></table:table-row></table:table>"
            + "<text:tracked-changes><text:changed-region><text:deletion><text:p>deleted words</text:p></text:deletion></text:changed-region></text:tracked-changes>"
            + "</office:text>");

        string text = DocText.Extract(odt);

        Assert.Contains("Lease terms", text);
        Assert.Contains("The quarterly lease agreement signed\nin March", text);
        Assert.Contains("Hyphenated stays one word.", text);
        Assert.Contains("first item", text);
        Assert.Matches(@"Total\s+12", text);
        Assert.DoesNotContain("deleted words", text);
        Assert.DoesNotContain("T1", text);
    }

    [Fact]
    public void AnOpenDocumentSpreadsheetGivesTheTextOfItsCells()
    {
        string ods = Odf("budget.ods",
            "<office:spreadsheet><table:table table:name=\"Sheet1\">"
            + "<table:table-row><table:table-cell office:value-type=\"string\"><text:p>Rent</text:p></table:table-cell>"
            + "<table:table-cell office:value-type=\"float\" office:value=\"1250\"><text:p>1250</text:p></table:table-cell>"
            + "<table:table-cell table:number-columns-repeated=\"16000\"/></table:table-row>"
            + "<table:table-row><table:table-cell><text:p>Electricity</text:p></table:table-cell></table:table-row>"
            + "</table:table></office:spreadsheet>");

        string text = DocText.Extract(ods);

        Assert.Matches(@"Rent\s+1250", text);
        Assert.Contains("Electricity", text);
        Assert.DoesNotContain("Sheet1", text);
    }

    [Fact]
    public void AnOpenDocumentPresentationGivesItsSlidesAndItsNotes()
    {
        string odp = Odf("pitch.odp",
            "<office:presentation>"
            + "<draw:page draw:name=\"page1\"><draw:frame><draw:text-box><text:p>Quarterly results</text:p></draw:text-box></draw:frame>"
            + "<presentation:notes><draw:frame><draw:text-box><text:p>mention the renewals</text:p></draw:text-box></draw:frame></presentation:notes>"
            + "</draw:page>"
            + "<draw:page draw:name=\"page2\"><draw:frame><draw:text-box><text:p>Next steps</text:p></draw:text-box></draw:frame></draw:page>"
            + "</office:presentation>");

        string text = DocText.Extract(odp);

        Assert.Contains("Quarterly results", text);
        Assert.Contains("mention the renewals", text);
        Assert.Contains("Next steps", text);
        Assert.DoesNotContain("page1", text);
    }

    [Fact]
    public void WhoWroteACommentAndWhenIsNotPartOfTheDocument()
    {
        string odt = Odf("reviewed.odt",
            "<office:text><text:p>Signed copy<office:annotation><dc:creator>Reviewer Name</dc:creator>"
            + "<dc:date>2026-09-30T10:00:00</dc:date><text:p>check the date</text:p></office:annotation></text:p></office:text>");

        string text = DocText.Extract(odt);

        Assert.Contains("Signed copy", text);
        Assert.Contains("check the date", text);
        Assert.DoesNotContain("Reviewer Name", text);
        Assert.DoesNotContain("2026-09-30", text);
    }

    [Fact]
    public void TheBeatFiresPerParagraphInAnOpenDocumentFile()
    {
        string odt = Odf("long.odt",
            "<office:text>" + string.Concat(Enumerable.Range(0, 30).Select(i => $"<text:p>paragraph {i}</text:p>")) + "</office:text>");

        int beats = 0;
        string text = DocText.Extract(odt, () => beats++);

        Assert.True(beats > 1, $"expected more than one beat over thirty paragraphs, got {beats}");
        Assert.Contains("paragraph 29", text);
    }

    // ---- the limit on what one archive may inflate to ----

    [Theory]
    [InlineData("docx", "word/document.xml")]
    [InlineData("xlsx", "xl/sharedStrings.xml")]
    [InlineData("epub", "chapter1.xhtml")]
    [InlineData("odt", "content.xml")]
    public void AnArchiveThatInflatesPastItsLimitStopsThereAndKeepsWhatItAlreadyRead(string ext, string bomb)
    {
        // The limit is lowered to a size a test can reach, and the file is shaped the way a real
        // bomb is: a few real words, then padding that inflates to far more than the limit from
        // almost nothing on disk. Reading must stop and keep the words already read - never fail
        // the file, and never inflate the whole thing to find out how big it was.
        string path = Under("bomb." + ext);
        const long Limit = 64 * 1024;
        using (FileStream fs = File.Create(path))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry(bomb, CompressionLevel.SmallestSize).Open()))
        {
            w.Write(ext switch
            {
                "docx" => "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>"
                          + "<w:p><w:r><w:t>the words before the padding</w:t></w:r></w:p><w:p><w:r><w:t>",
                "xlsx" => "<sst><si><t>the words before the padding</t></si><si><t>",
                "odt" => $"<office:document-content xmlns:office=\"{Office}\" xmlns:text=\"{TextNs}\"><office:body><office:text>"
                         + "<text:p>the words before the padding</text:p><text:p>",
                _ => "<html><body><p>the words before the padding</p><p>",
            });
            w.Write(new string(' ', 4 << 20));     // four megabytes that compress to almost nothing
            w.Write("never reached");
        }

        string text = DocText.Extract(path, archiveBytes: Limit);

        Assert.DoesNotContain("never reached", text);
        // A workbook's shared strings reach the text only through a sheet, and a chapter is read
        // whole before it is stripped, so for those two the file simply yields nothing more.
        if (ext is "docx" or "odt") Assert.Contains("the words before the padding", text);
    }

    [Fact]
    public void AnOrdinaryDocumentIsNowhereNearTheLimit()
    {
        string docx = Under("ordinary.docx");
        using (FileStream fs = File.Create(docx))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
        {
            w.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
            for (int i = 0; i < 2000; i++) w.Write($"<w:p><w:r><w:t>paragraph {i} of an ordinary report</w:t></w:r></w:p>");
            w.Write("</w:body></w:document>");
        }

        Assert.Contains("paragraph 1999", DocText.Extract(docx));
    }

    [Fact]
    public void TheBudgetIsSharedAcrossEveryPartOfOneFile()
    {
        // Many parts each under the limit add up past it: the limit is per FILE, or a deck of a
        // thousand modest slides is a way round it.
        string pptx = Under("many.pptx");
        using (FileStream fs = File.Create(pptx))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            for (int i = 1; i <= 40; i++)
                using (var w = new StreamWriter(zip.CreateEntry($"ppt/slides/slide{i}.xml").Open()))
                    w.Write($"<p:sld xmlns:p=\"p\" xmlns:a=\"a\"><a:p><a:t>slide {i}</a:t></a:p>{new string(' ', 4000)}</p:sld>");

        string text = DocText.Extract(pptx, archiveBytes: 20_000);

        Assert.Contains("slide 1", text);
        Assert.DoesNotContain("slide 40", text);
    }
}
