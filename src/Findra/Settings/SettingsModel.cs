using System.Globalization;

using Findra.Startup;   // HelperTaskState
using SkiaSharp;        // SKTypeface, for measuring a note before the layout places the next row

namespace Findra;

/// <summary>
/// Which row this is. Every dispatch in <see cref="SettingsModel.Apply"/> switches on one of
/// these and on nothing else.
///
/// <para>It exists because the first draft of this file dispatched two controls on their display
/// strings - <c>row.Label.StartsWith("Show")</c> and the literal <c>"Register it"</c> - so a
/// copy-edit unwires a control and nothing anywhere notices. One of those two was the recovery
/// path for a scheduled task that had never been registered at all.</para>
/// </summary>
public enum ControlId
{
    None,
    Mode, DarkPalette, LightPalette, PalettesFile,
    Hotkey, ShowCapsule, ResetCapsule, Autostart, Helper,
    Drives, AddFolder,
    IndexContent, Transcribe, Capability,
    Version, Updates, CheckUpdates, CheckNow, InstalledVia, Removing,
}

/// <summary>
/// What a click asks the shell to do that the model cannot: leave the process, touch the
/// registry, open a dialog, start a download.
///
/// <para>Half of what a settings window does is not a config change, and a model that can only
/// return a new <see cref="Config"/> has no way to say so. That is how five controls in the first
/// draft ended up drawn and dead.</para>
/// </summary>
public enum SettingsAction
{
    None,
    OpenPalettesFile,
    CaptureChord,
    SetAutostart, ClearAutostart,
    RegisterHelper,
    PickFolder,
    InstallCapability,
    CheckNow,

    /// <summary>Put the capsule back where it can be seen, NOW, not at the next launch.
    ///
    /// <para>Clearing <c>CapsuleX</c>/<c>CapsuleY</c> is only half of it: the capsule window is
    /// built from the config when the shell starts, so a config write with nobody watching leaves
    /// an off-screen capsule off-screen for the rest of the session - and "it was dragged onto a
    /// monitor that is no longer there" is the only reason anybody presses this. That is the dead
    /// control shape one layer down: the click is answered by the model and dropped by the
    /// shell.</para></summary>
    RecentreCapsule,
}

/// <summary>What the painter draws. It switches on this and on nothing else.</summary>
public enum ControlKind { Note, Choice, Swatch, Toggle, Chord, Text, Button }

/// <summary>
/// One row of the pane. <see cref="Options"/> and <see cref="OptionOn"/> are always the same
/// length, and a row that offers nothing carries two empty lists rather than nulls.
/// <see cref="Tag"/> is the capability's enum value on a capability row and zero everywhere else -
/// it is what tells four rows sharing <see cref="ControlId.Capability"/> apart.
/// </summary>
public readonly record struct Control(
    ControlId Id,
    ControlKind Kind,
    string Label,
    string Value,
    bool On,
    IReadOnlyList<string> Options,
    IReadOnlyList<bool> OptionOn,
    string Note,
    int Tag)
{
    public static Control Plain(ControlId id, ControlKind kind, string label,
                                string value = "", bool on = false, string note = "", int tag = 0) =>
        new(id, kind, label, value, on, [], [], note, tag);
}

/// <summary>
/// Everything the settings window is looking at: the settings themselves, plus the facts about
/// the machine that are not settings and must not be stored as if they were - which palettes are
/// on disk, whether the scheduled task is there, what is installed, what the update check found.
/// </summary>
public sealed record SettingsState(Config Config)
{
    public Section Section { get; init; } = Section.Look;
    public IReadOnlyList<Palette> Palettes { get; init; } = Palette.BuiltIn;
    public CapabilitySet Installed { get; init; } = CapabilitySet.None;
    public bool HebrewOffered { get; init; }
    public HelperTaskState Helper { get; init; } = HelperTaskState.Unknown;
    public bool StartsAtLogon { get; init; }
    public bool EverIndexed { get; init; }
    public bool IndexerAlive { get; init; }
    public IReadOnlyList<string> Drives { get; init; } = [];

    /// <summary>True between clicking the hotkey row and the next key press. The window routes
    /// keys through <see cref="SettingsModel.ChordFrom"/> while it holds, and every path out of
    /// the section clears it - a capture mode nothing cancels is one the person is stuck in.
    /// </summary>
    public bool Capturing { get; init; }

    public string HotkeyMessage { get; init; } = "";
    public int ExclusionScroll { get; init; }
    public string Version { get; init; } = "";
    public UpdateState Update { get; init; } = UpdateState.NotDue;
    public string? Latest { get; init; }
    public PanelTarget HoverTarget { get; init; } = PanelTarget.None;
    public int HoverRow { get; init; } = -1;
    public int HoverOption { get; init; } = -1;
}

/// <summary>
/// What a click produced: the new state, and anything the shell has to do about it.
///
/// <para><see cref="Action"/> is not an error path or an escape hatch - it is the other half of
/// the answer. A click that produces neither a changed state nor an action is a control that does
/// nothing, and <c>EveryDrawnControlDoesSomethingWhenItIsClicked</c> is the test that says so.</para>
/// </summary>
public readonly record struct SettingsOutcome(SettingsState State, SettingsAction Action, string Argument)
{
    // These two are the SAME construction, and nothing in the type marks a change: `Changed` is
    // documentary, for the reader of an arm. Do not write a test that asserts an outcome is
    // `Changed` rather than `Nothing` - it would be asserting nothing at all. The sweep compares
    // states by value instead, which is why it works.
    public static SettingsOutcome Nothing(SettingsState s) => new(s, SettingsAction.None, "");
    public static SettingsOutcome Changed(SettingsState s) => new(s, SettingsAction.None, "");
    public static SettingsOutcome Ask(SettingsState s, SettingsAction a, string argument = "") => new(s, a, argument);
}

/// <summary>
/// Every decision the settings window makes, as a pure function over a <see cref="Config"/> and
/// what is on disk. No window, no canvas, no Avalonia - so every visible setting can be asserted
/// without a screen.
/// </summary>
public static class SettingsModel
{
    private static readonly CultureInfo Fixed = CultureInfo.InvariantCulture;

    public static IReadOnlyList<Control> Controls(SettingsState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Section switch
        {
            Section.Look => Look(s),
            Section.Opening => Opening(s),
            Section.Searches => Searches(s),
            Section.Content => Content(s),
            Section.About => About(s),
            _ => [],
        };
    }

    public static IReadOnlyList<int> OptionCounts(SettingsState s) =>
        [.. Controls(s).Select(c => c.Options.Count)];

    /// <summary>How many wrapped lines each row's note takes, in the same order as
    /// <see cref="Controls"/>. The layout pushes every row down by exactly this, so a row with no
    /// note has to report zero rather than one - see ARowWithNoNoteReservesNoRoomForOne.</summary>
    public static IReadOnlyList<int> NoteLines(SettingsState s, SKTypeface face) =>
        [.. Controls(s).Select(c => Parts.Wrap(c.Note, face, Parts.NoteSize, RailLayout.ControlWidth).Count)];

    public static int ListRows(SettingsState s) =>
        s is null ? throw new ArgumentNullException(nameof(s))
                  : s.Section == Section.Searches ? VisibleExclusions(s).Count : 0;

    // ---- Look ----------------------------------------------------------------------------

    // Short, because a three-option row gives each pill 87.3px of text and "Follow Windows"
    // measures 96.4px in the shipped face, so it would render as "Follow Windo...". The words that
    // were lost live in the row's note, which has room for them. Every OS settings pane spells
    // this trio the same way for the same reason.
    private static readonly string[] ModeLabels = ["Auto", "Dark", "Light"];

    private static IReadOnlyList<Control> Look(SettingsState s)
    {
        string[] darks = [.. s.Palettes.Where(p => !p.Light).Select(p => p.Name)];
        string[] lights = [.. s.Palettes.Where(p => p.Light).Select(p => p.Name)];

        return
        [
            new Control(ControlId.Mode, ControlKind.Choice, "Mode", "", false, ModeLabels,
                        [.. ModeLabels.Select((_, i) => (int)s.Config.Mode == i)],
                        "Auto follows the Windows light/dark setting, which needs both a dark and a light palette - so it is two picks.", 0),
            new Control(ControlId.DarkPalette, ControlKind.Swatch, "Dark palette", s.Config.DarkPalette, false, darks,
                        [.. darks.Select(n => Same(n, s.Config.DarkPalette))], "", 0),
            new Control(ControlId.LightPalette, ControlKind.Swatch, "Light palette", s.Config.LightPalette, false, lights,
                        [.. lights.Select(n => Same(n, s.Config.LightPalette))], "", 0),
            Control.Plain(ControlId.PalettesFile, ControlKind.Button, "Your own palettes", "Open the file",
                note: "A palette is a name and three colours. Add one and it appears above."),
        ];
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ---- Opening it ----------------------------------------------------------------------

    private static IReadOnlyList<Control> Opening(SettingsState s) =>
    [
        Control.Plain(ControlId.Hotkey, ControlKind.Chord, "Hotkey",
            s.Capturing ? "press a combination" : s.Config.Hotkey,
            note: s.HotkeyMessage.Length > 0 ? s.HotkeyMessage : "Click, then press the combination you want."),
        Control.Plain(ControlId.ShowCapsule, ControlKind.Toggle, "Show the capsule on the desktop", on: s.Config.ShowCapsule),
        Control.Plain(ControlId.ResetCapsule, ControlKind.Button, "Bring the capsule back", "Reset its position",
            note: "For when it was dragged onto a monitor that is no longer there."),
        Control.Plain(ControlId.Autostart, ControlKind.Toggle, "Start Findra when I sign in", on: s.StartsAtLogon),
        s.Helper == HelperTaskState.Registered
            ? Control.Plain(ControlId.Helper, ControlKind.Text, "The name helper", "registered",
                note: "It starts at sign-in and reads file names. The only part needing administrator rights.")
            : Control.Plain(ControlId.Helper, ControlKind.Button, "The name helper", "Register it",
                note: s.Helper == HelperTaskState.Unknown
                    ? "Findra could not tell whether the task is there. Registering it again is safe."
                    : "Not registered, so searching by name has nothing to search. One prompt, once."),
    ];

    // ---- What it searches ------------------------------------------------------------------

    private static IReadOnlyList<Control> Searches(SettingsState s)
    {
        string[] options = ["All", .. s.Drives.Select(d => d + ":")];
        bool all = s.Config.IndexDrives.Length == 0;
        bool[] on = [all, .. s.Drives.Select(d => !all && s.Config.IndexDrives.Contains(d, StringComparer.OrdinalIgnoreCase))];

        return
        [
            new Control(ControlId.Drives, ControlKind.Choice, "Drives", "", false, options, on,
                        "Names come from every fixed volume whatever this says; this decides only what is read inside files.", 0),
            // The count is formatted invariantly, and it is the ONLY culture-sensitive format in
            // this file - which is why the culture test reads every section's Note as well as its
            // Value. Drop the Fixed argument and a German machine reads "31" as "31" but a
            // thousand-strong list as "1.000".
            Control.Plain(ControlId.AddFolder, ControlKind.Button, "Folders Findra will not open", "Add a folder",
                note: $"{s.Config.SearchExclusions.Length.ToString("N0", Fixed)} folders are skipped. Names in them stay searchable."),
        ];
    }

    public static IReadOnlyList<string> VisibleExclusions(SettingsState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        string[] all = s.Config.SearchExclusions;
        int rows = RailLayout.ListRowsThatFit;
        int from = Math.Clamp(s.ExclusionScroll, 0, Math.Max(0, all.Length - rows));
        return [.. all.Skip(from).Take(rows)];
    }

    public static SettingsState AddExclusion(SettingsState s, string path)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(path);
        // Stored as the full path with separators around it, which is the shape FileKinds.Excluded
        // normalises to anyway - so what is stored is exactly what is matched. Storing the leaf
        // name instead would skip every folder anywhere with that name.
        string entry = "\\" + path.Replace('/', '\\').Trim().Trim('\\') + "\\";
        if (s.Config.SearchExclusions.Contains(entry, StringComparer.OrdinalIgnoreCase)) return s;
        return s with { Config = s.Config with { SearchExclusions = [.. s.Config.SearchExclusions, entry] } };
    }

    // ---- Content ----------------------------------------------------------------------------

    /// <summary>Three states, three sentences, and one switch for all of them. "Paused" is the wrong
    /// word for two of them: one has never been asked for, the other was turned off.</summary>
    public static string ContentSentence(Config config, bool everIndexed, bool indexerAlive)
    {
        ArgumentNullException.ThrowIfNull(config);
        if (!config.IndexContent)
            return everIndexed
                ? "Turned off. What was already read stays searchable; nothing new is being read."
                : "Off. Searching by name works now; looking inside files walks every drive, so Findra waits to be asked.";
        return indexerAlive
            ? "On. Findra reads inside files while it is running."
            : "On, but nothing is being read - indexing only happens while Findra is open.";
    }

    private static IReadOnlyList<Control> Content(SettingsState s)
    {
        // SHORT forms for the pills, and TranscribeLimit.Describe for the value beside the label.
        // Five options give each pill 74.8px and Parts.Pill ellipsises to 62.8, which "30 minutes"
        // (65.3) does not fit. Describe() is what --content prints and parses, so it is not the
        // thing to shorten; TranscribeLimit.ShortName is, and the first-run screen's own limit row
        // reads the same list rather than writing out a second one that can drift from it.
        string[] presets = [.. TranscribeLimit.Presets.Select(TranscribeLimit.ShortName)];
        var rows = new List<Control>
        {
            new(ControlId.IndexContent, ControlKind.Toggle, "Look inside my files", "", s.Config.IndexContent, [], [],
                ContentSentence(s.Config, s.EverIndexed, s.IndexerAlive), 0),
            new(ControlId.Transcribe, ControlKind.Choice, "Transcribe up to",
                TranscribeLimit.Describe(s.Config.TranscribeMinutes), false, presets,
                [.. TranscribeLimit.Presets.Select(m => m == s.Config.TranscribeMinutes)],
                "One number for audio and video. Anything longer is skipped; raising it picks up those files.", 0),
        };

        foreach (Capability c in Capabilities.All)
        {
            if (c == Capability.Hebrew && !s.HebrewOffered) continue;
            rows.Add(s.Installed.Has(c)
                ? Control.Plain(ControlId.Capability, ControlKind.Text, Capabilities.Title(c), "installed", tag: (int)c)
                // MARGINAL, given what is already there (spec §6). Meaning and Speech share the
                // e5 pair, so a fixed per-row number makes the total fail to add up in public.
                : Control.Plain(ControlId.Capability, ControlKind.Button, Capabilities.Title(c),
                                Sizes.Human(Capabilities.MarginalBytes(c, s.Installed.Have ?? new HashSet<Capability>())), tag: (int)c));
        }

        return rows;
    }

    // ---- About -------------------------------------------------------------------------------

    /// <summary>What About says about updates. Findra never installs anything itself (spec §9b),
    /// so this is a sentence and never a button that acts.</summary>
    public static string AboutUpdateLine(string version, UpdateState state, string? latest, string? installSource) =>
        state switch
        {
            UpdateState.Available when latest is not null => UpdateCheck.Advice(installSource ?? "unknown", latest),
            UpdateState.Current => $"{version} is the newest release.",
            UpdateState.Disabled => "Not checked - the check is turned off below.",
            UpdateState.Unknown => "The last check could not reach GitHub. Nothing is wrong with this copy.",
            _ => "Not checked yet today.",
        };

    private static IReadOnlyList<Control> About(SettingsState s) =>
    [
        Control.Plain(ControlId.Version, ControlKind.Text, "Version", s.Version),
        Control.Plain(ControlId.Updates, ControlKind.Text, "Updates",
                      AboutUpdateLine(s.Version, s.Update, s.Latest, s.Config.InstallSource)),
        // "once a day" is dropped from the label and left to the note, which already says "at
        // most once every 24 hours". It is a no-option row, so it is drawn at the widest LabelW
        // rather than a narrowed column, and it is still the longest label in the window -
        // EveryRowLabelFitsItsOwnColumn measures it in the shipped face rather than trusting this.
        Control.Plain(ControlId.CheckUpdates, ControlKind.Toggle, "Check for a newer version",
            on: s.Config.CheckForUpdates,
            note: "One anonymous request to GitHub, at most once every 24 hours, in the background. " +
                  "No query parameters, no machine or install identifier, nothing about your files. " +
                  "Findra never installs anything itself, and off means the request is not made."),
        Control.Plain(ControlId.CheckNow, ControlKind.Button, "Check now", "Check"),
        Control.Plain(ControlId.InstalledVia, ControlKind.Text, "Installed via", s.Config.InstallSource ?? "unknown"),
        Control.Plain(ControlId.Removing, ControlKind.Note, "",
            note: "Removing Findra: the uninstaller, or findra --uninstall, which also removes the scheduled task. " +
                  "It keeps your index and your models unless you ask it not to."),
    ];

    // ---- what a click does ---------------------------------------------------------------------

    public static SettingsOutcome Apply(SettingsState s, PanelHit hit)
    {
        ArgumentNullException.ThrowIfNull(s);

        if (hit.Target == PanelTarget.Section && hit.Row >= 0 && hit.Row < RailLayout.Sections.Count)
            return SettingsOutcome.Changed(s with
            {
                Section = RailLayout.Sections[hit.Row],
                ExclusionScroll = 0,
                // Leaving the section cancels capture. A mode nothing cancels is one the person is
                // stuck in, reading every key they press as a hotkey.
                Capturing = false,
                HotkeyMessage = "",
            });

        IReadOnlyList<Control> rows = Controls(s);
        if (hit.Row < 0 || hit.Row >= rows.Count) return SettingsOutcome.Nothing(s);
        Control row = rows[hit.Row];
        Config c = s.Config;

        if (hit.Target == PanelTarget.ListRemove && s.Section == Section.Searches)
        {
            string[] shown = [.. VisibleExclusions(s)];
            if (hit.Row >= shown.Length) return SettingsOutcome.Nothing(s);
            // Never assign null: SearchExclusions is read on every file the indexer opens, in
            // another process, long after this click.
            return SettingsOutcome.Changed(s with
            {
                Config = c with { SearchExclusions = [.. c.SearchExclusions.Where(x => x != shown[hit.Row])] },
            });
        }

        return row.Id switch
        {
            ControlId.Mode when hit.Target == PanelTarget.Option =>
                SettingsOutcome.Changed(s with { Config = c with { Mode = (ThemeMode)hit.Option } }),
            ControlId.DarkPalette when hit.Target == PanelTarget.Option =>
                SettingsOutcome.Changed(s with { Config = c with { DarkPalette = row.Options[hit.Option] } }),
            ControlId.LightPalette when hit.Target == PanelTarget.Option =>
                SettingsOutcome.Changed(s with { Config = c with { LightPalette = row.Options[hit.Option] } }),
            ControlId.PalettesFile =>
                SettingsOutcome.Ask(s, SettingsAction.OpenPalettesFile, Paths.PalettesFile),

            // The state change is what makes the row read "press a combination"; the action is what
            // tells the window to start routing keys here.
            ControlId.Hotkey =>
                SettingsOutcome.Ask(s with { Capturing = true, HotkeyMessage = "" }, SettingsAction.CaptureChord),
            ControlId.ShowCapsule =>
                SettingsOutcome.Changed(s with { Config = c with { ShowCapsule = !c.ShowCapsule } }),
            // Both halves, for the same reason the autostart toggle needs both: the config write
            // is what survives a restart and the action is what the person actually sees happen.
            ControlId.ResetCapsule =>
                SettingsOutcome.Ask(s with { Config = c with { CapsuleX = null, CapsuleY = null } },
                                    SettingsAction.RecentreCapsule),
            // Both halves: the state so the toggle moves under the pointer, the action so the
            // registry follows. The registry is the truth and there is no config field beside it,
            // because two records of one fact can disagree.
            ControlId.Autostart =>
                SettingsOutcome.Ask(s with { StartsAtLogon = !s.StartsAtLogon },
                                    s.StartsAtLogon ? SettingsAction.ClearAutostart : SettingsAction.SetAutostart),
            ControlId.Helper when row.Kind == ControlKind.Button =>
                SettingsOutcome.Ask(s, SettingsAction.RegisterHelper),

            ControlId.Drives when hit.Target == PanelTarget.Option =>
                SettingsOutcome.Changed(s with { Config = c with { IndexDrives = ToggleDrive(c.IndexDrives, s.Drives, hit.Option) } }),
            ControlId.AddFolder => SettingsOutcome.Ask(s, SettingsAction.PickFolder),

            ControlId.IndexContent =>
                SettingsOutcome.Changed(s with { Config = c with { IndexContent = !c.IndexContent } }),
            ControlId.Transcribe when hit.Target == PanelTarget.Option =>
                SettingsOutcome.Changed(s with { Config = c with { TranscribeMinutes = TranscribeLimit.Presets[hit.Option] } }),
            ControlId.Capability when row.Kind == ControlKind.Button =>
                SettingsOutcome.Ask(s, SettingsAction.InstallCapability, ((Capability)row.Tag).ToString()),

            ControlId.CheckUpdates =>
                SettingsOutcome.Changed(s with { Config = c with { CheckForUpdates = !c.CheckForUpdates } }),
            ControlId.CheckNow => SettingsOutcome.Ask(s, SettingsAction.CheckNow),

            _ => SettingsOutcome.Nothing(s),
        };
    }

    /// <summary>Option 0 is "All", which is an EMPTY list - Config.IndexDrives documents empty as
    /// every fixed volume, and a surface that writes every letter instead would silently stop
    /// covering a disk somebody plugs in later.</summary>
    private static string[] ToggleDrive(string[] chosen, IReadOnlyList<string> available, int option)
    {
        if (option == 0) return [];
        string letter = available[option - 1];
        return chosen.Contains(letter, StringComparer.OrdinalIgnoreCase)
            ? [.. chosen.Where(d => !string.Equals(d, letter, StringComparison.OrdinalIgnoreCase))]
            : [.. chosen, letter];
    }

    // ---- the chord, captured and rebound ---------------------------------------------------------

    /// <summary>
    /// The chord a key press names, or null when it does not name one.
    ///
    /// <para>Two refusals, and both matter. A MODIFIER pressed alone is not a chord: capture
    /// begins the instant the row is clicked, so the first key down is the modifier the person is
    /// reaching for, and binding "Ctrl+" there takes the hotkey away before they finish - and the
    /// way back is the hotkey. A key with NO modifier is not a chord either: <c>RegisterHotKey</c>
    /// will take a bare letter and then swallow it in every text field in Windows.</para>
    ///
    /// <para>Built through <see cref="Hotkey.Describe"/>, so whatever comes out is a string
    /// <see cref="Hotkey.Parse"/> reads back - which is what <see cref="Rebind"/> hands to
    /// <c>RegisterHotKey</c>. A hand-rolled builder that spells "Control+F" produces a chord that
    /// saves and never registers.</para>
    /// </summary>
    public static string? ChordFrom(bool ctrl, bool alt, bool shift, bool win, uint vk)
    {
        // Hotkey's list, not a second copy: Hotkey.VirtualKeyOf names a pressed modifier precisely
        // so that this refusal is the only one, and two lists that can disagree would put the
        // window and the model on different answers about what a chord is.
        if (Hotkey.ModifierKeys.Contains(vk)) return null;

        uint mods = 0;
        if (ctrl) mods |= Hotkey.MOD_CONTROL;
        if (alt) mods |= Hotkey.MOD_ALT;
        if (shift) mods |= Hotkey.MOD_SHIFT;
        if (win) mods |= Hotkey.MOD_WIN;
        if (mods == 0) return null;

        return Hotkey.Describe(mods, vk);
    }

    public static SettingsState Rebind(SettingsState s, string chord, Func<string, bool> register)
    {
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(register);

        // Saved only if it actually registered. Saving first and hoping leaves the next launch on
        // a combination that cannot be pressed, with the control that would fix it behind a card
        // the hotkey no longer opens.
        if (!register(chord))
            return s with
            {
                Capturing = false,
                HotkeyMessage = $"{chord} is taken by something else. Findra kept {s.Config.Hotkey}.",
            };

        return s with { Config = s.Config with { Hotkey = chord }, HotkeyMessage = "", Capturing = false };
    }
}
