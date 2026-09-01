using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia.Controls;

namespace Findra;

// Forces a top-level window to be a Win32 "tool window": removed from Alt+Tab and the taskbar.
// Avalonia's ShowInTaskbar=false does not reliably leave WS_EX_TOOLWINDOW on a borderless,
// transparent, unowned window such as the card or the dim overlay, so it can still surface in
// Alt+Tab. We re-assert the style in the Opened handler - after Avalonia has finished its own
// window setup - so ours is the last writer.
[SupportedOSPlatform("windows")]
internal static class WindowStyle
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOOLWINDOW = 0x00000080; // no taskbar button, not in Alt+Tab
    private const long WS_EX_APPWINDOW = 0x00040000;  // forces a taskbar button — must be cleared
    private const long WS_EX_TRANSPARENT = 0x00000020;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    // Hide the window from Alt+Tab and the taskbar. Idempotent; safe to call before the native
    // handle exists (it re-applies on Opened). Wire this in the window's constructor.
    public static void HideFromAltTab(Window window)
    {
        window.Opened += (_, _) => Apply(window);
        Apply(window); // in case the handle already exists (window re-shown)
    }

    private static void Apply(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero) return;
        long ex = (long)GetWindowLongPtr(handle.Handle, GWL_EXSTYLE);
        long want = (ex | WS_EX_TOOLWINDOW) & ~WS_EX_APPWINDOW;
        if (want != ex)
        {
            SetWindowLongPtr(handle.Handle, GWL_EXSTYLE, (IntPtr)want);
            Log.Once("toolwindow|" + window.GetType().Name, "INFO", "app",
                $"{window.GetType().Name}: applied WS_EX_TOOLWINDOW (hidden from Alt+Tab)");
        }
    }

    // A fullscreen overlay must never become the mouse target or activate when clicked. Used by
    // the dim layer behind the open card, so a click on the desktop underneath it still reaches
    // the desktop.
    public static void MakeInputTransparent(Window window)
    {
        window.Opened += (_, _) => ApplyInputTransparent(window);
        ApplyInputTransparent(window);
    }

    private static void ApplyInputTransparent(Window window)
    {
        var handle = window.TryGetPlatformHandle();
        if (handle is null || handle.Handle == IntPtr.Zero) return;
        long ex = (long)GetWindowLongPtr(handle.Handle, GWL_EXSTYLE);
        long want = ex | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
        if (want != ex) SetWindowLongPtr(handle.Handle, GWL_EXSTYLE, (IntPtr)want);
    }
}
