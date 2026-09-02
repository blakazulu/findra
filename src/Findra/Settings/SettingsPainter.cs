using SkiaSharp;

namespace Findra;

/// <summary>
/// Draws whatever <see cref="SettingsModel.Controls"/> says is there. Deliberately ignorant: it
/// switches on <see cref="ControlKind"/> and on nothing else, so a new setting is a row in the
/// model and never a change here.
/// </summary>
public static class SettingsPainter
{
    public static void Paint(SKCanvas canvas, SettingsState s, Derived d, SKTypeface face)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(d);

        canvas.Clear(SKColors.Transparent);

        var card = new SKRect(0, 0, RailLayout.Width, RailLayout.Height);
        using (var fill = new SKPaint { Color = d.Ground, IsAntialias = true })
            canvas.DrawRoundRect(new SKRoundRect(card, RailLayout.Radius), fill);
        // The card's own edge, to the pixel: accent at 52, 1.4px wide (SearchCard.cs). The two
        // surfaces are meant to read as one object seen twice, and the outline is the first thing
        // an eye compares - a plain d.Edge hairline here made this read as a different program's
        // dialog. It is also the only accent anywhere in the Opening and About sections, both of
        // which came in under the shot suite's colour floor while the edge was neutral.
        using (var edge = new SKPaint
        { Color = d.Accent.WithAlpha(52), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f })
            canvas.DrawRoundRect(new SKRoundRect(card, RailLayout.Radius), edge);

        CardText.Draw(canvas, "Findra", RailLayout.Pad + 8, 34, 17f, face, d.Ink);
        CardText.Draw(canvas, RailLayout.Title(s.Section), RailLayout.PaneRect().Left, 34, 13f, face, d.Fade(150));

        // ---- the rail ----
        using (var tile = new SKPaint { Color = d.Tile, IsAntialias = true })
            canvas.DrawRoundRect(new SKRoundRect(RailLayout.RailRect(), 10f), tile);

        for (int i = 0; i < RailLayout.Sections.Count; i++)
        {
            Section section = RailLayout.Sections[i];
            SKRect r = RailLayout.SectionRect(i);
            bool here = section == s.Section;
            bool hovered = s.HoverTarget == PanelTarget.Section && s.HoverRow == i;

            if (here || hovered)
                using (var fill = new SKPaint { Color = here ? d.RowSelected : d.RowHover, IsAntialias = true })
                    canvas.DrawRoundRect(new SKRoundRect(r, 8f), fill);

            // The accent marks which section you are in. RowSelected and RowHover are a step and a
            // half apart, which is enough to see and not enough to read: without this the rail
            // says "the pointer is here" and "you are here" in nearly the same voice. The label
            // itself stays in Ink, which is the pair the legibility check measures.
            if (here)
                using (var mark = new SKPaint { Color = d.Accent, IsAntialias = true })
                    canvas.DrawRoundRect(new SKRoundRect(new SKRect(r.Left + 2, r.Top + 7, r.Left + 5, r.Bottom - 7), 1.5f), mark);

            CardText.Draw(canvas, RailLayout.Title(section), r.Left + 12, r.MidY + 5, 13.5f, face,
                          here ? d.Ink : d.Fade(170));
        }

        // ---- the pane ----
        IReadOnlyList<Control> rows = SettingsModel.Controls(s);
        IReadOnlyList<int> notes = SettingsModel.NoteLines(s, face);
        for (int i = 0; i < rows.Count; i++) Row(canvas, rows[i], i, notes, s, d, face);

        if (s.Section == Section.Searches) Exclusions(canvas, s, d, face);

        // ---- the footer ----
        Parts.Pill(canvas, RailLayout.CloseRect(), "Close",
                   chosen: false, hovered: s.HoverTarget == PanelTarget.Close, d, face);
        CardText.Draw(canvas, "Changes are saved as you make them.",
                      RailLayout.Pad + 8, RailLayout.Height - 24, 11.5f, face, d.Fade(140));
    }

    private static void Row(SKCanvas canvas, Control c, int i, IReadOnlyList<int> notes,
                            SettingsState s, Derived d, SKTypeface face)
    {
        SKRect r = RailLayout.ControlRect(i, notes);
        bool hoveredRow = s.HoverTarget is PanelTarget.Control or PanelTarget.Option && s.HoverRow == i;
        int n = c.Options.Count;

        if (c.Kind != ControlKind.Note)
            Parts.Label(canvas, c.Label, r, RailLayout.LabelWidthFor(n), d, face);

        switch (c.Kind)
        {
            case ControlKind.Toggle:
                Parts.Toggle(canvas, r, c.On, hoveredRow, d);
                break;

            case ControlKind.Choice:
                for (int o = 0; o < n; o++)
                    Parts.Pill(canvas, RailLayout.OptionRect(i, o, n, notes), c.Options[o],
                               c.OptionOn[o], hoveredRow && s.HoverOption == o, d, face);
                break;

            case ControlKind.Swatch:
                for (int o = 0; o < n; o++)
                {
                    // From the state, NOT from Palette.ByName - which reloads palettes.json from
                    // disk on every call, and this runs on every pointer move.
                    Palette? p = Find(s.Palettes, c.Options[o]);
                    if (p is not null)
                        Parts.Swatch(canvas, RailLayout.OptionRect(i, o, n, notes), p,
                                     c.OptionOn[o], hoveredRow && s.HoverOption == o, d);
                }
                break;

            case ControlKind.Chord:
            case ControlKind.Button:
                Parts.Pill(canvas, new SKRect(r.Right - 176, r.Top + 2, r.Right, r.Bottom - 2),
                           c.Value.Length > 0 ? c.Value : c.Label, chosen: false, hoveredRow, d, face);
                break;

            case ControlKind.Text:
                float left = r.Left + RailLayout.LabelWidthFor(n);
                CardText.Draw(canvas, CardText.Ellipsize(c.Value, face, Parts.LabelSize, r.Right - left),
                              left, r.MidY + 5, Parts.LabelSize, face, d.Fade(190));
                break;
        }

        if (c.Note.Length > 0) Parts.Note(canvas, c.Note, RailLayout.NoteRect(i, notes), d, face);
    }

    private static Palette? Find(IReadOnlyList<Palette> palettes, string name)
    {
        foreach (Palette p in palettes)
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase)) return p;
        return null;
    }

    private static void Exclusions(SKCanvas canvas, SettingsState s, Derived d, SKTypeface face)
    {
        using (var well = new SKPaint { Color = d.Row, IsAntialias = true })
            canvas.DrawRoundRect(new SKRoundRect(RailLayout.ListRect(), 8f), well);

        IReadOnlyList<string> shown = SettingsModel.VisibleExclusions(s);
        for (int i = 0; i < shown.Count; i++)
        {
            SKRect r = RailLayout.ListRowRect(i);
            if (s.HoverTarget is PanelTarget.ListItem or PanelTarget.ListRemove && s.HoverRow == i)
                using (var hover = new SKPaint { Color = d.RowHover, IsAntialias = true })
                    canvas.DrawRoundRect(new SKRoundRect(r, 6f), hover);

            CardText.Draw(canvas, CardText.Ellipsize(shown[i], face, 12f, r.Width - RailLayout.ListRemoveW - 16),
                          r.Left + 8, r.MidY + 4, 12f, face, d.Fade(180));
            CardText.Draw(canvas, "×", r.Right - 16, r.MidY + 4, 13f, face, d.Fade(150));
        }
    }
}
