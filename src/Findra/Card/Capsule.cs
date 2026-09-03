using SkiaSharp;

namespace Findra;

public static class CapsuleLayout
{
    public const float Width = 620f;

    /// <summary>Tall enough for the bar AND the progress pill under it with air beneath. The pill
    /// ends 4px from the bottom at 128, which reads as clipped rather than as a margin.</summary>
    public const float Height = 140f;
    public const float BarH = 52f;
    public const float Radius = BarH / 2f;
    public const float Pad = 20f;
    /// <summary>The placeholder's size. A constant because a test measures the string into the
    /// bar, and a literal in the painter would let the two drift.</summary>
    public const float TextSize = 15f;

    /// <summary>The bar somebody clicks. Everything else on the capsule hangs off it.</summary>
    public static SKRect BarRect() =>
        new(0, (Height - BarH) / 2f, Width, (Height + BarH) / 2f);

    // ---- the progress pill --------------------------------------------------------------------

    /// <summary>
    /// The second, smaller pill under the bar: what is being read on the left, a track across the
    /// middle, and how far it has got on the right.
    ///
    /// <para>It used to be a bare track and a line of text floating under the capsule with nothing
    /// around them - the only thing in the product drawn without a container, which read as part of
    /// the desktop rather than part of Findra. A pill says it belongs to the capsule above it.</para>
    /// </summary>
    public const float PillH = 26f;
    public const float PillW = 420f;
    public const float PillGap = 10f;
    public const float PillPad = 12f;
    public const float PillTextSize = 11.5f;

    public const float ProgressH = 3f;

    public static SKRect PillRect()
    {
        float top = BarRect().Bottom + 8f;
        float left = (Width - PillW) / 2f;
        return new SKRect(left, top, left + PillW, top + PillH);
    }
}

/// <summary>
/// The resting look: what sits on the desktop when nothing is open. It is deliberately almost
/// nothing - a field, a magnifier, a placeholder - because the card is what unfolds out of it
/// and the capsule's job is to be unobtrusive until then.
///
/// The progress line under the bar is drawn only when there is something to say. A permanently
/// visible empty bar is what makes an idle widget feel busy.
/// </summary>
public static class CapsulePainter
{
    /// <summary>
    /// What the bar says when nothing has been typed.
    ///
    /// <para>A constant, because there were two. The window drew "Search files, photos, words…"
    /// and <c>--searchshot capsule</c> drew "Search 1.5M files", so every render this project has
    /// ever reviewed - the README's, the website's, every palette sweep - showed a string the
    /// product does not use, and the one it does use has never been looked at. That is the same
    /// defect as a painter branch no state reaches, one level up: the state was shot, and it was
    /// shot with different data.</para>
    /// </summary>
    public const string Placeholder = "Search files, photos, words…";

    public static void Paint(SKCanvas canvas, string placeholder, IndexProgress progress,
                             Derived d, SKTypeface face)
    {
        canvas.Clear(SKColors.Transparent);

        SKRect bar = CapsuleLayout.BarRect();
        var rr = new SKRoundRect(bar, CapsuleLayout.Radius);

        using (var glow = new SKPaint
        {
            Color = d.AccentGlow, IsAntialias = true,
            ImageFilter = SKImageFilter.CreateBlur(10, 10),
        }) canvas.DrawRoundRect(rr, glow);

        using (var fill = new SKPaint { Color = d.Ground, IsAntialias = true })
            canvas.DrawRoundRect(rr, fill);
        using (var wash = new SKPaint { Color = d.AccentSoft, IsAntialias = true })
            canvas.DrawRoundRect(rr, wash);
        using (var edge = new SKPaint
        {
            Color = d.Accent, IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f,
        }) canvas.DrawRoundRect(rr, edge);

        float cy = CapsuleLayout.Height / 2f;
        DrawMagnifier(canvas, CapsuleLayout.Pad + 9f, cy, d);
        CardText.Draw(canvas, placeholder, CapsuleLayout.Pad + 34f, cy + 5f, CapsuleLayout.TextSize, face, d.Dim);

        if (!progress.Show) return;

        // The pill itself, in the same chip fill and edge the settings surface uses for a control
        // that is reporting rather than offering. It is not clickable and it does not pretend to
        // be: no accent, no hover, no glow.
        SKRect pill = CapsuleLayout.PillRect();
        var pr = new SKRoundRect(pill, CapsuleLayout.PillH / 2f);
        using (var fill = new SKPaint { Color = d.Ground, IsAntialias = true })
            canvas.DrawRoundRect(pr, fill);
        using (var chip = new SKPaint { Color = d.Chip, IsAntialias = true })
            canvas.DrawRoundRect(pr, chip);
        using (var edge = new SKPaint
        {
            Color = d.Edge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f,
        }) canvas.DrawRoundRect(pr, edge);

        float ty = pill.MidY + CapsuleLayout.PillTextSize * 0.36f;
        float labelW = CardText.Measure(progress.Label, face, CapsuleLayout.PillTextSize);
        float countW = CardText.Measure(progress.Count, face, CapsuleLayout.PillTextSize);

        CardText.Draw(canvas, progress.Label, pill.Left + CapsuleLayout.PillPad, ty,
                      CapsuleLayout.PillTextSize, face, d.Dim);
        CardText.Draw(canvas, progress.Count, pill.Right - CapsuleLayout.PillPad - countW, ty,
                      CapsuleLayout.PillTextSize, face, d.Dim);

        // Whatever is left between the two, which is why both are measured rather than assumed:
        // "indexing recordings" and "1,234,567 of 2,000,000" are the widest either side gets, and
        // a track laid out from a guess would run underneath one of them.
        float trackLeft = pill.Left + CapsuleLayout.PillPad + labelW + CapsuleLayout.PillGap;
        float trackRight = pill.Right - CapsuleLayout.PillPad - countW - CapsuleLayout.PillGap;
        if (trackRight - trackLeft < 24f) return;

        float py = pill.MidY - CapsuleLayout.ProgressH / 2f;
        var track = new SKRect(trackLeft, py, trackRight, py + CapsuleLayout.ProgressH);
        using (var t = new SKPaint { Color = d.Edge, IsAntialias = true })
            canvas.DrawRoundRect(new SKRoundRect(track, CapsuleLayout.ProgressH / 2f), t);

        var done = new SKRect(track.Left, track.Top,
                              track.Left + track.Width * Math.Clamp(progress.Fraction, 0, 1),
                              track.Bottom);
        using (var f = new SKPaint { Color = d.Accent, IsAntialias = true })
            canvas.DrawRoundRect(new SKRoundRect(done, CapsuleLayout.ProgressH / 2f), f);
    }

    private static void DrawMagnifier(SKCanvas canvas, float cx, float cy, Derived d)
    {
        using var p = new SKPaint
        {
            Color = d.Ink, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f,
            StrokeCap = SKStrokeCap.Round,
        };
        canvas.DrawCircle(cx, cy - 1f, 7f, p);
        canvas.DrawLine(cx + 5f, cy + 4f, cx + 10f, cy + 9f, p);
    }
}
