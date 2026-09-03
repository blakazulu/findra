using SkiaSharp;

namespace Findra;

/// <summary>The settings window's five sections, in rail order (spec §7).</summary>
public enum Section { Look, Opening, Searches, Content, About }

/// <summary>What a pointer landed on. Shared by the settings window and the first-run screen,
/// which is why it is not called SettingsTarget.</summary>
public enum PanelTarget { None, Section, Control, Option, ListItem, ListRemove, Close }

/// <summary>A hit carries BOTH indices rather than encoding two numbers into one. A row and an
/// option inside it are different questions and the caller asks both.</summary>
public readonly record struct PanelHit(PanelTarget Target, int Row, int Option)
{
    public static readonly PanelHit None = new(PanelTarget.None, -1, -1);
}

/// <summary>
/// Where everything sits in either of the two new surfaces.
///
/// <para>A PURE FUNCTION of its arguments - no state, no stored rectangles - called by the
/// painter and by the hit test, which is the rule the card already follows
/// (<c>SearchCard.cs:8-10</c>) and the reason a pointer event can never race a layout.</para>
///
/// <para>Every row-placing method takes <c>noteLines</c>: how many wrapped lines each row's
/// explanatory note occupies, zero where there is none. A row is pushed down by exactly what the
/// notes above it need. Spacing rows by a fixed gap instead is how the About section's four-line
/// disclosure ends up drawn over the two rows beneath it, with a row-count check seeing nothing
/// wrong.</para>
///
/// <para>The pane is a FIXED rectangle. Spec §7 rejected a tall single-column card: the content is
/// roughly 1,400px, and a scrolling list inside a scrolling card is hand-drawn hit testing not
/// worth owning. A section that stops fitting <see cref="SectionFits"/> loses a row.</para>
/// </summary>
public static class RailLayout
{
    /// <summary>The card's width, deliberately: the two surfaces are meant to read as one object
    /// seen twice.</summary>
    public const float Width = 820f;
    public const float Height = 560f;
    public const float Pad = 14f;
    public const float Radius = 16f;

    public const float TitleH = 52f;
    public const float RailW = 196f;
    public const float RowH = 42f;
    public const float FooterH = 46f;

    public const float ControlTop = 18f;
    public const float ControlH = 34f;
    /// <summary>Air between a row (or the bottom of its note) and the next row.</summary>
    public const float ControlGap = 12f;
    public const float ControlPad = 20f;

    /// <summary>The label column for an ordinary row - up to three options.</summary>
    public const float LabelW = 232f;
    public const float OptionGap = 8f;

    /// <summary>
    /// How much of a control row the label takes, given how many options share the rest.
    ///
    /// <para>Worked against the row, which is 546px wide (240..786): at the wide column five
    /// options get <c>(546 - 232 - 4*8) / 5 = 56px</c> each, and <see cref="Parts.Pill"/>
    /// ellipsises to <c>width - 12</c>, so a five-option row at 232 is five ellipses. Narrowing
    /// to 140 gives <c>(546 - 140 - 32) / 5 = 74.8px</c>, and 128px of label column.</para>
    ///
    /// <para><b>The budgets, measured in the shipped face</b> (Quicksand Regular at
    /// <see cref="Parts.LabelSize"/>, not the host's default - Quicksand runs 2 to 6 per cent
    /// wider than Segoe UI at this size, so every margin below is real rather than estimated):
    /// </para>
    ///
    /// <list type="table">
    /// <item><description>2 or 3 options: 220px of label, 141px / 87.3px of pill</description></item>
    /// <item><description>4 options: 156px of label, 76.5px of pill</description></item>
    /// <item><description>5 options: 128px of label, 62.8px of pill</description></item>
    /// </list>
    ///
    /// <para><b>62.8px is the tight one, and it is already tight.</b> The five-option row that
    /// matters is the transcription limit - the drives row can reach five or more options on a
    /// machine with several fixed volumes, but its labels are two characters.
    /// <c>TranscribeLimit.Describe</c>'s longest word - "30 minutes", measured at 65.3px - does
    /// not fit that pill. The rule is to shorten the label rather than widen
    /// the tolerance or re-narrow the column, so the pills say "30 min" (40.2px), "5 min"
    /// (32.8px), "2 hr" (23.3px), "No limit" (45.6px) and "Off" (19.1px); the long form stays on
    /// the command line, where nothing is measuring it. The row labels themselves arrive with the
    /// settings model and each has to come in under the column its own option count buys - 128px
    /// for the five-option row, which "Recordings" (67.6px) and "Transcribe up to" (98.3px) both
    /// clear.</para>
    ///
    /// <para>The numbers are not free parameters: <c>EveryOptionLabelFitsThePillItIsDrawnIn</c>
    /// and <c>EveryRowLabelFitsItsOwnColumn</c> measure real labels against what this returns,
    /// from both directions, so narrowing it to make pills fit breaks the labels and widening it
    /// to make labels fit breaks the pills.</para>
    /// </summary>
    public static float LabelWidthFor(int options) => options >= 5 ? 140f : options == 4 ? 168f : LabelW;

    /// <summary>
    /// Where the exclusions list starts, measured from the pane's top.
    ///
    /// <para>A CONSTANT, unlike the control rows, and deliberately: <c>SettingsModel</c> would need
    /// a typeface to work out the note heights above it, and threading one through
    /// <c>VisibleExclusions</c> and <c>Apply</c> would put a font into every call that removes an
    /// exclusion. The two rows above the list carry fixed notes, so the offset can be fixed too -
    /// and <c>TheListStartsBelowTheRowsAndTheNotesAboveIt</c> is what checks the constant against
    /// what those two rows actually measure.</para>
    /// </summary>
    public const float ListTop = 190f;
    public const float ListRowH = 26f;
    public const float ListBottomGap = 44f;
    /// <summary>The width of the cross at the right of an exclusion row.</summary>
    public const float ListRemoveW = 26f;

    public static readonly IReadOnlyList<Section> Sections =
        [Section.Look, Section.Opening, Section.Searches, Section.Content, Section.About];

    public static string Title(Section s) => s switch
    {
        Section.Look => "Look",
        Section.Opening => "Opening it",
        Section.Searches => "What it searches",
        Section.Content => "Content",
        Section.About => "About",
        _ => "",
    };

    public static SKRect RailRect() => new(Pad, TitleH, Pad + RailW, Height - FooterH);

    public static SKRect SectionRect(int i) => new(
        Pad + 6, TitleH + 6 + i * RowH,
        Pad + RailW - 6, TitleH + 6 + i * RowH + RowH - 8);

    /// <summary>The same rectangle whichever section is chosen. That is the whole point.</summary>
    public static SKRect PaneRect() => new(Pad + RailW + 10, TitleH, Width - Pad, Height - FooterH);

    public static SKRect CloseRect() =>
        new(Width - Pad - 92, Height - FooterH + 8, Width - Pad - 4, Height - 12);

    public static float ControlWidth => PaneRect().Width - 2 * ControlPad;

    /// <summary>The top of row <paramref name="row"/>, given what the notes above it need.</summary>
    private static float TopOf(int row, IReadOnlyList<int> noteLines)
    {
        ArgumentNullException.ThrowIfNull(noteLines);
        float y = PaneRect().Top + ControlTop;
        for (int i = 0; i < row; i++)
            y += ControlH + Parts.NoteHeight(i < noteLines.Count ? noteLines[i] : 0) + ControlGap;
        return y;
    }

    public static SKRect ControlRect(int row, IReadOnlyList<int> noteLines)
    {
        SKRect p = PaneRect();
        float y = TopOf(row, noteLines);
        return new SKRect(p.Left + ControlPad, y, p.Right - ControlPad, y + ControlH);
    }

    /// <summary>Where a row's note is drawn: between its own row and the next one, exactly as tall
    /// as its lines need.</summary>
    public static SKRect NoteRect(int row, IReadOnlyList<int> noteLines)
    {
        SKRect r = ControlRect(row, noteLines);
        float h = Parts.NoteHeight(row < noteLines.Count ? noteLines[row] : 0);
        return new SKRect(r.Left, r.Bottom, r.Right, r.Bottom + h);
    }

    /// <summary>Does the whole section, notes included, fit the fixed pane? Spec §7's answer to a
    /// section that does not is to lose a row, never to scroll.</summary>
    public static bool SectionFits(IReadOnlyList<int> noteLines)
    {
        ArgumentNullException.ThrowIfNull(noteLines);
        if (noteLines.Count == 0) return true;
        int last = noteLines.Count - 1;
        return NoteRect(last, noteLines).Bottom <= PaneRect().Bottom;
    }

    public static SKRect OptionRect(int row, int option, int count, IReadOnlyList<int> noteLines)
    {
        SKRect r = ControlRect(row, noteLines);
        int n = Math.Max(1, count);
        float left = r.Left + LabelWidthFor(n);
        float w = (r.Right - left - OptionGap * (n - 1)) / n;
        float x = left + option * (w + OptionGap);
        return new SKRect(x, r.Top, x + w, r.Bottom);
    }

    /// <summary>The exclusions list - the only scroller in either surface.</summary>
    public static SKRect ListRect()
    {
        SKRect p = PaneRect();
        return new SKRect(p.Left + ControlPad, p.Top + ListTop, p.Right - ControlPad, p.Bottom - ListBottomGap);
    }

    public static SKRect ListRowRect(int i)
    {
        SKRect l = ListRect();
        return new SKRect(l.Left, l.Top + i * ListRowH, l.Right, l.Top + i * ListRowH + ListRowH - 2);
    }

    public static int ListRowsThatFit => (int)(ListRect().Height / ListRowH);

    /// <summary><paramref name="optionCounts"/> is how many options each control row holds, zero
    /// for a row that is one thing; <paramref name="noteLines"/> is how many lines each row's note
    /// takes; <paramref name="listRows"/> is how many exclusions are shown. The model supplies all
    /// three, so the layout never has to know what a setting is.</summary>
    public static PanelHit HitTest(float x, float y, IReadOnlyList<int> optionCounts,
                                   IReadOnlyList<int> noteLines, int listRows = 0)
    {
        ArgumentNullException.ThrowIfNull(optionCounts);
        ArgumentNullException.ThrowIfNull(noteLines);
        if (x < 0 || x > Width || y < 0 || y > Height) return PanelHit.None;

        // First, because it sits in the footer and a footer-wide hit would swallow it.
        if (CloseRect().Contains(x, y)) return new PanelHit(PanelTarget.Close, -1, -1);

        if (RailRect().Contains(x, y))
        {
            for (int i = 0; i < Sections.Count; i++)
                if (SectionRect(i).Contains(x, y)) return new PanelHit(PanelTarget.Section, i, -1);
            // The empty rail below the last section is not the last section. Dividing by RowH
            // would answer 5, 6 or 7 here and the caller would index Sections with it.
            return PanelHit.None;
        }

        if (PaneRect().Contains(x, y))
        {
            for (int row = 0; row < optionCounts.Count; row++)
            {
                // ControlRect only - a click on the note band below a row belongs to nothing, and
                // a hit test that did not know about the band would give it to the row after.
                if (!ControlRect(row, noteLines).Contains(x, y)) continue;
                int n = optionCounts[row];
                for (int o = 0; o < n; o++)
                    if (OptionRect(row, o, n, noteLines).Contains(x, y)) return new PanelHit(PanelTarget.Option, row, o);
                return new PanelHit(PanelTarget.Control, row, -1);
            }

            // The list, bounded by what is actually shown. An unbounded divide answers 6 for a
            // list of six and the caller removes SearchExclusions[6].
            if (listRows > 0 && ListRect().Contains(x, y))
            {
                for (int i = 0; i < Math.Min(listRows, ListRowsThatFit); i++)
                {
                    SKRect r = ListRowRect(i);
                    if (!r.Contains(x, y)) continue;
                    return x >= r.Right - ListRemoveW
                        ? new PanelHit(PanelTarget.ListRemove, i, -1)
                        : new PanelHit(PanelTarget.ListItem, i, -1);
                }
            }
        }

        return PanelHit.None;
    }
}
