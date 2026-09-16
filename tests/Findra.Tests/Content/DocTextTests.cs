using System.IO.Compression;
using Findra;
using Xunit;

/// <summary>
/// <see cref="DocText.Extract"/>'s optional beat, threaded into the extraction itself rather than
/// fired once after the whole call returns - which is what lets a watchdog tell a large document
/// still being read apart from a decoder that has stopped moving. See <see cref="IndexerWatch"/>.
/// </summary>
public sealed class DocTextTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "findra-doctext-" + Guid.NewGuid().ToString("N"));

    private string Under(string name)
    {
        Directory.CreateDirectory(_dir);
        return Path.Combine(_dir, name);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void TheBeatFiresMoreThanOnceOverAMultiSheetWorkbook()
    {
        // A workbook is a zip of one XML part per sheet, which is DocText's own unit of progress
        // for this format - the same shape as a page in a PDF or a slide in a deck.
        string xlsx = Under("book.xlsx");
        using (FileStream fs = File.Create(xlsx))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
            for (int i = 1; i <= 3; i++)
                using (var w = new StreamWriter(zip.CreateEntry($"xl/worksheets/sheet{i}.xml").Open()))
                    w.Write($"<sheetData><row><c><t>sheet {i} has words in it for the reader to find</t></c></row></sheetData>");

        int beats = 0;
        string text = DocText.Extract(xlsx, () => beats++);

        Assert.True(beats > 1, $"expected more than one beat over three sheets, got {beats}");
        Assert.Contains("sheet 1", text);
        Assert.Contains("sheet 3", text);
    }

    [Fact]
    public void TheBeatFiresMoreThanOnceOverALargeSharedStringsTable()
    {
        // Every repeated string in a real workbook lives in xl/sharedStrings.xml, parsed before
        // any worksheet - a realistic place to spend a long, genuinely-progressing stretch with
        // nothing else in Extract to beat from. One worksheet on its own would beat only once, so
        // this table is what has to carry the count past one.
        string xlsx = Under("shared.xlsx");
        using (FileStream fs = File.Create(xlsx))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(zip.CreateEntry("xl/sharedStrings.xml").Open()))
            {
                w.Write("<sst>");
                for (int i = 0; i < 20; i++) w.Write($"<si><t>shared string number {i}</t></si>");
                w.Write("</sst>");
            }
            using (var w = new StreamWriter(zip.CreateEntry("xl/worksheets/sheet1.xml").Open()))
                w.Write("<sheetData><row><c t=\"s\"><v>0</v></c></row></sheetData>");
        }

        int beats = 0;
        string text = DocText.Extract(xlsx, () => beats++);

        Assert.True(beats > 1, $"expected more than one beat while reading a large shared-strings table, got {beats}");
        Assert.Contains("shared string number 0", text);
    }

    [Fact]
    public void TheBeatFiresMoreThanOnceAcrossManyParagraphsInOneOoxmlPart()
    {
        // docx beats once per PART, and there are only three - in practice nearly everything sits
        // in the one substantial part, word/document.xml, so a beat only between parts is barely
        // better than one beat before the file and one after. The real progress unit inside a huge
        // single part is the paragraph.
        string docx = Under("many-paragraphs.docx");
        using (FileStream fs = File.Create(docx))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Create))
        using (var w = new StreamWriter(zip.CreateEntry("word/document.xml").Open()))
        {
            w.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>");
            for (int i = 0; i < 50; i++) w.Write($"<w:p><w:r><w:t>paragraph {i}</w:t></w:r></w:p>");
            w.Write("</w:body></w:document>");
        }

        int beats = 0;
        string text = DocText.Extract(docx, () => beats++);

        Assert.True(beats > 1, $"expected more than one beat across many paragraphs in one part, got {beats}");
        Assert.Contains("paragraph 0", text);
        Assert.Contains("paragraph 49", text);
    }

    [Fact]
    public void NoBeatIsRequired()
    {
        // The default stays usable for every caller that does not care about progress - the
        // parameter is additive, not a new obligation on every reader.
        string txt = Under("plain.txt");
        File.WriteAllText(txt, "words in a plain file");
        Assert.Equal("words in a plain file", DocText.Extract(txt));
    }
}
