using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Linq;
using System.Xml;

namespace Findra;

// Text out of documents, for the indexer child. PDF through PdfPig, with the pages that carry no
// text (a scan) read by the recognisers Windows ships; docx/pptx/xlsx/epub and the OpenDocument
// formats are zip archives of XML and need no library - every text node of the right parts, in
// order, joined with spaces; RTF has its own small reader; everything else is read as text.
// Nothing here formats: the output is words for the full-text index, and a table cell sitting
// next to its neighbour is exactly what that wants.
//
// This runs over arbitrary files found on someone's disk, which is why it lives in the indexer at
// normal integrity and never in the elevated helper (spec §3).
public static class DocText
{
    /// <summary>How many CHARACTERS of one file are read. Roughly a two-hundred-page book; past
    /// that a "document" is a corpus - a database dump, a concatenated log - and reading it whole
    /// costs minutes and finds nothing anyone was looking for. Characters because that is what is
    /// counted and what is chunked: nothing here has a vocabulary to measure in.</summary>
    public const int MaxChars = 400_000;

    /// <summary>Formats Findra classifies as documents and cannot read INSIDE: the legacy binary
    /// Office files. Reading a .doc's bytes as text yields mojibake, and the file would be
    /// recorded as indexed and never found, which is worse than a gap - a gap is visible and a
    /// later reader can re-queue exactly those rows.</summary>
    private static readonly HashSet<string> NoReader = new(StringComparer.OrdinalIgnoreCase)
        { "doc", "xls", "ppt" };

    /// <summary>Can this build read the words inside that file, or would <see cref="Extract"/>
    /// fall through to reading its bytes as text?</summary>
    public static bool CanExtract(string path) => !NoReader.Contains(Path.GetExtension(path).TrimStart('.'));

    /// <summary><paramref name="beat"/> is called once per page, sheet, slide or chapter - the
    /// unit of progress each format already iterates over - so a watchdog can tell a large
    /// document still being read apart from a decoder that has stopped moving (see
    /// <see cref="IndexerWatch"/>). HTML and plain text have no such loop: <c>Extract</c> reads the
    /// whole file in one call for either, and there is nowhere inside that call to beat from.
    ///
    /// <para><paramref name="archiveBytes"/> is how much a zip-based file may decompress to
    /// (<see cref="ArchiveBudget"/>); it is a parameter so a test can reach the limit without a
    /// quarter-gigabyte fixture.</para>
    /// </summary>
    public static string Extract(string path, Action? beat = null, long archiveBytes = ArchiveBudget.MaxBytes)
    {
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        string text = ext switch
        {
            "pdf" => Pdf(path, beat),
            "docx" => Ooxml(path, beat, archiveBytes, "word/document.xml", "word/footnotes.xml", "word/endnotes.xml"),
            "pptx" => OoxmlAll(path, beat, archiveBytes, "ppt/slides/slide", "ppt/notesSlides/notesSlide"),
            "xlsx" => Xlsx(path, beat, archiveBytes),
            "epub" => Epub(path, beat, archiveBytes),
            "odt" or "ods" or "odp" => Odf(path, beat, archiveBytes),
            "rtf" => RtfText.Read(path, beat),
            "html" or "htm" => StripTags(File.ReadAllText(path)),
            _ => File.ReadAllText(path),
        };
        return Clean(text);
    }

    private static string Pdf(string path, Action? beat)
    {
        var pages = new List<string>();
        var wordsOn = new List<int>();
        int total = 0;
        using (var doc = UglyToad.PdfPig.PdfDocument.Open(path))
        {
            foreach (var page in doc.GetPages())
            {
                // words in reading order, grouped into lines by their baseline: page.Text is the
                // content stream's order, which for a Hebrew PDF is the VISUAL order (the glyphs
                // were laid down left to right) and reads backwards
                var sb = new StringBuilder();
                var words = page.GetWords().ToList();
                var line = new List<UglyToad.PdfPig.Content.Word>();
                double? baseline = null;
                foreach (var w in words)
                {
                    double y = w.BoundingBox.Bottom;
                    if (baseline is double b && Math.Abs(y - b) > w.BoundingBox.Height * 0.6)
                    {
                        sb.Append(LineText(line)).Append('\n');
                        line.Clear();
                    }
                    line.Add(w);
                    baseline = y;
                }
                if (line.Count > 0) sb.Append(LineText(line)).Append('\n');
                pages.Add(sb.ToString());
                wordsOn.Add(words.Count);
                total += sb.Length + 1;
                beat?.Invoke();
                if (total > MaxChars) break;
            }
        }

        // The pages with no text of their own are pictures of pages - a scan, a fax, a phone
        // photo saved as a PDF - and their words are read by OCR, in page order beside the rest.
        // PdfPig's handle is closed first: rendering opens the file again, and nothing here needs
        // two readers on somebody's document at once.
        IReadOnlySet<int> scanned = ScannedPdf.PagesToRead(wordsOn);
        var text = new StringBuilder();
        using var ocr = scanned.Count > 0 ? new ScannedPdf(path, wordsOn.Count(n => n < ScannedPdf.MinWords)) : null;
        for (int i = 0; i < pages.Count; i++)
        {
            text.Append(pages[i]);
            if (ocr is not null && scanned.Contains(i) && text.Length <= MaxChars)
            {
                string read = ocr.Read(i);
                if (read.Length > 0) text.Append(read).Append('\n');
                beat?.Invoke();
            }
            text.Append('\n');
            if (text.Length > MaxChars) break;
        }
        return text.ToString();
    }

    // A line of a Hebrew PDF arrives as visual order: the words right-to-left across the page and
    // each word's letters reversed. Reversing the whole line restores logical order for the Hebrew;
    // the Latin words and numbers inside it then read backwards, so those runs are flipped back.
    private static string LineText(List<UglyToad.PdfPig.Content.Word> line)
    {
        line.Sort((a, b) => a.BoundingBox.Left.CompareTo(b.BoundingBox.Left));
        string visual = string.Join(" ", line.Select(w => w.Text));
        int hebrew = visual.Count(c => c >= '֐' && c <= '׿');
        int letters = visual.Count(char.IsLetter);
        if (letters == 0 || hebrew * 2 < letters) return visual;
        var arr = visual.ToCharArray();
        Array.Reverse(arr);
        // flip the Latin/digit runs back
        int i = 0;
        while (i < arr.Length)
        {
            if (!IsLtr(arr[i])) { i++; continue; }
            int j = i;
            while (j < arr.Length && (IsLtr(arr[j]) || (arr[j] is '.' or ',' or ':' or '/' or '-' or '@' or '_' && j + 1 < arr.Length && IsLtr(arr[j + 1])))) j++;
            Array.Reverse(arr, i, j - i);
            i = j;
        }
        return new string(arr);
    }

    private static bool IsLtr(char c) => char.IsAsciiLetterOrDigit(c);

    // Every zip-based format reads through here: one budget for the whole file, and when it runs
    // out, what was read before it is what the file is indexed with.
    private static string Archive(string path, long maxBytes, Action<ZipArchive, ArchiveBudget, StringBuilder> read)
    {
        var sb = new StringBuilder();
        using var zip = ZipFile.OpenRead(path);
        try { read(zip, new ArchiveBudget(maxBytes), sb); }
        catch (ArchiveBudget.SpentException ex)
        {
            // Once a process: a file that inflates past the limit is rare and the line is the
            // same for every one of them, so the first names the case and the rest add nothing.
            Log.Once("index|zip|limit", "INFO", "index",
                     $"stopped reading {Path.GetFileName(path)} part way ({ex.Message}); the words read before that are kept");
        }
        return sb.ToString();
    }

    // word/document.xml, footnotes.xml and endnotes.xml: a fixed three parts, not one per page -
    // Word does not record page boundaries in the XML at all, so a part is the finest unit this
    // format has to beat on.
    private static string Ooxml(string path, Action? beat, long maxBytes, params string[] parts)
        => Archive(path, maxBytes, (zip, budget, sb) =>
        {
            foreach (var part in parts)
            {
                var e = zip.GetEntry(part);
                if (e is null) continue;
                using var s = budget.Open(e);
                XmlText(s, sb, beat);
                beat?.Invoke();
            }
        });

    // slides and notes are numbered parts; take them in order
    private static string OoxmlAll(string path, Action? beat, long maxBytes, params string[] prefixes)
        => Archive(path, maxBytes, (zip, budget, sb) =>
        {
            var entries = new List<ZipArchiveEntry>();
            foreach (var e in zip.Entries)
                foreach (var p in prefixes)
                    if (e.FullName.StartsWith(p, StringComparison.OrdinalIgnoreCase) && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                        entries.Add(e);
            entries.Sort((a, b) => string.CompareOrdinal(Pad(a.FullName), Pad(b.FullName)));
            foreach (var e in entries)
            {
                using var s = budget.Open(e);
                XmlText(s, sb, beat);
                beat?.Invoke();
                if (sb.Length > MaxChars) break;
            }
        });

    private static string Pad(string name) => Regex.Replace(name, @"\d+", m => m.Value.PadLeft(6, '0'));

    // cells reference a shared-strings table; inline strings and numbers sit in the sheet
    private static string Xlsx(string path, Action? beat, long maxBytes)
        => Archive(path, maxBytes, (zip, budget, sb) =>
        {
            var shared = new List<string>();
            if (zip.GetEntry("xl/sharedStrings.xml") is { } ss)
            {
                using var s = budget.Open(ss);
                using var r = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
                var cur = new StringBuilder();
                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element && r.Name == "si") cur.Clear();
                    else if (r.NodeType == XmlNodeType.Text) cur.Append(r.Value);
                    else if (r.NodeType == XmlNodeType.EndElement && r.Name == "si")
                    {
                        shared.Add(cur.ToString());
                        // Every repeated string in the workbook lives here, parsed before any
                        // worksheet - a real workbook can spend a long, genuinely-progressing
                        // stretch in this loop alone with nothing else in Extract to beat from.
                        // Beat.Throttled upstream already rate-limits the write this drives, so
                        // beating per entry costs nothing extra.
                        beat?.Invoke();
                    }
                }
            }
            foreach (var e in zip.Entries)
            {
                if (!e.FullName.StartsWith("xl/worksheets/sheet", StringComparison.OrdinalIgnoreCase)) continue;
                using var s = budget.Open(e);
                using var r = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
                string? type = null;
                while (r.Read())
                {
                    if (r.NodeType == XmlNodeType.Element && r.Name == "c") type = r.GetAttribute("t");
                    else if (r.NodeType == XmlNodeType.Element && r.Name == "v")
                    {
                        string v = r.ReadElementContentAsString();
                        if (type == "s" && int.TryParse(v, out int idx) && idx < shared.Count) sb.Append(shared[idx]);
                        else sb.Append(v);
                        sb.Append(' ');
                    }
                    else if (r.NodeType == XmlNodeType.Element && r.Name == "t") { sb.Append(r.ReadElementContentAsString()).Append(' '); }
                    else if (r.NodeType == XmlNodeType.EndElement && r.Name == "row")
                    {
                        sb.Append('\n');
                        // A row is this format's own progress unit, the same as a paragraph in a
                        // docx part: one huge sheet with nothing in the shared-strings table used
                        // to beat only once, after the whole sheet had already been read.
                        beat?.Invoke();
                    }
                }
                if (sb.Length > MaxChars) break;
            }
        });

    private static string Epub(string path, Action? beat, long maxBytes)
        => Archive(path, maxBytes, (zip, budget, sb) =>
        {
            var entries = new List<ZipArchiveEntry>();
            foreach (var e in zip.Entries)
                if (e.FullName.EndsWith(".xhtml", StringComparison.OrdinalIgnoreCase) || e.FullName.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
                    || e.FullName.EndsWith(".htm", StringComparison.OrdinalIgnoreCase))
                    entries.Add(e);
            entries.Sort((a, b) => string.CompareOrdinal(Pad(a.FullName), Pad(b.FullName)));
            foreach (var e in entries)
            {
                using var s = budget.Open(e);
                using var rd = new StreamReader(s);
                sb.Append(StripTags(rd.ReadToEnd())).Append('\n');
                beat?.Invoke();
                if (sb.Length > MaxChars) break;
            }
        });

    // OpenDocument (.odt, .ods, .odp): everything a person wrote is in content.xml, under
    // office:body - paragraphs and headings, table cells, list items, and the frames a
    // presentation's slides and notes are made of all hold their words in text:p and text:h.
    private static string Odf(string path, Action? beat, long maxBytes)
        => Archive(path, maxBytes, (zip, budget, sb) =>
        {
            if (zip.GetEntry("content.xml") is not { } e) return;
            using var s = budget.Open(e);
            ReadOdf(s, sb, beat);
        });

    private const string OdfOffice = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private const string OdfText = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const string OdfTable = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string OdfDraw = "urn:oasis:names:tc:opendocument:xmlns:drawing:1.0";
    private const string DublinCore = "http://purl.org/dc/elements/1.1/";

    // Matched by namespace rather than by prefix: "text:" is what every writer uses, and nothing
    // in the format obliges it to. Spaces are markup here - runs of them are text:s, a tab is
    // text:tab - and the whitespace between two spans is a real space between two words.
    private static void ReadOdf(Stream s, StringBuilder sb, Action? beat)
    {
        using var r = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        bool body = false;
        int skipping = -1;      // the depth of an element whose contents are not the document's text
        while (r.Read())
        {
            if (skipping >= 0)
            {
                if (r.NodeType == XmlNodeType.EndElement && r.Depth == skipping) skipping = -1;
                continue;
            }
            switch (r.NodeType)
            {
                case XmlNodeType.Element:
                    if (r.NamespaceURI == OdfOffice && r.LocalName == "body") { body = !r.IsEmptyElement; break; }
                    if (!body) break;
                    // deleted text kept for change tracking, and who wrote a comment and when
                    if ((r.NamespaceURI == OdfText && r.LocalName == "tracked-changes") || r.NamespaceURI == DublinCore)
                    {
                        if (!r.IsEmptyElement) skipping = r.Depth;
                        break;
                    }
                    if (r.NamespaceURI != OdfText) break;
                    if (r.LocalName == "s") sb.Append(' ');
                    else if (r.LocalName == "tab") sb.Append('\t');
                    else if (r.LocalName == "line-break") sb.Append('\n');
                    else if (r.IsEmptyElement && r.LocalName is "p" or "h") sb.Append('\n');
                    break;
                case XmlNodeType.Text or XmlNodeType.CDATA:
                    if (body) sb.Append(r.Value);
                    break;
                case XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace:
                    if (body) sb.Append(' ');
                    break;
                case XmlNodeType.EndElement:
                    if (r.NamespaceURI == OdfText && r.LocalName is "p" or "h")
                    {
                        sb.Append('\n');
                        beat?.Invoke();
                    }
                    else if (r.NamespaceURI == OdfTable && r.LocalName == "table-row") sb.Append('\n');
                    else if (r.NamespaceURI == OdfTable && r.LocalName == "table-cell") sb.Append(' ');
                    else if (r.NamespaceURI == OdfDraw && r.LocalName is "page" or "frame") sb.Append('\n');
                    else if (r.NamespaceURI == OdfOffice && r.LocalName == "body") body = false;
                    break;
            }
            if (sb.Length > MaxChars) return;
        }
    }

    // A part is not the finest unit worth beating on: docx has only three, and in practice nearly
    // everything sits in the one substantial part, word/document.xml, so a beat only at Ooxml's own
    // loop is barely better than one beat before the part and one after. A paragraph end is the
    // natural progress unit inside the part itself, so THIS is where a huge single part beats.
    private static void XmlText(Stream s, StringBuilder sb, Action? beat)
    {
        using var r = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore });
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.Text || r.NodeType == XmlNodeType.CDATA) { sb.Append(r.Value); sb.Append(' '); }
            // a paragraph or a table cell ends a run of words; without this "Total" glues to "12"
            else if (r.NodeType == XmlNodeType.EndElement && r.Name is "w:p" or "a:p" or "w:tc" or "w:tab" or "w:br")
            {
                sb.Append(r.Name == "w:tab" ? ' ' : '\n');
                if (r.Name is "w:p" or "a:p") beat?.Invoke();
            }
            if (sb.Length > MaxChars) return;
        }
    }

    private static string StripTags(string html)
    {
        html = Regex.Replace(html, @"<(script|style)[^>]*>.*?</\1>", " ", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        html = Regex.Replace(html, @"<br\s*/?>|</p>|</div>|</h\d>|</li>|</tr>", "\n", RegexOptions.IgnoreCase);
        html = Regex.Replace(html, "<[^>]+>", " ");
        return System.Net.WebUtility.HtmlDecode(html);
    }

    private static string Clean(string s)
    {
        if (s.Length > MaxChars) s = s[..MaxChars];
        s = s.Replace("\r", "");
        s = Regex.Replace(s, @"[ \t\f\v]+", " ");
        s = Regex.Replace(s, @"\n{3,}", "\n\n");
        return s.Trim();
    }

    /// <summary>Pieces of roughly <paramref name="size"/> characters cut at sentence or line ends,
    /// overlapping by <paramref name="overlap"/> so a sentence on a boundary belongs to both.</summary>
    public static List<string> Chunk(string text, int size = 1200, int overlap = 200, int max = 240)
    {
        var chunks = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return chunks;
        int pos = 0;
        while (pos < text.Length && chunks.Count < max)
        {
            int end = Math.Min(text.Length, pos + size);
            if (end < text.Length)
            {
                int cut = LastBreak(text, pos + size / 2, end);
                if (cut > pos) end = cut;
            }
            string c = text[pos..end].Trim();
            if (c.Length > 40) chunks.Add(c);
            if (end >= text.Length) break;
            pos = Math.Max(pos + 1, end - overlap);
        }
        return chunks;
    }

    /// <summary>Whether a chunk is allowed to claim a meaning.
    ///
    /// <para>A passage embeds to a point, and a passage too short to say one thing rather than
    /// another lands near the middle of the space - which is close to every query at once. Such a
    /// chunk does not score badly against unrelated words; it scores WELL against all of them, and
    /// it out-ranks the documents that actually answer the question. Ordinary English sentences do
    /// this: "Payment complete. Thank you for subscribing." is not junk, it simply does not mean
    /// one thing. Nearly every document ends in one, because <see cref="Chunk"/> keeps anything
    /// over forty characters.</para>
    ///
    /// <para><b>Nothing is skipped by this.</b> The chunk is still stored and still full-text
    /// indexed, exactly as the words read out of a picture are - so every word in it stays
    /// findable and only the vector is withheld. That is the whole difference between this and a
    /// rule that decides on somebody's behalf what is worth reading.</para>
    ///
    /// <para>Three thresholds, because each catches a shape the others let through: sixty short
    /// words can be under four hundred characters, four hundred characters can be nine long ones,
    /// and a boilerplate footer repeated down a page clears both while saying one word.</para>
    /// </summary>
    public const int MinEmbedChars = 400, MinEmbedWords = 60, MinEmbedDistinct = 25;

    public static bool WorthEmbedding(string chunk)
    {
        if (string.IsNullOrWhiteSpace(chunk) || chunk.Length < MinEmbedChars) return false;

        int words = 0;
        var distinct = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        int start = -1;
        for (int i = 0; i <= chunk.Length; i++)
        {
            bool part = i < chunk.Length && (char.IsLetterOrDigit(chunk[i]) || chunk[i] == '\'');
            if (part && start < 0) start = i;
            else if (!part && start >= 0)
            {
                words++;
                distinct.Add(chunk[start..i]);
                start = -1;
            }
        }
        if (words < MinEmbedWords || distinct.Count < MinEmbedDistinct) return false;

        // What is left of a component template after its tags are stripped: button labels and
        // interpolation markers. `.html` is a document extension because people save articles, and
        // on a machine whose work lives in repositories it is mostly Angular and Vue - which the
        // kind table already refuses in every other form it comes in.
        //
        // A DENSITY, never the presence of the characters. A document explaining template syntax
        // is a document, and refusing it would be this same defect one level along.
        int markers = 0;
        for (int i = 1; i < chunk.Length; i++) if (chunk[i] == '{' && chunk[i - 1] == '{') markers++;
        return markers * 200 <= chunk.Length;
    }

    private static int LastBreak(string s, int from, int to)
    {
        for (int i = to - 1; i > from; i--)
            if (s[i] == '\n' || s[i] == '.' || s[i] == '!' || s[i] == '?' || s[i] == '؟') return i + 1;
        for (int i = to - 1; i > from; i--)
            if (s[i] == ' ') return i + 1;
        return -1;
    }
}
