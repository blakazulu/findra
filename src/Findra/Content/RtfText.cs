using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Findra;

/// <summary>
/// The words in an RTF file, and nothing else in it.
///
/// <para>RTF is ASCII, so reading it as text does find its words - beside every control word,
/// font name and colour table, indexed as words in their own right. This reads the format instead:
/// groups that are not body text (the font, colour and style tables, document properties,
/// pictures, field instructions, and every group marked <c>\*</c>, which a reader is allowed to
/// ignore) are passed over, paragraph and line marks become line breaks, and characters are
/// decoded in the code page the document says it was written in.</para>
///
/// <para>Hebrew arrives two ways and both are read: as <c>\uN</c> Unicode with a fallback that is
/// skipped (<c>\ucN</c> says how long), or as <c>\'hh</c> bytes in the document's code page
/// (<c>\ansicpg1255</c>) or in the font's (<c>\fcharset177</c> in the font table).</para>
///
/// <para>This runs over whatever is on somebody's disk, so nothing here throws on bad input: an
/// unbalanced brace is ignored, an unknown control word is dropped, and a file that ends early
/// gives back what was read up to there.</para>
/// </summary>
public static class RtfText
{
    /// <summary>How deep groups are followed. Real documents nest a few dozen deep; past this a
    /// brace still counts, so the file is read correctly, but no more state is kept for it -
    /// otherwise a file of nothing but opening braces is a way to make this allocate without
    /// limit.</summary>
    private const int MaxDepth = 1000;

    /// <summary>Beat after this many bytes with no paragraph in them: a picture's hex runs to
    /// megabytes with no <c>\par</c> anywhere, and the watchdog needs to hear from it.</summary>
    private const int BeatEveryBytes = 1 << 20;

    // Destinations that are not the document's text. Anything marked \* is skipped as well.
    private static readonly HashSet<string> NotText = new(StringComparer.Ordinal)
    {
        "fonttbl", "colortbl", "stylesheet", "info", "pict", "object", "nonshppict", "listtable",
        "listoverridetable", "revtbl", "rsidtbl", "generator", "xmlnstbl", "themedata",
        "colorschememapping", "latentstyles", "datastore", "fldinst", "filetbl", "pgdsctbl",
        "mmathPr", "header", "headerl", "headerr", "headerf", "footer", "footerl", "footerr",
        "footerf", "private", "userprops", "xe", "tc", "bkmkstart", "bkmkend",
    };

    static RtfText() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private struct Group
    {
        public bool Skip;       // inside a destination that is not body text
        public bool Fonts;      // inside the font table
        public int Uc;          // fallback characters after each \uN
        public int Font;        // -1: the document's default font
    }

    public static string Read(string path, Action? beat = null, int maxChars = DocText.MaxChars)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        return Read(fs, beat, maxChars);
    }

    public static string Read(Stream input, Action? beat = null, int maxChars = DocText.MaxChars)
    {
        ArgumentNullException.ThrowIfNull(input);
        var r = new Reader(input);
        var sb = new StringBuilder();
        var bytes = new List<byte>();       // \'hh bytes waiting to be decoded together
        Encoding? bytesAs = null;

        var stack = new Stack<Group>();
        var g = new Group { Uc = 1, Font = -1 };
        int beyond = 0;                     // groups deeper than MaxDepth, counted only
        int docPage = 1252, deff = -1, fontId = -1;
        var fontPage = new Dictionary<int, int>();
        int skipFallback = 0;
        long sinceBeat = 0;
        var word = new char[32];

        Encoding Current()
        {
            int f = g.Font >= 0 ? g.Font : deff;
            return Page(f >= 0 && fontPage.TryGetValue(f, out int cp) ? cp : docPage);
        }

        void Flush()
        {
            if (bytes.Count == 0) return;
            sb.Append((bytesAs ?? Page(docPage)).GetString(bytes.ToArray()));
            bytes.Clear();
        }

        void Text(char c) { if (g.Skip) return; Flush(); sb.Append(c); }

        void Byte(byte b)
        {
            if (g.Skip) return;
            Encoding e = Current();
            if (!ReferenceEquals(e, bytesAs)) Flush();
            bytesAs = e;
            bytes.Add(b);
        }

        while (sb.Length <= maxChars)
        {
            int c = r.Next();
            if (c < 0) break;
            if (++sinceBeat >= BeatEveryBytes) { sinceBeat = 0; beat?.Invoke(); }

            if (c == '{')
            {
                skipFallback = 0;
                if (stack.Count >= MaxDepth) { beyond++; continue; }
                stack.Push(g);
                continue;
            }
            if (c == '}')
            {
                skipFallback = 0;
                if (beyond > 0) { beyond--; continue; }
                if (stack.Count == 0) continue;       // unbalanced: ignored
                g = stack.Pop();
                continue;
            }
            if (c == '\r' || c == '\n') continue;     // line breaks in the file are not in the text

            if (c != '\\')
            {
                if (skipFallback > 0) { skipFallback--; continue; }
                if (c < 0x80) Text((char)c);
                else Byte((byte)c);
                continue;
            }

            // a control symbol or a control word
            int n = r.Next();
            if (n < 0) break;
            if (!IsLetter(n))
            {
                switch (n)
                {
                    case '\'':
                        int hi = Hex(r.Next()), lo = Hex(r.Next());
                        if (hi < 0 || lo < 0) break;
                        if (skipFallback > 0) { skipFallback--; break; }
                        Byte((byte)(hi * 16 + lo));
                        break;
                    case '*':
                        // "\*" opens a destination a reader may ignore; it is always the first
                        // thing in its group, so the group just opened is the one to skip
                        g.Skip = true;
                        break;
                    case '\\': case '{': case '}':
                        if (skipFallback > 0) { skipFallback--; break; }
                        Text((char)n);
                        break;
                    case '~': Text(' '); break;
                    case '_': Text('-'); break;
                    case '\r': case '\n': Text('\n'); break;   // "\" then a line break is \par
                    // \- (optional hyphen), \: and anything else say nothing
                }
                continue;
            }

            // a control word: letters, then an optional signed number, then an optional space
            int len = 0;
            while (n >= 0 && IsLetter(n))
            {
                if (len < word.Length) word[len++] = (char)n;
                n = r.Next();
            }
            bool neg = false, hasArg = false;
            long arg = 0;
            if (n == '-') { neg = true; n = r.Next(); }
            while (n >= 0 && n is >= '0' and <= '9')
            {
                hasArg = true;
                if (arg < 100_000_000) arg = arg * 10 + (n - '0');
                n = r.Next();
            }
            if (neg) arg = -arg;
            if (n >= 0 && n != ' ') r.Back(n);
            int a = (int)arg;
            string w = new(word, 0, len);

            if (w == "bin")
            {
                // raw bytes follow, which may contain anything - braces included
                r.Skip(hasArg && arg > 0 ? arg : 0);
                continue;
            }
            if (w == "u" && hasArg)
            {
                if (!g.Skip) Text((char)(ushort)(a < 0 ? a + 65536 : a));
                skipFallback = g.Uc;
                continue;
            }
            if (NotText.Contains(w))
            {
                g.Skip = true;
                if (w == "fonttbl") g.Fonts = true;
                continue;
            }

            if (g.Fonts)
            {
                // inside the font table: which font says which code page
                if (w == "f" && hasArg) fontId = a;
                else if (w == "fcharset" && hasArg && fontId >= 0 && CharsetPage(a) is int cp) fontPage[fontId] = cp;
                else if (w == "cpg" && hasArg && fontId >= 0 && a > 0) fontPage[fontId] = a;
                continue;
            }

            switch (w)
            {
                case "ansicpg": if (hasArg && a > 0) docPage = a; break;
                case "mac": docPage = 10000; break;
                case "pc": docPage = 437; break;
                case "pca": docPage = 850; break;
                case "deff": if (hasArg) deff = a; break;
                // capped: a fallback is a character or two, and a huge count would skip real text
                case "uc": if (hasArg && a >= 0) g.Uc = Math.Min(a, 8); break;
                case "f": if (hasArg) { Flush(); g.Font = a; } break;
                case "par": case "line": case "sect": case "page": case "row":
                    Text('\n');
                    if (w == "par") { beat?.Invoke(); sinceBeat = 0; }
                    break;
                case "tab": Text('\t'); break;
                case "cell": case "nestcell": Text(' '); break;
                case "emdash": case "endash": Text('-'); break;
                case "lquote": case "rquote": Text('\''); break;
                case "ldblquote": case "rdblquote": Text('"'); break;
                case "bullet": case "emspace": case "enspace": case "qmspace": Text(' '); break;
            }
        }
        Flush();
        return sb.ToString();
    }

    private static bool IsLetter(int c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static int Hex(int c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    /// <summary>The code page a font table's <c>\fcharsetN</c> names. Null for the charsets that
    /// mean "the document's own" (ANSI, default, symbol).</summary>
    private static int? CharsetPage(int charset) => charset switch
    {
        77 => 10000, 128 => 932, 129 => 949, 130 => 1361, 134 => 936, 136 => 950,
        161 => 1253, 162 => 1254, 163 => 1258, 177 => 1255, 178 => 1256, 186 => 1257,
        204 => 1251, 222 => 874, 238 => 1250, 254 => 437, 255 => 850,
        _ => null,
    };

    private static readonly Dictionary<int, Encoding> Pages = [];

    private static Encoding Page(int cp)
    {
        lock (Pages)
        {
            if (Pages.TryGetValue(cp, out Encoding? e)) return e;
            try { e = Encoding.GetEncoding(cp); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
            {
                e = cp == 1252 ? Encoding.Latin1 : Page(1252);
            }
            Pages[cp] = e;
            return e;
        }
    }

    /// <summary>A byte at a time over a buffered stream, with one byte of push-back.</summary>
    private sealed class Reader(Stream s)
    {
        private readonly byte[] _buf = new byte[1 << 16];
        private int _pos, _len;
        private int _back = -1;

        public int Next()
        {
            if (_back >= 0) { int b = _back; _back = -1; return b; }
            if (_pos == _len)
            {
                _len = s.Read(_buf, 0, _buf.Length);
                _pos = 0;
                if (_len <= 0) { _len = 0; return -1; }
            }
            return _buf[_pos++];
        }

        public void Back(int b) => _back = b;

        public void Skip(long n)
        {
            if (n > 0 && _back >= 0) { _back = -1; n--; }
            while (n > 0)
            {
                if (_pos == _len)
                {
                    _len = s.Read(_buf, 0, _buf.Length);
                    _pos = 0;
                    if (_len <= 0) { _len = 0; return; }
                }
                int take = (int)Math.Min(n, _len - _pos);
                _pos += take;
                n -= take;
            }
        }
    }
}
