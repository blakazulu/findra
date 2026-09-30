using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Versioning;

namespace Findra;

/// <summary>
/// The words on the pages of a PDF that carry no text of their own: a scanned contract, a fax, a
/// phone photo of a receipt saved as a PDF. Such a page is a picture, so the text layer
/// <see cref="DocText"/> reads is empty, and before this the whole file was skipped as having no
/// text.
///
/// <para>Each such page is rendered by the PDF renderer Windows ships (<c>Windows.Data.Pdf</c>) and
/// read by the same recognisers as the text inside pictures (<see cref="ImageText"/>). No model
/// and no download: this is part of reading documents and runs whenever that does. Pages that do
/// have text are never rendered, so an ordinary PDF costs exactly what it did.</para>
///
/// <para>One instance is one file. The document is opened on the first page asked for and let go
/// at <see cref="Dispose"/>, with the file shared for reading, writing and deleting so that
/// somebody saving over their own document is never blocked by the indexer reading it.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class ScannedPdf : IDisposable
{
    /// <summary>A page with fewer words than this in its text layer is read by OCR. Not zero:
    /// scanning software and fax gateways stamp a scan with a few words of their own - a page
    /// number, "Scanned with", a sender's line - and a page that says only that is still a
    /// picture of a page. A real page of writing has far more; a title page with fewer is read
    /// twice, which costs a second and duplicates a handful of words.</summary>
    public const int MinWords = 10;

    /// <summary>How many pages of one file are read by OCR. A page takes a second or two, so a
    /// scanned book would hold the indexer for minutes that every other file on the disk waits
    /// through; the first hundred pages are what somebody searching for it remembers.</summary>
    public const int MaxPages = 100;

    /// <summary>The resolution a page is rendered at for reading: what a scanner uses for text.
    /// A page larger than that allows is rendered smaller, to fit the recogniser's own limit
    /// (<c>OcrEngine.MaxImageDimension</c>).</summary>
    public const double Dpi = 300;

    /// <summary>Which pages to read, by index, given how many words each page's text layer had:
    /// every page with fewer than <see cref="MinWords"/>, up to the first
    /// <see cref="MaxPages"/> of them.</summary>
    public static IReadOnlySet<int> PagesToRead(IReadOnlyList<int> wordsPerPage)
    {
        ArgumentNullException.ThrowIfNull(wordsPerPage);
        var pages = new HashSet<int>();
        for (int i = 0; i < wordsPerPage.Count && pages.Count < MaxPages; i++)
            if (wordsPerPage[i] < MinWords) pages.Add(i);
        return pages;
    }

    private readonly string _path;
    private readonly int _wanted;
    private FileStream? _file;
    private Windows.Storage.Streams.IRandomAccessStream? _stream;
    private Windows.Data.Pdf.PdfDocument? _doc;
    private bool _opened;

    /// <param name="wanted">How many pages of the file had no text, for the log line that says
    /// some were left unread.</param>
    public ScannedPdf(string path, int wanted = 0)
    {
        _path = path;
        _wanted = wanted;
    }

    /// <summary>The words on one page (from zero), or "" when there are none, no recogniser is
    /// installed, or Windows cannot open this file (a password, a damaged file).</summary>
    public string Read(int page)
    {
        if (!ImageText.Ready || !Open() || _doc is null || page < 0 || page >= (long)_doc.PageCount) return "";
        try { return Render(page); }
        catch (Exception ex)
        {
            Log.Once($"index|ocr|pdf|{ex.GetType().Name}", "WARN", "index",
                     $"a scanned page could not be read :: {ex.GetType().Name}: {ex.Message}");
            return "";
        }
    }

    private bool Open()
    {
        if (_opened) return _doc is not null;
        _opened = true;
        try
        {
            _file = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            _stream = _file.AsRandomAccessStream();
            _doc = Windows.Data.Pdf.PdfDocument.LoadFromStreamAsync(_stream).AsTask().GetAwaiter().GetResult();
            // Once a process: what the log needs is that this happens at all, and the first file
            // it happened to. A line per file would be a line per scan on a disk full of them.
            Log.Once("index|ocr|pdf", "INFO", "index", $"reading the words on scanned pages, first in {Path.GetFileName(_path)}");
            if (_wanted > MaxPages)
                Log.Once("index|ocr|pdf|cap", "INFO", "index",
                         $"{Path.GetFileName(_path)} has {_wanted} scanned pages; the first {MaxPages} are read");
            return true;
        }
        catch (Exception ex)
        {
            Log.Once($"index|ocr|pdfopen|{ex.GetType().Name}", "INFO", "index",
                     $"a PDF's scanned pages could not be opened for reading :: {ex.GetType().Name}: {ex.Message}");
            _doc = null;
            return false;
        }
    }

    private string Render(int index)
    {
        using Windows.Data.Pdf.PdfPage page = _doc!.GetPage((uint)index);
        double w = page.Size.Width, h = page.Size.Height;   // device-independent pixels, 96 to the inch
        if (!(w > 0 && h > 0)) return "";
        double scale = Math.Min(Dpi / 96, Windows.Media.Ocr.OcrEngine.MaxImageDimension / Math.Max(w, h));
        var options = new Windows.Data.Pdf.PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(w * scale)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(h * scale)),
            // a transparent page reads as black on black once it is flattened for the recogniser
            BackgroundColor = new Windows.UI.Color { A = 255, R = 255, G = 255, B = 255 },
        };
        using var png = new Windows.Storage.Streams.InMemoryRandomAccessStream();
        page.RenderToStreamAsync(png, options).AsTask().GetAwaiter().GetResult();
        png.Seek(0);
        return ImageText.Read(png);
    }

    public void Dispose()
    {
        _doc = null;
        _stream?.Dispose();
        _file?.Dispose();
    }
}
