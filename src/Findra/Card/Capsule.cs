using SkiaSharp;

namespace Findra;

public static class CapsuleLayout
{
    public const float Width = 560f;
    public const float Height = 128f;
    public const float BarH = 52f;
    public const float Radius = BarH / 2f;
    public const float Pad = 20f;
    public const float ProgressH = 3f;
    public const float ProgressW = 260f;
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
    public static void Paint(SKCanvas canvas, string placeholder, string progress,
                             float progressFraction, Derived d, SKTypeface face)
    {
        canvas.Clear(SKColors.Transparent);

        var bar = new SKRect(0, (CapsuleLayout.Height - CapsuleLayout.BarH) / 2f,
                             CapsuleLayout.Width, (CapsuleLayout.Height + CapsuleLayout.BarH) / 2f);
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
        CardText.Draw(canvas, placeholder, CapsuleLayout.Pad + 34f, cy + 5f, 15f, face, d.Dim);

        if (progress.Length == 0) return;

        float py = bar.Bottom + 12f;
        var track = new SKRect(CapsuleLayout.Pad, py, CapsuleLayout.Pad + CapsuleLayout.ProgressW,
                               py + CapsuleLayout.ProgressH);
        using (var t = new SKPaint { Color = d.Edge, IsAntialias = true })
            canvas.DrawRoundRect(new SKRoundRect(track, CapsuleLayout.ProgressH / 2f), t);

        var done = new SKRect(track.Left, track.Top,
                              track.Left + CapsuleLayout.ProgressW * Math.Clamp(progressFraction, 0, 1),
                              track.Bottom);
        using (var f = new SKPaint { Color = d.Accent, IsAntialias = true })
            canvas.DrawRoundRect(new SKRoundRect(done, CapsuleLayout.ProgressH / 2f), f);

        CardText.Draw(canvas, progress, CapsuleLayout.Pad + CapsuleLayout.ProgressW + 12f,
                      py + CapsuleLayout.ProgressH + 3f, 10.5f, face, d.Dim);
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
