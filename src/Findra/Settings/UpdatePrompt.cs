using System;

using SkiaSharp;

namespace Findra;

/// <summary>Which of the four things the update prompt is saying, or that it is not up at all.
/// </summary>
public enum UpdatePromptState
{
    /// <summary>Not up. Every other surface answers clicks normally.</summary>
    None,

    /// <summary>The request is out. No button, because there is nothing to decide yet and a
    /// cancel that cannot stop a ten-second HTTP call is a lie.</summary>
    Asking,

    /// <summary>Findra is the newest release. One way out.</summary>
    UpToDate,

    /// <summary>There is a newer one, and something can be done about it.</summary>
    Available,

    /// <summary>GitHub could not be reached. Nothing is wrong with this copy, and saying so is
    /// most of the point - a check that fails silently is what this prompt exists to replace.
    /// </summary>
    Unreachable,

    /// <summary>The switch above is off, so <c>CheckAsync</c> short-circuits before the request
    /// and returns <c>Disabled</c> - even when the button was pressed. Without an arm of its own,
    /// pressing Check now with updates turned off would answer with silence, which is the exact
    /// defect this panel exists to remove.</summary>
    Off,
}

/// <summary>What the prompt's right-hand button does when there is one.</summary>
public enum UpdatePromptTarget { None, Close, Go }

/// <summary>
/// The panel that answers "Check now".
///
/// <para>It exists because the answer used to land in a text row above the button that asked for
/// it, which is the quietest possible place to put the one piece of news this product ever has.
/// Pressing a button and watching a sentence three rows up change is not an answer.</para>
///
/// <para><b>It is only ever raised by the button.</b> The background check runs at most once a day
/// on startup and must never put a panel over anything: a person who did not ask a question is not
/// waiting for an answer, and a dialog that appears on its own is the behaviour spec §3 forbids the
/// capsule for the same reason.</para>
///
/// <para><b>Findra still installs nothing itself.</b> "Update now" starts the upgrade the way the
/// person would have started it - winget in a window they can see, or the releases page - and the
/// binary is replaced by winget or by the installer, never by Findra. <see cref="GoLabel"/> and
/// <see cref="Body"/> say which, because the honest button depends on how this copy arrived.</para>
/// </summary>
public static class UpdatePrompt
{
    // ---- what it says -------------------------------------------------------------------------

    public const string AskingTitle = "Checking for updates";
    public const string UpToDateTitle = "You have the latest version";
    public const string UnreachableTitle = "Could not reach GitHub";
    public const string OffTitle = "Update checks are turned off";

    /// <summary>The heading. <paramref name="latest"/> is the tag the check came back with, so
    /// the one state that names a version reads as news rather than as a status.</summary>
    public static string Title(UpdatePromptState state, string? latest) => state switch
    {
        UpdatePromptState.Asking => AskingTitle,
        UpdatePromptState.UpToDate => UpToDateTitle,
        UpdatePromptState.Available => $"Findra {Clean(latest)} is available",
        UpdatePromptState.Unreachable => UnreachableTitle,
        UpdatePromptState.Off => OffTitle,
        UpdatePromptState.None => "",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "no title for this prompt state"),
    };

    /// <summary>The paragraph under it. The Available arm is where the install source matters:
    /// a winget copy is upgraded by a command, an installed one by a download, and a source build
    /// by a pull - and a button offering the wrong one of those is worse than no button.</summary>
    public static string Body(UpdatePromptState state, string version, string? latest, string? installSource)
        => state switch
        {
            UpdatePromptState.Asking => "Asking GitHub whether there is a newer release.",
            UpdatePromptState.UpToDate => $"Findra {version} is the newest release.",
            UpdatePromptState.Unreachable =>
                "The request did not get through. Nothing is wrong with this copy, and Findra will " +
                "try again tomorrow.",
            UpdatePromptState.Off =>
                "The switch above is off, so nothing was asked. Findra makes no request at all " +
                "while it is off - turn it on to check.",
            UpdatePromptState.Available => Source(installSource) switch
            {
                "winget" => $"You have {version}. Update now runs winget upgrade blakazulu.Findra " +
                            "in a window you can watch. Windows will ask for administrator rights, " +
                            "because Findra is installed for the whole machine.",
                "installer" => $"You have {version}. Update now opens the releases page, where the " +
                               "installer for this version is. Run it over the top; it keeps your " +
                               "index, your models and your settings.",
                "source" => $"You have {version}. Update now opens the releases page with the notes " +
                            "for it. Pull and rebuild to take it.",
                _ => $"You have {version}. Update now opens the releases page, which has both the " +
                     "installer and the winget command.",
            },
            UpdatePromptState.None => "",
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "no body for this prompt state"),
        };

    /// <summary>The left button, or empty where there is only one way out. Never "Cancel" on a
    /// state that has nothing to cancel.</summary>
    public static string CloseLabel(UpdatePromptState state) => state switch
    {
        UpdatePromptState.Available => "Not now",
        UpdatePromptState.UpToDate or UpdatePromptState.Unreachable or UpdatePromptState.Off => "Close",
        UpdatePromptState.Asking or UpdatePromptState.None => "",
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "no close label for this prompt state"),
    };

    /// <summary>The right button, or empty where there is nothing to do. Only Available has one -
    /// "Close" is the whole answer everywhere else, and a second pill beside it doing the same
    /// thing is a choice with no difference in it.</summary>
    public static string GoLabel(UpdatePromptState state, string? installSource) =>
        state != UpdatePromptState.Available ? ""
        : Source(installSource) == "winget" ? "Update now" : "Open releases";

    /// <summary>Two buttons or one. Asking has none: there is nothing to decide until the answer
    /// arrives, and the request cannot be called back.</summary>
    public static int Buttons(UpdatePromptState state) => state switch
    {
        UpdatePromptState.Available => 2,
        UpdatePromptState.UpToDate or UpdatePromptState.Unreachable or UpdatePromptState.Off => 1,
        UpdatePromptState.Asking or UpdatePromptState.None => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "no button count for this prompt state"),
    };

    private static string Source(string? installSource) =>
        (installSource ?? "unknown").ToLowerInvariant();

    /// <summary>Tags carry a leading v and the prompt does not. Everything else about the string
    /// is left alone: it is what GitHub returned, and inventing a shape for it is how a version
    /// nobody recognises ends up on screen.</summary>
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

    /// <summary>How tall the panel is, which depends on how many lines the body wraps to and
    /// whether there are buttons under it. Measured rather than guessed, on the shipped face, for
    /// the reason every other band on these surfaces is.</summary>
    public static float Height(int bodyLines, int buttons) =>
        Pad + TitleSize + 14f + Parts.NoteHeight(Math.Max(1, bodyLines))
        + (buttons > 0 ? 18f + ButtonH : 0f) + Pad;

    /// <summary>The panel, centred in the settings window. It is drawn over the pane rather than
    /// beside it, so its rectangle is the only thing on the surface that answers a click while it
    /// is up.</summary>
    public static SKRect Panel(float surfaceWidth, float surfaceHeight, int bodyLines, int buttons)
    {
        float h = Height(bodyLines, buttons);
        float left = (surfaceWidth - Width) / 2f;
        float top = (surfaceHeight - h) / 2f;
        return new SKRect(left, top, left + Width, top + h);
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

    /// <summary>What is under the pointer. Everything outside the two buttons is None, INCLUDING
    /// the panel's own body and the dimmed pane behind it: a click on the scrim must not dismiss
    /// a question the person has not answered, and a click on the pane behind must not reach a
    /// control they cannot see.</summary>
    public static UpdatePromptTarget HitTest(float x, float y, SKRect panel, int buttons)
    {
        if (buttons >= 1 && Button(panel, 0, buttons).Contains(x, y)) return UpdatePromptTarget.Close;
        if (buttons == 2 && Button(panel, 1, buttons).Contains(x, y)) return UpdatePromptTarget.Go;
        return UpdatePromptTarget.None;
    }
}
