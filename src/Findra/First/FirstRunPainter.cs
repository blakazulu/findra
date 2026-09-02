using System;
using System.Collections.Generic;

using SkiaSharp;

namespace Findra;

/// <summary>
/// Where everything sits on the first-run screen.
///
/// <para>A PURE FUNCTION of its arguments, called by the painter and by the hit test, exactly as
/// <see cref="RailLayout"/> is for the settings window and <c>SearchCard</c> is for the card. No
/// stored rectangles, so a pointer event can never race a layout.</para>
/// </summary>
public static class FirstRunLayout
{
    public const float Width = RailLayout.Width;      // the same object, seen once

    /// <summary>Taller than the settings window, and the number is arithmetic rather than taste:
    /// a title and a sentence, three preset tiles, five rows where Hebrew is offered, a full row's
    /// worth of dead air, three switches with the update disclosure wrapped between the second and
    /// the third, a summary line, and two buttons.</summary>
    public const float Height = 820f;
    public const float Pad = RailLayout.Pad;
    public const float Radius = RailLayout.Radius;

    public const float TileTop = 96f;
    public const float TileH = 104f;
    public const float TileGap = 12f;

    public const float RowsTop = 224f;
    public const float RowH = 48f;

    /// <summary>
    /// The air between the last row and the first switch, and it is a whole row height on purpose.
    ///
    /// <para>At 14px the notional next row band and the first switch band interleaved: a click one
    /// row past the end of the list landed on the content toggle, because the switch spans the same
    /// y as the row that is not there. A dead zone shorter than a row is not a dead zone.</para>
    /// </summary>
    public const float RowsToSwitchesGap = 56f;

    public const float SwitchH = 34f;
    public const float SwitchGap = 10f;

    /// <summary>
    /// The band the update disclosure is drawn in, between the second switch and the third.
    ///
    /// <para>Spec §9b puts the disclosure "beside the model downloads" on this screen, so it is
    /// three wrapped lines of prose rather than a tooltip, and the switch under it has to be
    /// pushed clear of them. A fixed constant rather than a measurement for the same reason
    /// <see cref="RailLayout.ListTop"/> is one: the layout would otherwise need a typeface to
    /// answer where a switch is, and every hit test would carry a font.
    /// <c>TheDisclosureFitsTheBandTheLayoutReservesForIt</c> is what holds the constant to what
    /// the sentence actually measures.</para>
    /// </summary>
    public const float DisclosureH = 52f;

    public const float ButtonW = 132f;
    public const float ButtonH = 34f;

    /// <summary>The left and right margins every band shares.</summary>
    private const float Inset = Pad + 12f;

    public static SKRect TileRect(int i)
    {
        float room = Width - 2 * Inset;
        float w = (room - TileGap * 2) / 3f;
        float x = Inset + i * (w + TileGap);
        return new SKRect(x, TileTop, x + w, TileTop + TileH);
    }

    public static SKRect RowRect(int i) =>
        new(Inset, RowsTop + i * RowH, Width - Inset, RowsTop + i * RowH + RowH - 6);

    /// <summary>The three switches sit under however many rows there are, so a machine with no
    /// Hebrew does not leave a gap where its row would have been. The third is pushed down by the
    /// disclosure that belongs to the second.</summary>
    public static SKRect SwitchRect(int i, int rows)
    {
        float top = RowsTop + rows * RowH + RowsToSwitchesGap
                  + i * (SwitchH + SwitchGap)
                  + (i >= 2 ? DisclosureH : 0f);
        return new SKRect(Inset, top, Width - Inset, top + SwitchH);
    }

    /// <summary>The disclosure's own band: under the update switch, above the one after it.</summary>
    public static SKRect DisclosureRect(int rows)
    {
        SKRect s = SwitchRect(1, rows);
        return new SKRect(s.Left, s.Bottom + SwitchGap, s.Right, s.Bottom + SwitchGap + DisclosureH);
    }

    public static SKRect ButtonRect(int i)
    {
        float right = Width - Inset;
        float x = right - (2 - i) * (ButtonW + TileGap) + TileGap;
        return new SKRect(x, Height - ButtonH - 20, x + ButtonW, Height - 20);
    }

    /// <summary>Between the last switch and the buttons: two lines of room, because
    /// <see cref="FirstRun.Summary"/> is a sentence rather than a number and its longest form
    /// carries a failure message as well.</summary>
    public static SKRect SummaryRect(int rows) =>
        new(Inset, SwitchRect(2, rows).Bottom + 12,
            Width - Inset - ButtonW * 2 - TileGap, ButtonRect(0).Top - 6);

    /// <summary>Tiles, rows, switches, buttons, in that order, each bounded by what is actually
    /// drawn. <paramref name="rows"/> is <c>FirstRun.Rows(state).Count</c>, which is one shorter
    /// where Hebrew is not offered.</summary>
    public static FirstRunHit HitTest(float x, float y, int rows)
    {
        if (x < 0 || x > Width || y < 0 || y > Height) return new FirstRunHit(FirstRunTarget.None, -1);

        for (int i = 0; i < 3; i++)
            if (TileRect(i).Contains(x, y)) return new FirstRunHit(FirstRunTarget.Preset, i);

        for (int i = 0; i < rows; i++)
            if (RowRect(i).Contains(x, y)) return new FirstRunHit(FirstRunTarget.Row, i);

        FirstRunTarget[] switches = [FirstRunTarget.Content, FirstRunTarget.Updates, FirstRunTarget.Autostart];
        for (int i = 0; i < switches.Length; i++)
            if (SwitchRect(i, rows).Contains(x, y)) return new FirstRunHit(switches[i], -1);

        if (ButtonRect(0).Contains(x, y)) return new FirstRunHit(FirstRunTarget.NotNow, -1);
        if (ButtonRect(1).Contains(x, y)) return new FirstRunHit(FirstRunTarget.Go, -1);

        return new FirstRunHit(FirstRunTarget.None, -1);
    }
}

/// <summary>
/// Draws spec §6's first screen on Task 2's parts, so it and the settings window read as one
/// object seen twice: the same width, the same card edge, the same pills, toggles and notes.
///
/// <para>Knows no policy. Every string it draws comes out of <see cref="FirstRun"/>, so a change
/// to what a row costs or what the summary says is a change there and never here.</para>
/// </summary>
public static class FirstRunPainter
{
    private const float TitleSize = 20f;
    private const float TickBox = 18f;
    private const float BarW = 132f;
    private const float BarH = 7f;

    // ---- the three colour decisions this painter makes, and why they are functions --------------
    //
    // Every other colour on this screen is Ink or a Parts call, both of which the palette layer
    // already measures. These three are choices made here, and a choice made inline is one no test
    // can see: EveryMarkThisScreenDrawsIsReadableOnTheSurfaceItLandsOn reads them, composites each
    // over the surface it lands on, and fails on the reading an eye would get. Written inline, the
    // test enumerated a list of pairs by hand and stayed green when the painter went back to
    // drawing prices in the accent - which reads 3.07 to 3.58 on the three light palettes.

    /// <summary>The ink a row's price is drawn in. A row already ticked owes nothing more, so its
    /// "0 MB" steps back into secondary ink; every other row's number is the one thing on the line
    /// a person is deciding on, and it is full ink on every palette.</summary>
    public static SKColor PriceInk(FirstRunRow row, Derived d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return row.Ticked && !row.Free ? d.Fade(150) : d.Ink;
    }

    /// <summary>The ink a preset tile's size is drawn in. The chosen tile is already marked by its
    /// fill and its outline, so its size does not also need the accent - which as TEXT is the
    /// weakest thing on a light palette.</summary>
    public static SKColor TileSizeInk(bool chosen, Derived d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return chosen ? d.Ink : d.Fade(170);
    }

    /// <summary>
    /// A tick box's fill and the mark on it. Three states, not two: off, taken, and the free row -
    /// which is ticked but is not a choice anybody made and cannot be untaken.
    ///
    /// <para>The free row says so by staying in the chip's colours rather than by fading its mark.
    /// Measured composited, a mark at alpha 170 over the accent fill reads 2.77 to 3.17 on the
    /// three light palettes, under even the 3:1 floor for something that is not text; the same
    /// mark in ink on a chip reads 11.05 to 14.51.</para>
    /// </summary>
    public static (SKColor Fill, SKColor Mark) TickInk(bool on, bool free, Derived d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return on && !free ? (d.Accent, d.OnAccent) : (d.Chip, d.Ink);
    }

    public static void Paint(SKCanvas canvas, FirstRunState s, Derived d, SKTypeface face)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(d);

        canvas.Clear(SKColors.Transparent);

        var card = new SKRect(0, 0, FirstRunLayout.Width, FirstRunLayout.Height);
        using (var fill = new SKPaint { Color = d.Ground, IsAntialias = true })
            canvas.DrawRoundRect(new SKRoundRect(card, FirstRunLayout.Radius), fill);
        // The card's own edge, to the pixel, the same accent-at-52 hairline the card and the
        // settings window carry. It is the first thing an eye compares between two surfaces that
        // are meant to be the same object.
        using (var edge = new SKPaint
        { Color = d.Accent.WithAlpha(52), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f })
            canvas.DrawRoundRect(new SKRoundRect(card, FirstRunLayout.Radius), edge);

        IReadOnlyList<FirstRunRow> rows = FirstRun.Rows(s);
        float left = FirstRunLayout.RowRect(0).Left;
        float right = FirstRunLayout.RowRect(0).Right;
        // The list is settled once the screen has been answered, whether the download is still
        // running or over. The three stages say three different things, because "it carries on in
        // the tray" is a promise about work that has already stopped.
        bool busy = s.Stage != FirstRunStage.Choosing;

        CardText.Draw(canvas, s.Stage switch
        {
            FirstRunStage.Downloading => "Getting things ready",
            FirstRunStage.Finished => "Findra is ready",
            _ => "Welcome to Findra",
        }, left, 44, TitleSize, face, d.Ink);

        CardText.Draw(canvas, s.Stage switch
        {
            FirstRunStage.Downloading => "You can close this window. The downloads carry on, and Findra is in the tray.",
            FirstRunStage.Finished => "Findra is in the tray. Settings can add any of the rest later.",
            _ => "Names are searchable the moment Findra starts. Everything below is optional.",
        }, left, 70, Parts.LabelSize, face, d.Fade(170));

        Tiles(canvas, s, d, face);
        for (int i = 0; i < rows.Count; i++) Row(canvas, rows[i], i, s, d, face, busy);
        Switches(canvas, s, rows.Count, d, face);

        Parts.Note(canvas, FirstRun.Summary(s), FirstRunLayout.SummaryRect(rows.Count), d, face);

        // One button in the second act, not two. The answer has already been given, so "Not now"
        // has nothing left to decline and a second pill that does exactly what the first one does
        // is a choice with no difference in it.
        if (!busy)
            Parts.Pill(canvas, FirstRunLayout.ButtonRect(0), "Not now",
                       chosen: false, hovered: s.HoverTarget == FirstRunTarget.NotNow, d, face);

        Parts.Pill(canvas, FirstRunLayout.ButtonRect(1),
                   s.Stage switch
                   {
                       FirstRunStage.Downloading => "Close",
                       FirstRunStage.Finished => "Done",
                       _ => "Get these",
                   },
                   chosen: !busy, hovered: s.HoverTarget == FirstRunTarget.Go, d, face);
    }

    private static void Tiles(SKCanvas canvas, FirstRunState s, Derived d, SKTypeface face)
    {
        Preset here = Presets.Match(s.Chosen);
        Preset[] presets = [Preset.JustNames, Preset.Recommended, Preset.Everything];

        for (int i = 0; i < presets.Length; i++)
        {
            SKRect r = FirstRunLayout.TileRect(i);
            bool chosen = presets[i] == here;
            bool hovered = s.HoverTarget == FirstRunTarget.Preset && s.HoverIndex == i;

            var rr = new SKRoundRect(r, 10f);
            using (var fill = new SKPaint
            { Color = chosen ? d.RowSelected : hovered ? d.RowHover : d.Tile, IsAntialias = true })
                canvas.DrawRoundRect(rr, fill);
            using (var edge = new SKPaint
            {
                Color = chosen ? d.Accent : d.Edge, IsAntialias = true,
                Style = SKPaintStyle.Stroke, StrokeWidth = chosen ? 2f : 1f,
            }) canvas.DrawRoundRect(rr, edge);

            CardText.DrawCentred(canvas, FirstRun.PresetTitles[i], r.MidX, r.Top + 44, 15f, face, d.Ink);
            CardText.DrawCentred(canvas, FirstRun.PresetSize(presets[i]), r.MidX, r.Top + 72,
                                 Parts.LabelSize, face, TileSizeInk(chosen, d));
        }
    }

    private static void Row(SKCanvas canvas, FirstRunRow row, int i, FirstRunState s,
                            Derived d, SKTypeface face, bool busy)
    {
        SKRect r = FirstRunLayout.RowRect(i);
        bool hovered = s.HoverTarget == FirstRunTarget.Row && s.HoverIndex == i;
        if (hovered && !row.Free)
            using (var fill = new SKPaint { Color = d.RowHover, IsAntialias = true })
                canvas.DrawRoundRect(new SKRoundRect(r, 8f), fill);

        float x = r.Left + 6 + (row.Indented ? 26f : 0f);
        Tick(canvas, new SKRect(x, r.Top + 4, x + TickBox, r.Top + 4 + TickBox), row.Ticked, row.Free, d);

        float textLeft = x + TickBox + 12f;
        // The size column is reserved whatever is drawn in it, so a bar and a size never move the
        // title, and a row mid-download does not jump when it finishes.
        float textRight = r.Right - BarW - 16f;
        CardText.Draw(canvas, CardText.Ellipsize(row.Title, face, Parts.LabelSize, textRight - textLeft),
                      textLeft, r.Top + 17, Parts.LabelSize, face, d.Ink);
        CardText.Draw(canvas, CardText.Ellipsize(row.Note, face, Parts.NoteSize, textRight - textLeft),
                      textLeft, r.Top + 34, Parts.NoteSize, face, d.Fade(150));

        if (busy && row.Capability is { } c && Progress(s, c) is { } p)
        {
            var bar = new SKRect(r.Right - BarW, r.MidY - BarH / 2f, r.Right, r.MidY + BarH / 2f);
            Parts.Bar(canvas, bar, p.Total > 0 ? (float)((double)p.Got / p.Total) : 0f, d);
            return;
        }

        CardText.DrawRight(canvas, row.Size, r.Right, r.Top + 17, Parts.LabelSize, face, PriceInk(row, d));
    }

    private static CapabilityProgress? Progress(FirstRunState s, Capability c)
    {
        // A loop rather than FirstOrDefault, for the reason FirstRun.Summary spells out: over a
        // sequence of structs the default is a real CapabilityProgress, so a nullable assigned
        // from it is never null - and every row not being fetched would draw an empty bar where
        // its size belongs.
        foreach (CapabilityProgress p in s.Downloads) if (p.Capability == c) return p;
        return null;
    }

    private static void Tick(SKCanvas canvas, SKRect box, bool on, bool free, Derived d)
    {
        (SKColor boxFill, SKColor mark) = TickInk(on, free, d);
        var rr = new SKRoundRect(box, 5f);
        using (var fill = new SKPaint { Color = boxFill, IsAntialias = true })
            canvas.DrawRoundRect(rr, fill);
        using (var edge = new SKPaint
        { Color = on && !free ? d.Accent : d.Edge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f })
            canvas.DrawRoundRect(rr, edge);
        if (!on) return;

        // Drawn rather than typed. A tick glyph would be a character the bundled face may not
        // carry, and CardText's fallback would silently switch typefaces for one mark.
        using var stroke = new SKPaint
        {
            Color = mark,
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round,
        };
        canvas.DrawLine(box.Left + 4.5f, box.MidY, box.MidX - 1f, box.Bottom - 5f, stroke);
        canvas.DrawLine(box.MidX - 1f, box.Bottom - 5f, box.Right - 4f, box.Top + 5f, stroke);
    }

    private static void Switches(SKCanvas canvas, FirstRunState s, int rows, Derived d, SKTypeface face)
    {
        (string Label, bool On, FirstRunTarget Target)[] switches =
        [
            ("Look inside my files", s.ContentOn, FirstRunTarget.Content),
            ("Check GitHub for a newer version", s.CheckUpdates, FirstRunTarget.Updates),
            ("Start Findra when I sign in", s.StartAtLogon, FirstRunTarget.Autostart),
        ];

        for (int i = 0; i < switches.Length; i++)
        {
            SKRect r = FirstRunLayout.SwitchRect(i, rows);
            bool hovered = s.HoverTarget == switches[i].Target;
            if (hovered)
                using (var fill = new SKPaint { Color = d.RowHover, IsAntialias = true })
                    canvas.DrawRoundRect(new SKRoundRect(r, 8f), fill);

            CardText.Draw(canvas, switches[i].Label, r.Left + 6, r.MidY + Parts.LabelSize * 0.36f,
                          Parts.LabelSize, face, d.Ink);
            Parts.Toggle(canvas, new SKRect(r.Left, r.Top, r.Right - 6, r.Bottom), switches[i].On, hovered, d);
        }

        Parts.Note(canvas, FirstRun.Disclosure, FirstRunLayout.DisclosureRect(rows), d, face);
    }
}
