using System;
using System.IO;
using System.Reflection;

using Avalonia.Controls;

namespace Findra;

/// <summary>
/// The window icon, read once out of the assembly.
///
/// <para>Avalonia does not give a window the executable's own icon, so a <c>Window</c> that never
/// sets <see cref="Window.Icon"/> shows whatever the shell uses for an application that supplied
/// none. Findra has two windows in the taskbar - the settings window and the first-run screen -
/// and both showed that placeholder. The card and the capsule do not, because they carry
/// WS_EX_TOOLWINDOW and are hidden from the taskbar and Alt-Tab entirely.
///
/// <para>Resolved lazily and once, and a failure is a log line rather than an exception, for the
/// reason <see cref="Parts.Face"/> gives: a missing or unreadable resource must not stop the
/// application starting, and a static initialiser that throws is unreportable. A window with no
/// icon is a cosmetic loss; a window that never opens is not.</para>
/// </summary>
public static class AppIcon
{
    private const string Resource = "Findra.findra.ico";

    private static readonly Lazy<WindowIcon?> Loaded = new(Read, isThreadSafe: true);

    /// <summary>The icon, or null where it could not be read. Null is a normal state.</summary>
    public static WindowIcon? Value => Loaded.Value;

    /// <summary>Puts it on a window, and does nothing at all if there is none to put.</summary>
    public static void Apply(Window window)
    {
        ArgumentNullException.ThrowIfNull(window);
        if (Value is { } icon) window.Icon = icon;
    }

    private static WindowIcon? Read()
    {
        try
        {
            using Stream? s = typeof(AppIcon).Assembly.GetManifestResourceStream(Resource);
            if (s is null)
            {
                Log.Warn("app", $"the window icon '{Resource}' is not in the assembly; the taskbar will show a default");
                return null;
            }

            return new WindowIcon(s);
        }
        catch (Exception ex)
        {
            Log.Warn("app", "the window icon could not be read: " + ex.Message);
            return null;
        }
    }
}
