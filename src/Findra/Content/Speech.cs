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
///
/// <para><see cref="Windows"/> is the better answer to that same problem, and is the one production
/// takes: it cuts the windows and embeds nothing at all, so the rule needs no delegate to be
/// testable and the caller embeds a batch at a time rather than paying a round trip per window.</para>
/// </summary>
public static class Speech
{
    /// <summary>One window of a transcript, before anything has embedded it. Cutting and embedding
    /// are separate so the embedding can be done a batch at a time - on the accelerator that is the
    /// difference between 134 segments a second and 408 - while the windowing rule, which is the
    /// part with an off-by-one in it, stays testable with no model on disk.</summary>
    public readonly record struct Window(double T0, double T1, string Text);

    public static List<Window> Windows(IReadOnlyList<Media.Line> lines, double maxSeconds = 20, int maxChars = 600)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var windows = new List<Window>();
        var buf = new StringBuilder();
        double t0 = -1, t1 = 0;

        void Flush()
        {
            if (buf.Length == 0) return;
            string text = buf.ToString().Trim();
            windows.Add(new Window(t0, t1, text));
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
        return windows;
    }

    public static List<ContentDb.Segment> Merge(IReadOnlyList<Media.Line> lines, Func<string, long> embed,
                                                double maxSeconds = 20, int maxChars = 600)
    {
        ArgumentNullException.ThrowIfNull(embed);
        var segs = new List<ContentDb.Segment>();
        foreach (Window w in Windows(lines, maxSeconds, maxChars))
            segs.Add(new ContentDb.Segment(ContentDb.SegSpeech, w.T0, w.T1, embed(w.Text), w.Text));
        return segs;
    }
}
