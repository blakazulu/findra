using SkiaSharp;

namespace Findra;

/// <summary>
/// The drawing vocabulary the settings window and the first-run screen share: a header, a label,
/// a toggle, a pill, a palette swatch, a progress bar and a wrapped note. Every colour comes from
/// <see cref="Derived"/>, so a palette is still four constants.
///
/// <para>This is where <see cref="Derived.Tile"/> and <see cref="Derived.Chip"/> stop being
/// reserved and start being painted, which is why both join the legibility check in
/// <c>--searchtest</c>.</para>
/// </summary>
public static class Parts
{
    /// <summary>
    /// The face every surface draws with, and the one every test measures with.
    ///
    /// <para>One resolver, because a test that measures a label with one typeface while the
    /// painter draws it with another is not a test of anything - and because spec §9a's promise
    /// that a README screenshot is the product rather than a mockup only holds if the product
    /// looks the same on the machine that regenerates it.</para>
    ///
    /// <para>Resolved ONCE. The settings window repaints on every pointer move and measures
    /// several labels per repaint, so a property that loaded the stream per access would allocate
    /// a typeface per measurement.</para>
    ///
    /// <para>Quicksand is bundled under the SIL Open Font License 1.1; see
    /// <c>assets/fonts/OFL.txt</c>, which ships beside the executable as
    /// <c>OFL-Quicksand.txt</c>, and <c>NOTICE</c>.</para>
    /// </summary>
    public static SKTypeface Face { get; } =
        Resolve(typeof(Parts).Assembly.GetManifestResourceStream("Findra.Quicksand-Regular.ttf"));

    /// <summary>
    /// The face in <paramref name="stream"/>, or the system default when there is not one.
    ///
    /// <para>Never throws, and that is the point: this runs in a static initialiser, and a type
    /// initialiser that throws surfaces as a <c>TypeInitializationException</c> from wherever the
    /// type is first touched - out of a paint, out of a measure, out of a shot - which is
    /// unreportable. A missing font is a product that looks wrong, not one that will not start.
    /// </para>
    ///
    /// <para>Public rather than internal only so the fallback can be tested: this assembly grants
    /// no <c>InternalsVisibleTo</c>, and every other seam the tests reach is public too. It is a
    /// seam, not an API - nothing but <see cref="Face"/> should call it.</para>
    /// </summary>
    public static SKTypeface Resolve(Stream? stream)
    {
        if (stream is null)
        {
            Log.Warn("look", "the bundled typeface is missing; falling back to the system default");
            return SKTypeface.Default;
        }

        try
        {
            using (stream)
            {
                SKTypeface? face = SKTypeface.FromStream(stream);
                if (face is not null) return face;
            }
        }
        catch (Exception ex)
        {
            Log.Warn("look", "the bundled typeface could not be read: " + ex.Message);
        }

        Log.Warn("look", "the bundled typeface could not be read; falling back to the system default");
        return SKTypeface.Default;
    }

    public const float HeaderSize = 15f;
    public const float LabelSize = 13f;
    public const float NoteSize = 11.5f;
    /// <summary>Air between two wrapped lines of a note.</summary>
    public const float NoteLeading = 4f;
    /// <summary>Air between the row above and the first line of its note.</summary>
    public const float NoteTop = 4f;

    /// <summary>
    /// How tall a note of <paramref name="lines"/> lines is - which is how far the layout pushes
    /// every row below it. Zero lines is zero height: a row with no note must not reserve one
    /// line's worth of air, or every section drifts down by the number of rows that have none.
    /// </summary>
    public static float NoteHeight(int lines) =>
        lines <= 0 ? 0f : NoteTop + lines * (NoteSize + NoteLeading);

    /// <summary>
    /// Greedy word wrap, measured with the face and size it will be drawn at.
    ///
    /// <para>Four rules that are easy to get wrong and invisible when they are: a word that caused
    /// an overflow is carried to the next line rather than dropped; a word that cannot fit any
    /// line is emitted alone rather than looped on; empty input produces no lines at all rather
    /// than one blank one; and a column with no width produces ONE line rather than one line per
    /// word - a zero width reaches this from a pane that has not been laid out yet, and answering
    /// with a line per word would have the layout reserve a note band as tall as the sentence has
    /// words.</para>
    /// </summary>
    public static IReadOnlyList<string> Wrap(string text, SKTypeface face, float size, float maxWidth)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text)) return lines;
        if (maxWidth <= 0) { lines.Add(text.Trim()); return lines; }

        string[] words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = new System.Text.StringBuilder();

        foreach (string word in words)
        {
            string candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && CardText.Measure(candidate, face, size) > maxWidth)
            {
                lines.Add(line.ToString());
                line.Clear();
                line.Append(word);        // carried, never dropped
            }
            else
            {
                line.Clear();
                line.Append(candidate);
            }
        }

        if (line.Length > 0) lines.Add(line.ToString());
        return lines;
    }

    public static void Header(SKCanvas canvas, string text, SKRect r, Derived d, SKTypeface face) =>
        CardText.Draw(canvas, text, r.Left, r.Top + HeaderSize, HeaderSize, face, d.Ink);

    public static void Label(SKCanvas canvas, string text, SKRect r, float labelWidth, Derived d, SKTypeface face) =>
        CardText.Draw(canvas, CardText.Ellipsize(text, face, LabelSize, labelWidth - 12),
                      r.Left, r.MidY + LabelSize * 0.36f, LabelSize, face, d.Ink);

    /// <summary>A note under a control, in secondary ink, into the rectangle the layout reserved
    /// for exactly this many lines.</summary>
    public static void Note(SKCanvas canvas, string text, SKRect r, Derived d, SKTypeface face)
    {
        float y = r.Top + NoteTop + NoteSize;
        foreach (string line in Wrap(text, face, NoteSize, r.Width))
        {
            CardText.Draw(canvas, line, r.Left, y, NoteSize, face, d.Fade(150));
            y += NoteSize + NoteLeading;
        }
    }

    public static void Toggle(SKCanvas canvas, SKRect r, bool on, bool hovered, Derived d)
    {
        var track = new SKRect(r.Right - 44, r.MidY - 11, r.Right, r.MidY + 11);
        using (var fill = new SKPaint { Color = on ? d.RowSelected : hovered ? d.RowHover : d.Row, IsAntialias = true })
            canvas.DrawRoundRect(new SKRoundRect(track, 11f), fill);
        using (var edge = new SKPaint { Color = d.Edge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f })
            canvas.DrawRoundRect(new SKRoundRect(track, 11f), edge);
        using (var knob = new SKPaint { Color = on ? d.Accent : d.Fade(120), IsAntialias = true })
            canvas.DrawCircle(on ? track.Right - 11 : track.Left + 11, track.MidY, 8f, knob);
    }

    public static void Pill(SKCanvas canvas, SKRect r, string text, bool chosen, bool hovered,
                            Derived d, SKTypeface face)
    {
        var rr = new SKRoundRect(r, r.Height / 2f);
        using (var fill = new SKPaint { Color = chosen ? d.RowSelected : hovered ? d.RowHover : d.Chip, IsAntialias = true })
            canvas.DrawRoundRect(rr, fill);
        using (var edge = new SKPaint
        {
            Color = chosen ? d.Accent : d.Edge, IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = chosen ? 1.5f : 1f,
        }) canvas.DrawRoundRect(rr, edge);

        string shown = CardText.Ellipsize(text, face, LabelSize, r.Width - 12);
        float w = CardText.Measure(shown, face, LabelSize);
        CardText.Draw(canvas, shown, r.MidX - w / 2f, r.MidY + LabelSize * 0.36f, LabelSize, face, d.Ink);
    }

    /// <summary>A palette's own colours, shown in its own colours - the only place in either
    /// surface where a colour is not the current palette's.</summary>
    public static void Swatch(SKCanvas canvas, SKRect r, Palette p, bool chosen, bool hovered, Derived d)
    {
        var rr = new SKRoundRect(r, 8f);
        using (var ground = new SKPaint { Color = p.Ground, IsAntialias = true }) canvas.DrawRoundRect(rr, ground);
        using (var accent = new SKPaint { Color = p.Accent, IsAntialias = true })
            canvas.DrawCircle(r.Left + 14, r.MidY, 7f, accent);
        using (var ink = new SKPaint { Color = p.Ink, IsAntialias = true })
            canvas.DrawCircle(r.Left + 30, r.MidY, 5f, ink);
        using (var edge = new SKPaint
        {
            Color = chosen ? d.Accent : hovered ? d.Fade(150) : d.Edge, IsAntialias = true,
            Style = SKPaintStyle.Stroke, StrokeWidth = chosen ? 2f : 1f,
        }) canvas.DrawRoundRect(rr, edge);
    }

    public static void Bar(SKCanvas canvas, SKRect r, float fraction, Derived d)
    {
        using (var track = new SKPaint { Color = d.Edge, IsAntialias = true })
            canvas.DrawRoundRect(new SKRoundRect(r, r.Height / 2f), track);
        var done = new SKRect(r.Left, r.Top, r.Left + r.Width * Math.Clamp(fraction, 0f, 1f), r.Bottom);
        if (done.Width <= 0) return;
        using (var fill = new SKPaint { Color = d.Accent, IsAntialias = true })
            canvas.DrawRoundRect(new SKRoundRect(done, r.Height / 2f), fill);
    }
}
