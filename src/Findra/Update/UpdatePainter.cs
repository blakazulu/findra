using SkiaSharp;

namespace Findra;

/// <summary>Paints the update window from an <see cref="UpdateView"/>. The window and
/// <c>--searchshot</c> both call it, so the shot is the window.</summary>
public static class UpdatePainter
{
    public static int BodyLines(UpdateView v, SKTypeface face) =>
        Parts.Wrap(UpdatePrompt.Body(v), face, Parts.NoteSize, UpdatePrompt.Width - 2 * UpdatePrompt.Pad).Count;

    public static SKRect Surface(UpdateView v, SKTypeface face) =>
        UpdatePrompt.Panel(BodyLines(v, face), UpdatePrompt.Buttons(v), UpdatePrompt.HasBar(v));

    public static void Paint(SKCanvas canvas, UpdateView v, UpdatePromptTarget hover, Derived d, SKTypeface face)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(v);
        int lines = BodyLines(v, face);
        int buttons = UpdatePrompt.Buttons(v);
        SKRect panel = UpdatePrompt.Panel(lines, buttons, UpdatePrompt.HasBar(v));

        using (var fill = new SKPaint { Color = d.Tile, IsAntialias = true })
            canvas.DrawRoundRect(new SKRoundRect(panel, UpdatePrompt.Radius), fill);
        using (var edge = new SKPaint
        { Color = d.Accent.WithAlpha(96), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f })
            canvas.DrawRoundRect(new SKRoundRect(SKRect.Inflate(panel, -0.7f, -0.7f), UpdatePrompt.Radius), edge);

        float x = panel.Left + UpdatePrompt.Pad;
        CardText.Draw(canvas, UpdatePrompt.Title(v), x, panel.Top + UpdatePrompt.Pad + UpdatePrompt.TitleSize,
                      UpdatePrompt.TitleSize, face, d.Ink);
        Parts.Note(canvas, UpdatePrompt.Body(v),
                   new SKRect(x, panel.Top + UpdatePrompt.Pad + UpdatePrompt.TitleSize + 14f,
                              panel.Right - UpdatePrompt.Pad, panel.Bottom - UpdatePrompt.Pad),
                   d, face);

        if (UpdatePrompt.HasBar(v))
        {
            float fraction = v.Total > 0 ? Math.Clamp(v.Got / (float)v.Total, 0f, 1f) : 0f;
            ProgressPill.Paint(canvas, UpdatePrompt.Bar(panel, lines),
                               new IndexProgress("Downloading", UpdatePrompt.Count(v), fraction, Show: true), d, face);
        }

        if (buttons >= 1)
            Parts.Pill(canvas, UpdatePrompt.Button(panel, 0, buttons), UpdatePrompt.CloseLabel(v),
                       chosen: buttons == 1, hovered: hover == UpdatePromptTarget.Close, d, face);
        if (buttons == 2)
            Parts.Pill(canvas, UpdatePrompt.Button(panel, 1, buttons), UpdatePrompt.GoLabel(v),
                       chosen: true, hovered: hover == UpdatePromptTarget.Go, d, face);
    }
}
