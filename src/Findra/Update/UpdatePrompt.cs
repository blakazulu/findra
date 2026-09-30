using System;

using SkiaSharp;

namespace Findra;

/// <summary>What the update window's right-hand button does when there is one.</summary>
public enum UpdatePromptTarget { None, Close, Go }

/// <summary>
/// What the update window says, and where each part of it is.
///
/// <para>The window is only ever opened by a person: the tray's Check for updates or Settings'
/// Check now. The daily background check never opens it, because a person who did not ask a
/// question is not waiting for an answer (spec §3).</para>
///
/// <para>Findra still replaces none of its own files. Update now runs the installer or winget,
/// and they do; the body of each offer says which, because the honest button depends on how this
/// copy arrived.</para>
/// </summary>
public static class UpdatePrompt
{
    // ---- what it says -------------------------------------------------------------------------

    public static string Title(UpdateView v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return v.Step switch
        {
            UpdateStep.Checking => "Checking for updates",
            UpdateStep.UpToDate => "You have the latest version",
            UpdateStep.Unreachable => "Could not reach GitHub",
            UpdateStep.Available => $"Findra {Clean(v.Latest)} is available",
            UpdateStep.Downloading => $"Downloading Findra {Clean(v.Latest)}",
            UpdateStep.DownloadFailed => "The download did not work",
            UpdateStep.Installing => $"Installing Findra {Clean(v.Latest)}",
            UpdateStep.InstallerDidNotRun => "The installer did not run",
            UpdateStep.Winget => $"Updating Findra to {Clean(v.Latest)}",
            UpdateStep.WingetFailed => "winget did not update Findra",
            UpdateStep.Store => "The Microsoft Store updates Findra",
            _ => throw new ArgumentOutOfRangeException(nameof(v), v.Step, "no title for this step"),
        };
    }

    public static string Body(UpdateView v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return v.Step switch
        {
            UpdateStep.Checking => "Asking GitHub whether there is a newer release.",
            UpdateStep.UpToDate => Winget(v)
                ? $"Findra {v.Version} is the newest version winget has."
                : $"Findra {v.Version} is the newest release.",
            UpdateStep.Unreachable => "The request did not get through. Nothing is wrong with this copy.",
            UpdateStep.Available => UpdateFlow.Route(v) switch
            {
                UpdateRoute.Installer =>
                    $"You have {v.Version}. Update now downloads the installer ({Sizes.Human(v.Installer!.Size)}), " +
                    "checks it, and runs it. Findra closes while it installs and opens again when it is done. " +
                    "Windows will ask for permission.",
                UpdateRoute.Winget =>
                    $"You have {v.Version}. Update now runs winget upgrade blakazulu.Findra. Findra closes while " +
                    "it installs and opens again when it is done. Windows will ask for permission.",
                UpdateRoute.Releases when Source(v) == "source" =>
                    $"You have {v.Version}. Open releases shows the notes for it. Pull and rebuild to take it.",
                UpdateRoute.Releases =>
                    $"You have {v.Version}. This release has no installer Findra can check for this computer, " +
                    "so Open releases takes you to the release page.",
                _ => throw new ArgumentOutOfRangeException(nameof(v), UpdateFlow.Route(v), "no offer for this route"),
            },
            UpdateStep.Downloading => "Checked against the checksum GitHub publishes before anything runs.",
            UpdateStep.Installing => "Findra will close now and open again when the installer is done.",
            UpdateStep.Winget => "winget is installing it. Findra will close while it installs and open again when it is done.",
            UpdateStep.DownloadFailed or UpdateStep.InstallerDidNotRun or UpdateStep.WingetFailed =>
                v.Problem ?? "Something went wrong. Nothing was installed.",
            UpdateStep.Store =>
                $"You have {v.Version}. This copy came from the Microsoft Store, which installs its updates. " +
                "Open Store shows whether one is waiting.",
            _ => throw new ArgumentOutOfRangeException(nameof(v), v.Step, "no body for this step"),
        };
    }

    /// <summary>The left button, or the only one. Empty where there is none.</summary>
    public static string CloseLabel(UpdateView v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return v.Step switch
        {
            UpdateStep.Available => "Not now",
            UpdateStep.Downloading => "Cancel",
            UpdateStep.UpToDate or UpdateStep.Unreachable or UpdateStep.DownloadFailed
                or UpdateStep.InstallerDidNotRun or UpdateStep.WingetFailed or UpdateStep.Store => "Close",
            UpdateStep.Checking or UpdateStep.Installing or UpdateStep.Winget => "",
            _ => throw new ArgumentOutOfRangeException(nameof(v), v.Step, "no close label for this step"),
        };
    }

    /// <summary>The right button. Empty where there is none.</summary>
    public static string GoLabel(UpdateView v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return v.Step switch
        {
            UpdateStep.Available => UpdateFlow.Route(v) == UpdateRoute.Releases ? "Open releases" : "Update now",
            UpdateStep.Unreachable or UpdateStep.DownloadFailed or UpdateStep.InstallerDidNotRun => "Try again",
            UpdateStep.Store => "Open Store",
            UpdateStep.Checking or UpdateStep.UpToDate or UpdateStep.Downloading or UpdateStep.Installing
                or UpdateStep.Winget or UpdateStep.WingetFailed => "",
            _ => throw new ArgumentOutOfRangeException(nameof(v), v.Step, "no go label for this step"),
        };
    }

    public static int Buttons(UpdateView v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return v.Step switch
        {
            UpdateStep.Available or UpdateStep.Unreachable or UpdateStep.DownloadFailed or UpdateStep.InstallerDidNotRun
                or UpdateStep.Store => 2,
            UpdateStep.UpToDate or UpdateStep.Downloading or UpdateStep.WingetFailed => 1,
            UpdateStep.Checking or UpdateStep.Installing or UpdateStep.Winget => 0,
            _ => throw new ArgumentOutOfRangeException(nameof(v), v.Step, "no button count for this step"),
        };
    }

    public static bool HasBar(UpdateView v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return v.Step == UpdateStep.Downloading;
    }

    /// <summary>"34 MB of 82 MB", in the formatter every other size in Findra uses.</summary>
    public static string Count(UpdateView v)
    {
        ArgumentNullException.ThrowIfNull(v);
        return $"{Sizes.Human(v.Got)} of {Sizes.Human(v.Total)}";
    }

    private static bool Winget(UpdateView v) => Source(v) == "winget";

    private static string Source(UpdateView v) => (v.InstallSource ?? "unknown").ToLowerInvariant();

    /// <summary>Tags carry a leading v and the window does not.</summary>
    private static string Clean(string? tag) =>
        tag is { Length: > 0 } t && (t[0] == 'v' || t[0] == 'V') ? t[1..] : tag ?? "";

    // ---- where it is ---------------------------------------------------------------------------

    public const float Width = 460f;
    public const float Pad = 24f;
    public const float ButtonW = 116f;
    public const float ButtonH = 34f;
    public const float ButtonGap = 10f;
    public const float TitleSize = 17f;
    public const float Radius = 14f;
    private const float BarGap = 14f;

    /// <summary>How tall the window is: measured body lines, the download bar when there is one,
    /// the buttons when there are any.</summary>
    public static float Height(int bodyLines, int buttons, bool bar) =>
        Pad + TitleSize + 14f + Parts.NoteHeight(Math.Max(1, bodyLines))
        + (bar ? BarGap + ProgressPillLayout.Height : 0f)
        + (buttons > 0 ? 18f + ButtonH : 0f) + Pad;

    /// <summary>The window's whole surface, at the origin: the window is the panel.</summary>
    public static SKRect Panel(int bodyLines, int buttons, bool bar) =>
        new(0, 0, Width, Height(bodyLines, buttons, bar));

    /// <summary>The download bar, under the body.</summary>
    public static SKRect Bar(SKRect panel, int bodyLines)
    {
        float top = panel.Top + Pad + TitleSize + 14f + Parts.NoteHeight(Math.Max(1, bodyLines)) + BarGap;
        return new SKRect(panel.Left + Pad, top, panel.Right - Pad, top + ProgressPillLayout.Height);
    }

    /// <summary>Button <paramref name="i"/> from the RIGHT, so the affirmative one is always the
    /// rightmost whether there are one or two. Index 0 is Close, index 1 is Go.</summary>
    public static SKRect Button(SKRect panel, int i, int buttons)
    {
        float right = panel.Right - Pad;
        // With two buttons Go is rightmost and Close sits to its left; with one, Close is
        // rightmost. Counting from the right is what makes those the same expression.
        int fromRight = buttons == 2 ? (i == 1 ? 0 : 1) : 0;
        float x = right - (fromRight + 1) * ButtonW - fromRight * ButtonGap;
        return new SKRect(x, panel.Bottom - Pad - ButtonH, x + ButtonW, panel.Bottom - Pad);
    }

    /// <summary>What a press does. The second press of a double-click does nothing: the button a
    /// first press brings up can sit exactly where the pressed one was - Cancel is where Update now
    /// was - so a double-click on Update now would otherwise start the download and cancel it.
    /// </summary>
    public static UpdatePromptTarget Press(float x, float y, SKRect panel, int buttons, int clickCount) =>
        clickCount > 1 ? UpdatePromptTarget.None : HitTest(x, y, panel, buttons);

    /// <summary>What is under the pointer. Only the buttons answer; everything else moves the
    /// window.</summary>
    public static UpdatePromptTarget HitTest(float x, float y, SKRect panel, int buttons)
    {
        if (buttons >= 1 && Button(panel, 0, buttons).Contains(x, y)) return UpdatePromptTarget.Close;
        if (buttons == 2 && Button(panel, 1, buttons).Contains(x, y)) return UpdatePromptTarget.Go;
        return UpdatePromptTarget.None;
    }
}
