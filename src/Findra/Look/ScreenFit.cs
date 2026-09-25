using System;
using Avalonia;

namespace Findra;

/// <summary>
/// How far a surface has to shrink to fit the screen it opens on.
///
/// <para>Every surface is laid out in fixed units and Windows multiplies those by the monitor's
/// scaling, so a layout that is comfortable at 100% can be taller than the screen at 125% - the
/// first-run screen is 928 units, 1160 physical pixels, on a 1080p laptop whose owner took the
/// scaling Windows recommends. A surface with no title bar that runs off the bottom of the screen
/// cannot be dragged back up, and its buttons are exactly the part that is lost.</para>
///
/// <para>So a surface that does not fit is drawn smaller, as a whole, by the factor this returns:
/// the layouts, their hit tests and <c>--searchshot</c> stay in their own units, and the window
/// scales the canvas and divides the pointer. Shrinking only as far as the screen demands means
/// text lands at about the physical size it has at 100%, which is where the layouts were drawn
/// to be read.</para>
/// </summary>
public static class ScreenFit
{
    /// <summary>Units kept clear around a surface that had to shrink, so it does not sit against
    /// the taskbar or the top edge.</summary>
    public const double Margin = 24;

    /// <summary>The smallest factor ever returned. A screen that would need less than this is
    /// not one the layouts can be read on anyway, and a surface at a quarter size is worse than
    /// one that runs off the edge.</summary>
    public const double Floor = 0.5;

    /// <summary>The factor for a surface of <paramref name="width"/> by <paramref name="height"/>
    /// units on a screen whose working area is <paramref name="workArea"/> physical pixels at
    /// <paramref name="scaling"/>. One where it fits, or where anything cannot be measured.</summary>
    public static double Factor(double width, double height, PixelRect workArea, double scaling)
    {
        double s = scaling > 0 ? scaling : 1.0;
        return Factor(width, height, workArea.Width / s, workArea.Height / s);
    }

    /// <summary>The same, with the room already in units.</summary>
    public static double Factor(double width, double height, double roomWidth, double roomHeight)
    {
        if (width <= 0 || height <= 0 || roomWidth <= 0 || roomHeight <= 0) return 1.0;
        if (width <= roomWidth && height <= roomHeight) return 1.0;
        double k = Math.Min((roomWidth - Margin) / width, (roomHeight - Margin) / height);
        return Math.Clamp(k, Floor, 1.0);
    }

    /// <summary>The screen <paramref name="w"/> is on once it is showing, the primary one before
    /// that (where a centred window without an owner opens), or null where neither can be read.
    /// </summary>
    public static Avalonia.Platform.Screen? ScreenOf(Avalonia.Controls.Window w)
    {
        ArgumentNullException.ThrowIfNull(w);
        try
        {
            Avalonia.Controls.Screens? screens = w.Screens;
            return (w.IsVisible ? screens?.ScreenFromWindow(w) : null) ?? screens?.Primary;
        }
        catch (Exception ex)
        {
            Log.Warn("screen", "could not read the screen a window is on: " + ex.Message);
            return null;
        }
    }

    /// <summary>The factor for a surface of that size in window <paramref name="w"/>, on the
    /// screen it is on. One where the screen cannot be read.</summary>
    public static double For(Avalonia.Controls.Window w, double width, double height) =>
        ScreenOf(w) is { } s ? Factor(width, height, s.WorkingArea, s.Scaling) : 1.0;

    /// <summary>Move a showing window up or left just far enough that it ends inside the
    /// working area. A window that grows downwards from where it opened is otherwise free to
    /// grow off the bottom of the screen.</summary>
    public static void KeepInside(Avalonia.Controls.Window w)
    {
        ArgumentNullException.ThrowIfNull(w);
        if (!w.IsVisible || ScreenOf(w) is not { } s) return;
        PixelRect room = s.WorkingArea;
        double scaling = s.Scaling > 0 ? s.Scaling : 1.0;
        int width = (int)Math.Ceiling(w.Width * scaling), height = (int)Math.Ceiling(w.Height * scaling);
        PixelPoint at = w.Position;
        int x = Math.Clamp(at.X, room.X, Math.Max(room.X, room.Right - width));
        int y = Math.Clamp(at.Y, room.Y, Math.Max(room.Y, room.Bottom - height));
        if (x != at.X || y != at.Y) w.Position = new PixelPoint(x, y);
    }
}
