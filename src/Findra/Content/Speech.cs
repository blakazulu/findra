using System;
using System.Collections.Generic;
using System.Text;

namespace Findra;

/// <summary>
/// Transcript lines into searchable segments.
///
/// <para>Whisper emits two- or three-second lines, and one segment per line makes a phrase that
/// spans two of them findable in neither. They are merged into windows a sentence comfortably
/// fits inside - about twenty seconds, or six hundred characters, whichever comes first - and
/// each window keeps the start of its first line and the end of its last, so a result can say
/// when it was said and the card can seek there.</para>
///
/// <para><paramref name="embed"/> hands back the vector row the window's text was appended at.
/// Passing it in rather than holding an encoder is what lets the windowing rule - the part with
/// an off-by-one in it - be tested without a model on disk.</para>
/// </summary>
public static class Speech
{
    public static List<ContentDb.Segment> Merge(IReadOnlyList<Media.Line> lines, Func<string, long> embed,
                                                double maxSeconds = 20, int maxChars = 600)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(embed);
        var segs = new List<ContentDb.Segment>();
        var buf = new StringBuilder();
        double t0 = -1, t1 = 0;

        void Flush()
        {
            if (buf.Length == 0) return;
            string text = buf.ToString().Trim();
            segs.Add(new ContentDb.Segment(ContentDb.SegSpeech, t0, t1, embed(text), text));
            buf.Clear();
            t0 = -1;
        }

        foreach (Media.Line l in lines)
        {
            if (t0 < 0) t0 = l.T0;
            buf.Append(l.Text).Append(' ');
            t1 = l.T1;
            if (t1 - t0 >= maxSeconds || buf.Length > maxChars) Flush();
        }
        // The tail. A loop that only writes on overflow loses whatever was in the buffer when
        // the input ran out, which is the end of every transcript on the machine.
        Flush();
        return segs;
    }
}
