using SkiaSharp;

namespace Findra;

/// <summary>
/// The first-run screen as a screen reader and the keyboard see it. This screen owns the display
/// until it is answered, so before this a person using Narrator met a window with nothing in it
/// and no way past it. Every rectangle is the one the painter and <see cref="FirstRunLayout"/>'s
/// hit test use.
/// </summary>
public static class FirstRunAccess
{
    public const string Name = "Welcome to Findra";

    public static IReadOnlyList<AccessNode> Nodes(FirstRunState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        var nodes = new List<AccessNode>();

        // The card over the page says what is being done and refuses every press until it is.
        if (s.Work is { } work)
        {
            nodes.Add(new AccessNode("work", AccessRole.Text, string.Join(". ", work.Steps.Select(st => st.Label)),
                new SKRect(0, 0, FirstRunLayout.Width, FirstRunLayout.SurfaceHeight(s))));
            return nodes;
        }

        if (s.Stage == FirstRunStage.Choosing) Choosing(s, nodes);
        else Answered(s, nodes);
        return nodes;
    }

    private static void Choosing(FirstRunState s, List<AccessNode> nodes)
    {
        nodes.Add(new AccessNode("title", AccessRole.Text, "Welcome to Findra", new SKRect(0, 20, FirstRunLayout.Width, 80))
            { Help = "Names are searchable the moment Findra starts. Everything below is optional." });

        Preset here = FirstRun.Match(s.Chosen, s.HebrewOffered);
        Preset[] presets = [Preset.JustNames, Preset.Recommended, Preset.Everything];
        for (int i = 0; i < presets.Length; i++)
            nodes.Add(new AccessNode("preset:" + i, AccessRole.Option, FirstRun.PresetTitles[i], FirstRunLayout.TileRect(i))
            {
                On = presets[i] == here,
                Help = FirstRun.PresetSize(presets[i], s.OnDisk, s.HebrewOffered),
            });

        IReadOnlyList<FirstRunRow> rows = FirstRun.Rows(s);
        int band = FirstRunLayout.BandRow(s);
        for (int i = 0; i < rows.Count; i++)
        {
            FirstRunRow row = rows[i];
            nodes.Add(new AccessNode("row:" + i, AccessRole.Toggle, row.Title, FirstRunLayout.RowRect(i, band))
            {
                On = row.Ticked,
                Help = string.Join(", ", new[] { row.Size, row.Note }.Where(t => t.Length > 0)),
                // The free documents row is not a choice: it is always there.
                Enabled = !row.Free,
            });
        }

        if (band >= 0)
            for (int o = 0; o < FirstRun.LimitOptions.Count; o++)
                nodes.Add(new AccessNode("limit:" + o, AccessRole.Option, $"{FirstRun.LimitLabel}: {FirstRun.LimitOptions[o]}",
                    FirstRunLayout.LimitOptionRect(o, band))
                {
                    On = o < TranscribeLimit.Presets.Count && TranscribeLimit.Presets[o] == s.TranscribeMinutes,
                    Help = o == 0 ? FirstRun.LimitNote : "",
                });

        bool[] on = [s.ContentOn, s.CheckUpdates, s.StartAtLogon];
        string[] notes = [FirstRun.ContentNote, FirstRun.Disclosure, ""];
        for (int i = 0; i < 3; i++)
            nodes.Add(new AccessNode("switch:" + i, AccessRole.Toggle, FirstRunPainter.SwitchLabels[i],
                FirstRunLayout.SwitchRect(i, rows.Count, band)) { On = on[i], Help = notes[i] });

        nodes.Add(new AccessNode("summary", AccessRole.Text, FirstRun.Summary(s), FirstRunLayout.SummaryRect(rows.Count, band)));
        nodes.Add(new AccessNode("notnow", AccessRole.Button, FirstRun.NotNowLabel, FirstRunLayout.ButtonRect(0)));
        nodes.Add(new AccessNode("go", AccessRole.Button, FirstRun.GoLabel(s), FirstRunLayout.ButtonRect(1)));
    }

    private static void Answered(FirstRunState s, List<AccessNode> nodes)
    {
        float h = WelcomeLayout.Height(s);
        nodes.Add(new AccessNode("title", AccessRole.Text, Welcome.Title, new SKRect(0, 20, FirstRunLayout.Width, 80))
            { Help = Welcome.Subtitle(s) });

        (string Title, string Note)[] places =
        [
            (Welcome.CapsuleTitle, Welcome.CapsuleNote),
            (Welcome.HotkeyTitle(s.Hotkey), Welcome.HotkeyNote(s.Hotkey)),
            (Welcome.TrayTitle, Welcome.TrayNote),
        ];
        for (int i = 0; i < places.Length; i++)
            nodes.Add(new AccessNode("place:" + i, AccessRole.Text, places[i].Title, WelcomeLayout.PlaceRect(i))
                { Help = places[i].Note });

        for (int i = 0; i < s.Downloads.Count; i++)
        {
            CapabilityProgress p = s.Downloads[i];
            nodes.Add(new AccessNode("bar:" + p.Capability, AccessRole.Text,
                $"{Capabilities.Title(p.Capability)}: {Welcome.Percent(p)}", WelcomeLayout.BarRowRect(i)));
        }
        if (WelcomeLayout.ShowsStatus(s))
            nodes.Add(new AccessNode("status", AccessRole.Text, Welcome.Status(s), WelcomeLayout.StatusRect(s)));

        SKRect next = WelcomeLayout.NextRect(s);
        bool asking = FirstRun.Asks(s);
        nodes.Add(asking
            ? new AccessNode("ask", AccessRole.Text, FirstRun.AskTitle, next) { Help = FirstRun.AskNote }
            : new AccessNode("reading", AccessRole.Text, Welcome.Reading(s), next));

        nodes.Add(new AccessNode("about", AccessRole.Text, Welcome.About(s), WelcomeLayout.AboutTextRect(s)));
        for (int i = 0; i < Welcome.Links.Count; i++)
            nodes.Add(new AccessNode("link:" + i, AccessRole.Link, Welcome.Links[i].Label, WelcomeLayout.LinkRect(i, s)));

        nodes.Add(new AccessNode("settings", AccessRole.Button, Welcome.SettingsLabel, WelcomeLayout.SettingsRect(h)));
        if (asking)
            nodes.Add(new AccessNode("notnow", AccessRole.Button, FirstRun.LaterLabel, FirstRunLayout.ButtonRect(0, h)));
        nodes.Add(new AccessNode("go", AccessRole.Button, FirstRun.GoLabel(s), FirstRunLayout.ButtonRect(1, h)));
    }
}
