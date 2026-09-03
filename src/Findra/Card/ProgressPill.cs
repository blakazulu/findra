using SkiaSharp;

namespace Findra;

/// <summary>
/// The progress pill: a ring, what is being read, how far it has got, and the percentage at the
/// far end. The pill itself is the bar - the fill runs left to right underneath the words rather
/// than beside them, so the shape carries the number twice and the eye can read it without
/// reading it.
///
/// <para>Two surfaces draw it - the card under its field, and the desktop capsule under its bar -
/// and both draw it through this. They are the same fact seen in two places, and a person with
/// both in front of them must not be shown two answers or two shapes of one answer. Each supplies
/// its own rectangle, because the two are different widths and that is the only thing about it
/// they are allowed to disagree on.</para>
///
/// <para>It reports and nothing else: no hover, no accent outline, no cursor of its own. Nothing
/// on it answers a click, and it is drawn so that nobody tries.</para>
/// </summary>
public static class ProgressPillLayout
{
    public const float Height = 26f;

    /// <summary>The ring's radius, and the room its column takes on the left.</summary>
    public const float Ring = 6f;
    public const float RingStroke = 3f;
    public const float Inset = 12f;

    /// <summary>Text size, taken from the height so the pill scales as one thing.</summary>
    public const float TextSize = Height * 0.46f;

    /// <summary>Kept clear on the right for the percentage, which is right-aligned into it.</summary>
    public const float PercentW = 44f;
}

public static class ProgressPill
{
    /// <summary>What the pill says in the middle: what is being read, then how far. One string,
    /// because it is ellipsised as one - a sentence cut between its two halves reads worse than a
    /// sentence cut at its end.</summary>
    public static string Sentence(IndexProgress p) =>
        p.Label.Length > 0 && p.Count.Length > 0 ? p.Label + " · " + p.Count
        : p.Label.Length > 0 ? p.Label
        : p.Count;

    public static void Paint(SKCanvas canvas, SKRect r, IndexProgress progress, Derived d, SKTypeface face)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(d);
        if (!progress.Show) return;

        float frac = Math.Clamp(progress.Fraction, 0, 1);
        var pill = new SKRoundRect(r, r.Height / 2f);

        using (var bg = new SKPaint { Color = d.Chip, IsAntialias = true })
            canvas.DrawRoundRect(pill, bg);

        // The pill IS the bar. Clipped to its own round rect so the fill keeps the shape's ends
        // rather than squaring them off, and washed rather than solid, because the words sit on
        // top of it and have to stay the thing being read.
        if (frac > 0)
        {
            canvas.Save();
            canvas.ClipRoundRect(pill, antialias: true);
            using var fill = new SKPaint { Color = d.Accent.WithAlpha(64), IsAntialias = true };
            canvas.DrawRect(new SKRect(r.Left, r.Top, r.Left + r.Width * frac, r.Bottom), fill);
            canvas.Restore();
        }

        using (var edge = new SKPaint
        {
            Color = d.Accent.WithAlpha(90), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f,
        }) canvas.DrawRoundRect(pill, edge);

        // The ring says the same thing a third way, and it is the part that reads at a glance from
        // across a desk where the digits do not.
        float cx = r.Left + ProgressPillLayout.Inset + ProgressPillLayout.Ring;
        float cy = r.MidY;
        using (var track = new SKPaint
        {
            Color = d.Fade(40), IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = ProgressPillLayout.RingStroke,
        }) canvas.DrawCircle(cx, cy, ProgressPillLayout.Ring, track);

        if (frac > 0)
            using (var arc = new SKPaint
            {
                Color = d.Accent, IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = ProgressPillLayout.RingStroke, StrokeCap = SKStrokeCap.Butt,
            })
                canvas.DrawArc(
                    new SKRect(cx - ProgressPillLayout.Ring, cy - ProgressPillLayout.Ring,
                               cx + ProgressPillLayout.Ring, cy + ProgressPillLayout.Ring),
                    // From the top, clockwise, which is the only direction a dial reads.
                    -90, 360 * frac, useCenter: false, arc);

        float size = ProgressPillLayout.TextSize;
        float tx = cx + ProgressPillLayout.Ring + 8f;
        float ty = r.MidY + size * 0.36f;
        float room = r.Right - tx - ProgressPillLayout.PercentW;

        CardText.Draw(canvas, CardText.Ellipsize(Sentence(progress), face, size, room),
                      tx, ty, size, face, d.Fade(200));

        // The number where a bar's number belongs. Rounded, never a decimal: a percentage that
        // ticks over hundredths is a thing that looks busy rather than a thing that says how far.
        string percent = ((int)(frac * 100)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "%";
        float pw = CardText.Measure(percent, face, size);
        CardText.Draw(canvas, percent, r.Right - ProgressPillLayout.Inset - pw, ty, size, face, d.Fade(150));
    }
}
