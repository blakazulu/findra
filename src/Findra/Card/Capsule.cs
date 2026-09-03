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
    /// Where the progress pill goes: under the bar, inset to the bar's own ends so the two read as
    /// one object rather than a widget with something parked beneath it.
    ///
    /// <para>It used to be a bare track and a line of text floating under the capsule with nothing
    /// around them - the only thing in the product drawn without a container, which read as part of
    /// the desktop rather than part of Findra.</para>
    /// </summary>
    public static SKRect PillRect()
    {
        float top = BarRect().Bottom + 8f;
        return new SKRect(Pad, top, Width - Pad, top + ProgressPillLayout.Height);
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

        // Drawn by ProgressPill, which the card's own pill also goes through: the capsule and
        // the card show the same fact, and two painters is two answers waiting to differ. This
        // surface decides only where it goes.
        ProgressPill.Paint(canvas, CapsuleLayout.PillRect(), progress, d, face);
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
