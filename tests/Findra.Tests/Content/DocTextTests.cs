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
    public void NoBeatIsRequired()
    {
        // The default stays usable for every caller that does not care about progress - the
        // parameter is additive, not a new obligation on every reader.
        string txt = Under("plain.txt");
        File.WriteAllText(txt, "words in a plain file");
        Assert.Equal("words in a plain file", DocText.Extract(txt));
    }
}
