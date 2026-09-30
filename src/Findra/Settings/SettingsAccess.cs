using SkiaSharp;

namespace Findra;

/// <summary>
/// The settings window as a screen reader and the keyboard see it: the rail's sections as tabs,
/// each row as the control it is drawn as, the exclusions list, the Remove question when it is up,
/// and Close. Every rectangle is the one <see cref="RailLayout"/> hands the painter and the hit
/// test, so pressing an element's centre is a press on that element.
/// </summary>
public static class SettingsAccess
{
    public const string Name = "Findra settings";

    public static IReadOnlyList<AccessNode> Nodes(SettingsState s, SKTypeface face)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(face);
        var nodes = new List<AccessNode>();

        // A question over the pane is the only thing that answers while it is up, so it is the only
        // thing offered.
        if (s.Removing is { } c)
        {
            (int body, int keep) = SettingsPainter.RemoveLines(s, c, face);
            string keepLabel = RemovePrompt.KeepLabel(c);
            SKRect panel = RemovePrompt.Panel(RailLayout.Width, RailLayout.Height, body, keep);
            nodes.Add(new AccessNode("remove:title", AccessRole.Text, RemovePrompt.Title(c), panel));
            nodes.Add(new AccessNode("remove:body", AccessRole.Text, RemovePrompt.Body(c, s.Installed), panel));
            if (keepLabel.Length > 0)
                nodes.Add(new AccessNode("remove:keep", AccessRole.Toggle, keepLabel,
                    RemovePrompt.KeepRow(panel, body, keep)) { On = s.KeepFound });
            nodes.Add(new AccessNode("remove:cancel", AccessRole.Button, "Cancel", RemovePrompt.Button(panel, go: false)));
            nodes.Add(new AccessNode("remove:go", AccessRole.Button, RemovePrompt.GoLabel(c, s.Installed),
                RemovePrompt.Button(panel, go: true)));
            return nodes;
        }

        for (int i = 0; i < RailLayout.Sections.Count; i++)
        {
            Section section = RailLayout.Sections[i];
            nodes.Add(new AccessNode("section:" + section, AccessRole.Tab, RailLayout.Title(section), RailLayout.SectionRect(i))
                { On = section == s.Section });
        }

        IReadOnlyList<Control> rows = SettingsModel.Controls(s);
        IReadOnlyList<int> notes = SettingsModel.NoteLines(s, face);
        for (int r = 0; r < rows.Count; r++)
        {
            Control row = rows[r];
            string key = $"row:{row.Id}:{row.Tag}";
            SKRect rect = RailLayout.ControlRect(r, notes);
            bool answers = SettingsModel.Answers(s, new PanelHit(PanelTarget.Control, r, -1));

            if (row.Options.Count > 0)
            {
                // One stop per option, each named with its row, so "C:" is heard as "Drives: C:".
                int n = row.Options.Count;
                for (int o = 0; o < n; o++)
                    nodes.Add(new AccessNode($"{key}:{o}", AccessRole.Option, $"{row.Label}: {row.Options[o]}",
                        RailLayout.OptionRect(r, o, n, notes))
                    {
                        On = o < row.OptionOn.Count && row.OptionOn[o],
                        Help = o == 0 ? row.Note : "",
                        Enabled = SettingsModel.Answers(s, new PanelHit(PanelTarget.Option, r, o)) || (o < row.OptionOn.Count && row.OptionOn[o]),
                    });
                continue;
            }

            nodes.Add(row.Kind switch
            {
                ControlKind.Toggle => new AccessNode(key, AccessRole.Toggle, row.Label, rect)
                    { On = row.On, Help = row.Note, Enabled = answers },
                ControlKind.Button or ControlKind.Chord => new AccessNode(key, AccessRole.Button,
                    row.Label.Length > 0 ? $"{row.Label}: {row.Value}" : row.Value, rect)
                    { Help = row.Note, Enabled = answers },
                ControlKind.Note => new AccessNode(key, AccessRole.Text, row.Note, rect),
                _ => new AccessNode(key, AccessRole.Text, row.Value.Length > 0 ? $"{row.Label}: {row.Value}" : row.Label, rect)
                    { Help = row.Note },
            });
        }

        if (s.Section == Section.Searches)
        {
            IReadOnlyList<string> shown = SettingsModel.VisibleExclusions(s);
            for (int i = 0; i < shown.Count; i++)
            {
                SKRect r = RailLayout.ListRowRect(i);
                nodes.Add(new AccessNode("skip:" + shown[i], AccessRole.Text, "Skipped: " + shown[i],
                    new SKRect(r.Left, r.Top, r.Right - RailLayout.ListRemoveW, r.Bottom)));
                nodes.Add(new AccessNode("unskip:" + shown[i], AccessRole.Button, "Stop skipping " + shown[i],
                    new SKRect(r.Right - RailLayout.ListRemoveW, r.Top, r.Right, r.Bottom)));
            }
        }

        nodes.Add(new AccessNode("close", AccessRole.Button, "Close", RailLayout.CloseRect()));
        return nodes;
    }
}
