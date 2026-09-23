using System;

using SkiaSharp;

namespace Findra;

/// <summary>What the pointer is over in the Remove panel.</summary>
public enum RemoveTarget { None, Keep, Cancel, Remove }

/// <summary>
/// The question "Remove" asks before it deletes anything.
///
/// <para>It says what goes, how much space comes back, what else goes with it, and - behind one
/// tick, on by default - whether what was already found is kept. Kept means adding it back later
/// does not read everything again; for Photos and Meaning it does NOT mean still searchable,
/// because searching them needs the add-on, and the words say so.</para>
///
/// <para>Where removing deletes nothing - Meaning while Speech needs the same files - the panel
/// says so and the button says "Turn off", because that is all it can do.</para>
/// </summary>
public static class RemovePrompt
{
    // ---- what it says -------------------------------------------------------------------------

    public static string Title(Capability c) => $"Remove {Capabilities.Title(c)}?";

    /// <summary>The paragraph: what stops, what comes back, and what goes with it.</summary>
    public static string Body(Capability c, CapabilitySet installed)
    {
        string stops = c switch
        {
            Capability.Photos => "Findra stops reading photos and video, and you can no longer search them by what is in them.",
            Capability.Meaning => "Findra stops searching documents by meaning. Their words can still be found.",
            Capability.Speech => "Findra stops listening to recordings and videos.",
            Capability.Hebrew => "Hebrew recordings get the ordinary transcript from now on.",
            _ => "",
        };
        if (AddOns.OnlyTurnsOff(c, installed))
            return $"Speech uses the same files, so they stay on this PC and nothing is freed. {stops}";

        string body = $"This frees {Sizes.Human(AddOns.Frees(c, installed))}. {stops}";
        foreach (Capability also in AddOns.RemovedWith(c, installed))
            if (also != c) body += $" This also removes {Capabilities.Title(also)}.";
        return body;
    }

    /// <summary>The tick's words, or empty where there is nothing of its own to keep (the Hebrew
    /// pass leaves ordinary transcripts).</summary>
    public static string KeepLabel(Capability c) => c switch
    {
        Capability.Photos or Capability.Meaning => "Keep what was already found, so adding it back later is quick",
        Capability.Speech => "Keep what was already heard, so it can still be found by its words",
        _ => "",
    };

    public const string CancelLabel = "Cancel";

    public static string GoLabel(Capability c, CapabilitySet installed) =>
        AddOns.OnlyTurnsOff(c, installed) ? "Turn off" : "Remove";

    // ---- where it is ---------------------------------------------------------------------------

    public const float BoxSize = 18f;
    public const float BoxGap = 10f;
    public const float KeepGap = 14f;

    /// <summary>Height of the tick row, which wraps its label beside the box.</summary>
    public static float KeepHeight(int keepLines) =>
        keepLines <= 0 ? 0f : KeepGap + Math.Max(BoxSize, Parts.NoteHeight(keepLines));

    public static float Height(int bodyLines, int keepLines) =>
        UpdatePrompt.Pad + UpdatePrompt.TitleSize + 14f + Parts.NoteHeight(Math.Max(1, bodyLines))
        + KeepHeight(keepLines) + 18f + UpdatePrompt.ButtonH + UpdatePrompt.Pad;

    public static SKRect Panel(float surfaceWidth, float surfaceHeight, int bodyLines, int keepLines)
    {
        float h = Height(bodyLines, keepLines);
        float left = (surfaceWidth - UpdatePrompt.Width) / 2f;
        float top = (surfaceHeight - h) / 2f;
        return new SKRect(left, top, left + UpdatePrompt.Width, top + h);
    }

    /// <summary>The width the body and the tick label wrap to.</summary>
    public static float TextWidth => UpdatePrompt.Width - 2 * UpdatePrompt.Pad;
    public static float KeepTextWidth => TextWidth - BoxSize - BoxGap;

    /// <summary>The tick row: box and label together, so a click on the words ticks it too.</summary>
    public static SKRect KeepRow(SKRect panel, int bodyLines, int keepLines)
    {
        float top = panel.Top + UpdatePrompt.Pad + UpdatePrompt.TitleSize + 14f
                    + Parts.NoteHeight(Math.Max(1, bodyLines)) + KeepGap;
        return new SKRect(panel.Left + UpdatePrompt.Pad, top, panel.Right - UpdatePrompt.Pad,
                          top + Math.Max(BoxSize, Parts.NoteHeight(keepLines)));
    }

    public static SKRect Box(SKRect keepRow) =>
        new(keepRow.Left, keepRow.Top + 2, keepRow.Left + BoxSize, keepRow.Top + 2 + BoxSize);

    /// <summary>Cancel on the left, the affirmative button on the right - the update panel's order.</summary>
    public static SKRect Button(SKRect panel, bool go) => UpdatePrompt.Button(panel, go ? 1 : 0, 2);

    /// <summary>Everything outside the tick row and the two buttons is None - the panel's body and
    /// the dimmed pane behind it answer nothing, as the update panel's do.</summary>
    public static RemoveTarget HitTest(float x, float y, SKRect panel, int bodyLines, int keepLines)
    {
        if (keepLines > 0 && KeepRow(panel, bodyLines, keepLines).Contains(x, y)) return RemoveTarget.Keep;
        if (Button(panel, go: false).Contains(x, y)) return RemoveTarget.Cancel;
        if (Button(panel, go: true).Contains(x, y)) return RemoveTarget.Remove;
        return RemoveTarget.None;
    }
}
