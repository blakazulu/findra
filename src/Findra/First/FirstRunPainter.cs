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

    /// <summary>
    /// Taller than the settings window, and the number is arithmetic rather than taste: a title
    /// and a sentence, three preset tiles, five rows where Hebrew is offered, the transcription
    /// limit under Speech where Speech is taken, a row's worth of dead air and then some, three
    /// switches with the update disclosure wrapped between the second and the third, a summary of
    /// three lines at the lead size, and two buttons.
    ///
    /// <para>Sized for the TALLEST of those, not the common one. The screen is a fixed window that
    /// is already open when Speech is ticked, so the limit row appears into room that was already
    /// reserved; a height that reflowed with it would resize the window under the pointer that
    /// clicked. Where the row is not shown the slack falls between the last switch and the
    /// summary, which is the one band on the screen with nothing in it - and the summary is drawn
    /// against the bottom of that band, so it stays put rather than jumping when Speech is
    /// ticked.</para>
    ///
    /// <para><c>EverythingFitsTheScreenWithHebrewOffered</c> and
    /// <c>TheLongestSummaryFitsTheBandTheLayoutLeavesForIt</c> hold this to both configurations
    /// and to what the longest sentence actually measures.</para>
    /// </summary>
    public const float Height = 928f;
    public const float Pad = RailLayout.Pad;
    public const float Radius = RailLayout.Radius;

    public const float TileTop = 96f;
    public const float TileH = 104f;
    public const float TileGap = 12f;

    public const float RowsTop = 224f;
    public const float RowH = 48f;

    /// <summary>
    /// The band the transcription limit takes when Speech is ticked, and therefore how far every
    /// row below it moves.
    ///
    /// <para>Inserted into the list rather than laid over it: the rows after Speech shift down by
    /// exactly this, and the air the band does not draw in belongs to nothing, so a click that
    /// misses a pill ticks no capability.</para>
    /// </summary>
    public const float LimitBand = 64f;

    /// <summary>The part of the band that is drawn - a label and five pills, on the pill height
    /// the settings window uses, with the rest of the band as air below it.</summary>
    public const float LimitH = 34f;

    /// <summary>
    /// The note under the pills, saying what the number actually decides.
    ///
    /// <para>A constant rather than a measurement, for the reason <see cref="DisclosureH"/> is
    /// one: a layout that measured the sentence would need a typeface and every hit test would
    /// then carry a font. <c>TheLimitNoteFitsTheBandTheLayoutReservesForIt</c> is what holds the
    /// constant to what <see cref="FirstRun.LimitNote"/> actually measures in the shipped face.
    /// </para>
    /// </summary>
    public const float LimitNoteH = 20f;

    /// <summary>A limit pill and the air between two of them. Five pills right-aligned with the
    /// column the row sizes are right-aligned in, so the two columns read as one edge. 76px holds
    /// 64px of label after <see cref="Parts.Pill"/>'s ellipsis inset, and the widest of the five -
    /// "No limit", 45.6px in the shipped face - clears it.</summary>
    public const float LimitPillW = 76f;
    public const float LimitPillGap = 8f;

    /// <summary>A tick box, and how far a dependent row is indented under the one it belongs to.
    /// Both live here rather than in the painter because the limit row's own geometry is measured
    /// off them: its label lines up with the Hebrew row's title, one indent and one tick box in
    /// from the row's edge.</summary>
    public const float TickBox = 18f;
    public const float RowIndent = 26f;

    /// <summary>Where an indented row's title starts, measured from the row's left edge.</summary>
    private const float TitleInset = 6f + RowIndent + TickBox + 12f;

    /// <summary>
    /// The air between the last row and the first switch, which is a whole row height and no more.
    ///
    /// <para>It cannot be closed. At 14px the notional next row band and the first switch band
    /// interleaved: a click one row past the end of the list landed on the content toggle, because
    /// the switch spans the same y as the row that is not there. A dead zone shorter than a row is
    /// not a dead zone.</para>
    ///
    /// <para>It also cannot be sixty pixels of nothing, which is what it was: the two halves of
    /// this screen stopped reading as a list and a set of switches and started reading as a list
    /// with a hole under it. So the band is exactly one row, and <see cref="RuleRect"/> gives it
    /// something to be - it is a parting between what gets downloaded and how Findra behaves,
    /// drawn rather than merely left empty.</para>
    /// </summary>
    public const float RowsToSwitchesGap = 48f;

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

    /// <summary>
    /// The band the content note is drawn in, between the first switch and the second.
    ///
    /// <para>The same shape as <see cref="DisclosureH"/> and there for a blunter reason: "Look
    /// inside my files" is the most consequential choice on the screen and it was a bare label,
    /// while the update check below it carried four lines. Two lines of prose, held to
    /// <see cref="FirstRun.ContentNote"/>'s real measurement by
    /// <c>TheContentNoteFitsTheBandTheLayoutReservesForIt</c>.</para>
    /// </summary>
    public const float ContentNoteH = 36f;

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

    /// <summary>How far everything below row <paramref name="i"/> has been pushed down by the
    /// transcription limit. <paramref name="limitRow"/> is <c>FirstRun.LimitRow(state)</c>: the
    /// row the band sits under, or -1 where Speech is not taken and there is no band.</summary>
    private static float Shift(int i, int limitRow) => limitRow >= 0 && i > limitRow ? LimitBand : 0f;

    public static SKRect RowRect(int i, int limitRow = -1)
    {
        float top = RowsTop + i * RowH + Shift(i, limitRow);
        return new SKRect(Inset, top, Width - Inset, top + RowH - 6);
    }

    /// <summary>The transcription limit's own band, in the gap the rows below it opened up.
    /// </summary>
    public static SKRect LimitRect(int limitRow)
    {
        float top = RowsTop + (limitRow + 1) * RowH;
        return new SKRect(Inset, top, Width - Inset, top + LimitH);
    }

    /// <summary>One of the five choices, right-aligned as a group with the column the row sizes
    /// end in, so the limit row's right edge is the list's right edge.</summary>
    public static SKRect LimitOptionRect(int option, int limitRow)
    {
        SKRect r = LimitRect(limitRow);
        int n = FirstRun.LimitOptions.Count;
        float left = r.Right - n * LimitPillW - (n - 1) * LimitPillGap;
        float x = left + option * (LimitPillW + LimitPillGap);
        return new SKRect(x, r.Top, x + LimitPillW, r.Bottom);
    }

    /// <summary>The label column the limit row's own label has to fit: from where an indented
    /// row's title starts to where the first pill begins.</summary>
    public static float LimitLabelLeft(int limitRow) => LimitRect(limitRow).Left + TitleInset;

    /// <summary>The note's own band, under the pills and inside the room the rows below already
    /// moved down for. It starts where the label does, so it reads as this row's note rather than
    /// as a line belonging to the list.</summary>
    public static SKRect LimitNoteRect(int limitRow)
    {
        SKRect r = LimitRect(limitRow);
        return new SKRect(LimitLabelLeft(limitRow), r.Bottom, r.Right, r.Bottom + LimitNoteH);
    }

    /// <summary>The content switch's note: under the first switch, above the second.</summary>
    public static SKRect ContentNoteRect(int rows, int limitRow = -1)
    {
        SKRect s = SwitchRect(0, rows, limitRow);
        return new SKRect(s.Left, s.Bottom + SwitchGap, s.Right, s.Bottom + SwitchGap + ContentNoteH);
    }

    /// <summary>The rule drawn through the dead zone, between the list and the switches. Its own
    /// band rather than a y, because the hit test has to be able to say that nothing lives here:
    /// a rule that answered a click would be the dead zone with a target painted on it.</summary>
    public static SKRect RuleRect(int rows, int limitRow = -1)
    {
        SKRect above = RowRect(rows - 1, limitRow);
        SKRect below = SwitchRect(0, rows, limitRow);
        float mid = (above.Bottom + below.Top) / 2f;
        return new SKRect(Inset, mid - 1f, Width - Inset, mid + 1f);
    }

    /// <summary>The three switches sit under however many rows there are, so a machine with no
    /// Hebrew does not leave a gap where its row would have been. The second and third are pushed
    /// down by the note that belongs to the first, the third again by the disclosure that belongs
    /// to the second, and all three by the limit row when it is there.
    /// </summary>
    public static SKRect SwitchRect(int i, int rows, int limitRow = -1)
    {
        float top = RowsTop + rows * RowH + Shift(rows, limitRow) + RowsToSwitchesGap
                  + i * (SwitchH + SwitchGap)
                  + (i >= 1 ? ContentNoteH : 0f)
                  + (i >= 2 ? DisclosureH : 0f);
        return new SKRect(Inset, top, Width - Inset, top + SwitchH);
    }

    /// <summary>The disclosure's own band: under the update switch, above the one after it.</summary>
    public static SKRect DisclosureRect(int rows, int limitRow = -1)
    {
        SKRect s = SwitchRect(1, rows, limitRow);
        return new SKRect(s.Left, s.Bottom + SwitchGap, s.Right, s.Bottom + SwitchGap + DisclosureH);
    }

    public static SKRect ButtonRect(int i)
    {
        float right = Width - Inset;
        float x = right - (2 - i) * (ButtonW + TileGap) + TileGap;
        return new SKRect(x, Height - ButtonH - 20, x + ButtonW, Height - 20);
    }

    /// <summary>Between the last switch and the buttons: at least two lines of room, because
    /// <see cref="FirstRun.Summary"/> is a sentence rather than a number and its longest form
    /// carries a failure message as well.</summary>
    public static SKRect SummaryRect(int rows, int limitRow = -1) =>
        new(Inset, SwitchRect(2, rows, limitRow).Bottom + 12,
            Width - Inset - ButtonW * 2 - TileGap, ButtonRect(0).Top - 6);

    /// <summary>
    /// The summary's band once the screen has been answered, which is a different band and not a
    /// different anchor inside the same one.
    ///
    /// <para>The second act draws no switches, so the room they took is empty and the sentence
    /// that carries the screen would otherwise sit alone against the bottom with three hundred
    /// pixels of nothing above it. Here it follows the list it is about, under the rule, and the
    /// slack falls between it and the button - which is the shape of every progress dialog and
    /// not a hole in the middle of one. Wider than the choosing band too: "Not now" is not drawn
    /// in the second act, so the sentence has the whole card rather than the card less two
    /// buttons.</para>
    /// </summary>
    public static SKRect SettledSummaryRect(int rows, int limitRow = -1) =>
        new(Inset, RowRect(rows - 1, limitRow).Bottom + RowsToSwitchesGap,
            Width - Inset - ButtonW - TileGap, ButtonRect(0).Top - 6);

    /// <summary>Tiles, rows, the transcription limit, switches, buttons, in that order, each
    /// bounded by what is actually drawn. <paramref name="rows"/> is
    /// <c>FirstRun.Rows(state).Count</c>, which is one shorter where Hebrew is not offered;
    /// <paramref name="limitRow"/> is <c>FirstRun.LimitRow(state)</c>, which is -1 where Speech is
    /// not taken.
    ///
    /// <para><paramref name="settled"/> is the second act, and it answers with the way out and
    /// nothing else. Once the screen has been answered the selection belongs to the shell: a
    /// tile, a row, a switch or a limit pill that still took a click would be acting on a
    /// decision that has already been handed over, and a download that had begun would not
    /// change with it. The painter draws the same distinction rather than leaving controls
    /// looking live.</para></summary>
    public static FirstRunHit HitTest(float x, float y, int rows, int limitRow = -1, bool settled = false)
    {
        if (x < 0 || x > Width || y < 0 || y > Height) return new FirstRunHit(FirstRunTarget.None, -1);

        if (settled)
            return ButtonRect(1).Contains(x, y)
                ? new FirstRunHit(FirstRunTarget.Go, -1)
                : new FirstRunHit(FirstRunTarget.None, -1);

        for (int i = 0; i < 3; i++)
            if (TileRect(i).Contains(x, y)) return new FirstRunHit(FirstRunTarget.Preset, i);

        for (int i = 0; i < rows; i++)
            if (RowRect(i, limitRow).Contains(x, y)) return new FirstRunHit(FirstRunTarget.Row, i);

        // The pills only. The rest of the band is the label and the air beside it, and a click
        // there is a click on nothing rather than on whichever pill is nearest.
        if (limitRow >= 0)
            for (int o = 0; o < FirstRun.LimitOptions.Count; o++)
                if (LimitOptionRect(o, limitRow).Contains(x, y)) return new FirstRunHit(FirstRunTarget.Limit, o);

        FirstRunTarget[] switches = [FirstRunTarget.Content, FirstRunTarget.Updates, FirstRunTarget.Autostart];
        for (int i = 0; i < switches.Length; i++)
            if (SwitchRect(i, rows, limitRow).Contains(x, y)) return new FirstRunHit(switches[i], -1);

        if (ButtonRect(0).Contains(x, y)) return new FirstRunHit(FirstRunTarget.NotNow, -1);
        if (ButtonRect(1).Contains(x, y)) return new FirstRunHit(FirstRunTarget.Go, -1);

        return new FirstRunHit(FirstRunTarget.None, -1);
    }
}

/// <summary>
/// Draws spec §6's first screen on the shared parts in <see cref="Parts"/>, so it and the
/// settings window read as one
/// object seen twice: the same width, the same card edge, the same pills, toggles and notes.
///
/// <para>Knows no policy. Every string it draws comes out of <see cref="FirstRun"/>, so a change
/// to what a row costs or what the summary says is a change there and never here.</para>
/// </summary>
public static class FirstRunPainter
{
    private const float TitleSize = 20f;
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

    /// <summary>
    /// The ink a row's price is drawn in: full ink, on every row, in every state.
    ///
    /// <para>A ticked row used to step back into secondary ink because its number was zero - it
    /// owed nothing more. A row's price is now its own download whether it is ticked or not, so
    /// fading the ticked ones would dim exactly the numbers somebody has agreed to pay and leave
    /// the ones they have declined at full strength.</para>
    ///
    /// <para>It stays a function rather than becoming a literal in the paint because the
    /// legibility check reads the painter's decision rather than a list written out beside it -
    /// a hand-written pair passes whatever the painter does.</para>
    /// </summary>
    public static SKColor PriceInk(FirstRunRow row, Derived d)
    {
        ArgumentNullException.ThrowIfNull(d);
        return d.Ink;
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

        int limitRow = FirstRun.LimitRow(s);

        // The tiles and the rows are drawn in both acts: in the first they are the question, in
        // the second they are the record of what was asked for and how far each part of it has
        // got. Everything else on the screen is a SETTING, and a setting whose value has already
        // been written and handed to the shell is not a control any more.
        Tiles(canvas, s, d, face);
        for (int i = 0; i < rows.Count; i++) Row(canvas, rows[i], i, s, d, face, busy, limitRow);

        // So the second act stops drawing them. Not dimmed and not greyed: a toggle drawn faintly
        // is still a toggle, and text faded far enough to read as disabled is text that fails the
        // reading this screen holds every other mark to. The switches, their notes and the
        // transcription limit go, and the rule goes with them because a parting between a list
        // and nothing is not a parting. What is left is the download and the way out of it.
        if (!busy)
        {
            if (limitRow >= 0) Limit(canvas, s, limitRow, d, face);
            Rule(canvas, FirstRunLayout.RuleRect(rows.Count, limitRow), d);
            Switches(canvas, s, rows.Count, limitRow, d, face);
        }

        // A lead, not a note. Every row states its own download and none of them moves any more,
        // so this is the only line that says what the whole selection costs - and what is still
        // owed, once the second act starts.
        //
        // Sat on the BOTTOM of its band rather than the top. The band is what is left between the
        // last switch and the buttons, and it is a whole limit row taller when Speech is not
        // ticked; anchored to the top, the one line that carries the screen would jump 44px down
        // the moment somebody ticks Speech, and float in the middle of nothing the rest of the
        // time. Against the buttons it stays where it is and the slack falls into the air above
        // it, which is the one part of the screen with nothing in it.
        string summary = FirstRun.Summary(s);
        SKRect band = busy
            ? FirstRunLayout.SettledSummaryRect(rows.Count, limitRow)
            : FirstRunLayout.SummaryRect(rows.Count, limitRow);
        float need = Parts.LeadHeight(Parts.Wrap(summary, face, Parts.LeadSize, band.Width).Count);
        // Against the TOP of the second act's band and the BOTTOM of the first's, because the two
        // bands are the wrong way round from each other: in the first act the slack is above the
        // sentence, in the second it is below it, and in both the sentence stays put.
        Parts.Lead(canvas, summary,
                   busy
                       ? new SKRect(band.Left, band.Top, band.Right, band.Top + need)
                       : new SKRect(band.Left, band.Bottom - need, band.Right, band.Bottom),
                   d, face);

        // One button in the second act, not two. The answer has already been given, so "Not now"
        // has nothing left to decline and a second pill that does exactly what the first one does
        // is a choice with no difference in it.
        if (!busy)
            Parts.Pill(canvas, FirstRunLayout.ButtonRect(0), FirstRun.NotNowLabel,
                       chosen: false, hovered: s.HoverTarget == FirstRunTarget.NotNow, d, face);

        // Lit in the second act as well as the first. It is the only thing on the screen that
        // still answers, and a settled chooser behind an unlit button is a screen with no way out
        // that anybody can see.
        Parts.Pill(canvas, FirstRunLayout.ButtonRect(1), FirstRun.GoLabel(s.Stage),
                   chosen: true, hovered: s.HoverTarget == FirstRunTarget.Go, d, face);
    }

    /// <summary>The parting between what gets downloaded and how Findra behaves, drawn in the
    /// same edge colour and at the same weight as the card's own rules, so the two surfaces divide
    /// their sections the same way.</summary>
    private static void Rule(SKCanvas canvas, SKRect r, Derived d)
    {
        using var p = new SKPaint { Color = d.Edge, IsAntialias = false, StrokeWidth = 1 };
        canvas.DrawLine(r.Left, r.MidY, r.Right, r.MidY, p);
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
                            Derived d, SKTypeface face, bool busy, int limitRow)
    {
        SKRect r = FirstRunLayout.RowRect(i, limitRow);
        bool hovered = s.HoverTarget == FirstRunTarget.Row && s.HoverIndex == i;
        if (hovered && !row.Free)
            using (var fill = new SKPaint { Color = d.RowHover, IsAntialias = true })
                canvas.DrawRoundRect(new SKRoundRect(r, 8f), fill);

        float x = r.Left + 6 + (row.Indented ? FirstRunLayout.RowIndent : 0f);
        Tick(canvas, new SKRect(x, r.Top + 4, x + FirstRunLayout.TickBox, r.Top + 4 + FirstRunLayout.TickBox),
             row.Ticked, row.Free, d);

        float textLeft = x + FirstRunLayout.TickBox + 12f;
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

    /// <summary>
    /// How long a recording is worth transcribing, under the Speech row that turned transcription
    /// on and above the Hebrew pass over the same recordings.
    ///
    /// <para>Indented and aligned with the Hebrew row's title, so it reads as Speech's setting
    /// rather than as a sixth capability, and drawn in the same pills the settings window uses for
    /// the same five choices.</para>
    /// </summary>
    private static void Limit(SKCanvas canvas, FirstRunState s, int limitRow, Derived d, SKTypeface face)
    {
        SKRect r = FirstRunLayout.LimitRect(limitRow);
        CardText.Draw(canvas, FirstRun.LimitLabel, FirstRunLayout.LimitLabelLeft(limitRow),
                      r.MidY + Parts.LabelSize * 0.36f, Parts.LabelSize, face, d.Ink);

        for (int o = 0; o < FirstRun.LimitOptions.Count; o++)
            Parts.Pill(canvas, FirstRunLayout.LimitOptionRect(o, limitRow), FirstRun.LimitOptions[o],
                       chosen: TranscribeLimit.Presets[o] == s.TranscribeMinutes,
                       hovered: s.HoverTarget == FirstRunTarget.Limit && s.HoverIndex == o, d, face);

        // What the number decides, in the band the rows above use for their own notes. Without
        // it, "Transcribe up to" under a row called Speech says nothing about the videos it also
        // governs - which is the question somebody asked of the shipped screen.
        Parts.Note(canvas, FirstRun.LimitNote, FirstRunLayout.LimitNoteRect(limitRow), d, face);
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

        // Drawn rather than typed. A tick is one of the glyphs a text face is least likely to
        // carry, and CardText's fallback would silently switch typefaces for one mark. The
        // exclusions list's remove cross is typed, because a multiplication sign is Latin-1 and
        // every face that carries the alphabet carries it too.
        using var stroke = new SKPaint
        {
            Color = mark,
            IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2f, StrokeCap = SKStrokeCap.Round,
        };
        canvas.DrawLine(box.Left + 4.5f, box.MidY, box.MidX - 1f, box.Bottom - 5f, stroke);
        canvas.DrawLine(box.MidX - 1f, box.Bottom - 5f, box.Right - 4f, box.Top + 5f, stroke);
    }

    private static void Switches(SKCanvas canvas, FirstRunState s, int rows, int limitRow,
                                 Derived d, SKTypeface face)
    {
        (string Label, bool On, FirstRunTarget Target)[] switches =
        [
            ("Look inside my files", s.ContentOn, FirstRunTarget.Content),
            ("Check GitHub for a newer version", s.CheckUpdates, FirstRunTarget.Updates),
            ("Start Findra when I sign in", s.StartAtLogon, FirstRunTarget.Autostart),
        ];

        for (int i = 0; i < switches.Length; i++)
        {
            SKRect r = FirstRunLayout.SwitchRect(i, rows, limitRow);
            bool hovered = s.HoverTarget == switches[i].Target;
            if (hovered)
                using (var fill = new SKPaint { Color = d.RowHover, IsAntialias = true })
                    canvas.DrawRoundRect(new SKRoundRect(r, 8f), fill);

            CardText.Draw(canvas, switches[i].Label, r.Left + 6, r.MidY + Parts.LabelSize * 0.36f,
                          Parts.LabelSize, face, d.Ink);
            Parts.Toggle(canvas, new SKRect(r.Left, r.Top, r.Right - 6, r.Bottom), switches[i].On, hovered, d);
        }

        // Both notes, in the two bands the layout reserves. The content one is the more important
        // of the pair and was the one missing: it is the choice that decides whether Findra reads
        // the inside of every file on the machine, and it was the only switch here with nothing
        // said about it.
        Parts.Note(canvas, FirstRun.ContentNote, FirstRunLayout.ContentNoteRect(rows, limitRow), d, face);
        Parts.Note(canvas, FirstRun.Disclosure, FirstRunLayout.DisclosureRect(rows, limitRow), d, face);
    }
}
