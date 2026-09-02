using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Findra;

/// <summary>The order combinations are tried in. Pure, so the de-duplication that keeps a user's
/// own choice from being tried twice is testable without a window.</summary>
public static class HotkeyChain
{
    /// <summary>The user's choice first, then the shipped fallbacks, with duplicates removed.
    /// Comparison is on the canonical form, so "ctrl+alt+f" and "Ctrl+Alt+F" are one entry and
    /// the chain does not waste a registration attempt proving it.</summary>
    public static IReadOnlyList<string> Build(string? preferred, IReadOnlyList<string> defaults)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var chain = new List<string>(defaults.Count + 1);

        void Add(string? entry)
        {
            if (string.IsNullOrWhiteSpace(entry)) return;
            string text = entry.Trim();
            string key = Hotkey.Parse(text) is { } p ? Hotkey.Describe(p.Mods, p.Vk) : text;
            if (seen.Add(key)) chain.Add(text);
        }

        Add(preferred);
        foreach (string d in defaults) Add(d);
        return chain;
    }
}

/// <summary>
/// The global hotkey: a hidden window that owns the `RegisterHotKey` registration and the
/// `WM_HOTKEY` messages it produces.
///
/// The host is its own window rather than the capsule's, because `Config.ShowCapsule = false` is a
/// supported way to run Findra and the hotkey has to keep working when there is no capsule to hang
/// it on. It is one device-independent pixel, parked off every monitor, transparent, and hidden
/// from Alt+Tab, so nothing about it reaches the screen.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class HotkeyHost : IDisposable
{
    // Any value 0x0000-0xBFFF is ours to choose; it only has to be unique within this process.
    private const int HotkeyId = 0x4649;
    private const uint WM_HOTKEY = 0x0312;

    // Far outside any plausible desktop, which is where Windows itself parks things it does not
    // want drawn.
    private static readonly PixelPoint OffScreen = new(-32000, -32000);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    // Named for Win32's POINT rather than "Point": Avalonia.Point is in scope in this file, and a
    // nested type that silently wins the name is a trap for whoever edits it next.
    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    private readonly Window _host;
    private readonly Win32Properties.CustomWndProcHookCallback _hook;
    private bool _registered;
    private bool _disposed;

    /// <summary>The combination that actually registered, or null when the whole chain was
    /// refused. The tray shows this either way - a hotkey that does nothing with no explanation is
    /// the worst outcome there is.</summary>
    public string? Landed { get; private set; }

    public event Action? Pressed;

    /// <summary>The monitor list, taken from the one window that exists whether or not the capsule
    /// does. Both open paths need it: one to dim the capsule's monitor, one the cursor's.</summary>
    public Screens? Screens => _host.Screens;

    public HotkeyHost()
    {
        _hook = OnMessage;
        _host = new Window
        {
            Title = "Findra",
            WindowDecorations = Avalonia.Controls.WindowDecorations.None,
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent },
            Background = Brushes.Transparent,
            ShowInTaskbar = false,
            ShowActivated = false,
            CanResize = false,
            Focusable = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Width = 1,
            Height = 1,
            Position = OffScreen,
        };
        WindowStyle.HideFromAltTab(_host);
        WindowStyle.MakeInputTransparent(_host);
    }

    /// <summary>Shows the host, walks the chain, and says in the log which combination it got -
    /// or that it got none. Never throws: a machine where no combination is free still has a
    /// capsule and a tray menu.</summary>
    public void Start(IReadOnlyList<string> chain)
    {
        try
        {
            _host.Show();
            _host.Position = OffScreen;   // re-asserted: Show can move a window Windows thinks is lost
            Win32Properties.AddWndProcHookCallback(_host, _hook);
        }
        catch (Exception ex)
        {
            Log.Error("hotkey", "the hotkey host window could not be created", ex);
            return;
        }

        IntPtr hwnd = Handle();
        if (hwnd == IntPtr.Zero)
        {
            Log.Warn("hotkey", "the hotkey host window has no handle, so no combination could be registered; the capsule and the tray still open the card");
            return;
        }

        Landed = Hotkey.RegisterFirstThatWorks(chain,
            (mods, vk) => RegisterHotKey(hwnd, HotkeyId, mods | Hotkey.MOD_NOREPEAT, vk));

        if (Landed is null)
        {
            Log.Warn("hotkey", "no hotkey combination could be registered; the capsule and the tray still open the card");
            return;
        }

        _registered = true;
        Log.Info("hotkey", $"registered {Landed}");
    }

    /// <summary>
    /// Swap the registered combination. Returns false and **restores the previous one** when the
    /// new combination will not register.
    ///
    /// <para>The order matters and there is only one safe one. Windows will not register a second
    /// hotkey on the same id, so the old registration has to go first - and if the new one then
    /// fails and nothing puts the old one back, the user is left with no hotkey at all, having
    /// touched a control that reported a failure. That is worse than the state they were in, and
    /// the control that would fix it sits behind a card the hotkey no longer opens.</para>
    ///
    /// <para>The restore path cannot be exercised headlessly - it needs a real window handle and a
    /// combination another application already owns - so it is end-to-end checklist step 28 and
    /// nothing else. No test covers it.</para>
    /// </summary>
    public bool Rebind(string chord)
    {
        IntPtr hwnd = Handle();
        if (hwnd == IntPtr.Zero) return false;
        if (Hotkey.Parse(chord) is not { } want) return false;

        string? previous = Landed;
        if (_registered) { UnregisterHotKey(hwnd, HotkeyId); _registered = false; }

        if (RegisterHotKey(hwnd, HotkeyId, want.Mods | Hotkey.MOD_NOREPEAT, want.Vk))
        {
            _registered = true;
            Landed = Hotkey.Describe(want.Mods, want.Vk);
            Log.Info("hotkey", $"rebound to {Landed}");
            return true;
        }

        if (previous is not null && Hotkey.Parse(previous) is { } back &&
            RegisterHotKey(hwnd, HotkeyId, back.Mods | Hotkey.MOD_NOREPEAT, back.Vk))
        {
            _registered = true;
            Landed = previous;
        }
        Log.Warn("hotkey", $"{chord} would not register; kept {Landed ?? "nothing"}");
        return false;
    }

    /// <summary>Where the pointer is, in physical pixels. The hotkey opens the card on the monitor
    /// the user is looking at, which is the one their cursor is on - not the one the capsule
    /// happens to rest on.</summary>
    public static PixelPoint CursorPosition()
    {
        try { if (GetCursorPos(out NativePoint p)) return new PixelPoint(p.X, p.Y); }
        catch (Exception ex) { Log.Once("hotkey|cursor", "WARN", "hotkey", "the cursor position could not be read: " + ex.Message); }
        return new PixelPoint(0, 0);
    }

    private IntPtr OnMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY || (int)wParam != HotkeyId) return IntPtr.Zero;
        handled = true;
        try { Pressed?.Invoke(); }
        catch (Exception ex) { Log.Error("hotkey", "the hotkey handler failed", ex); }
        return IntPtr.Zero;
    }

    private IntPtr Handle() => _host.TryGetPlatformHandle() is { } h ? h.Handle : IntPtr.Zero;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            IntPtr hwnd = Handle();
            if (_registered && hwnd != IntPtr.Zero) UnregisterHotKey(hwnd, HotkeyId);
            Win32Properties.RemoveWndProcHookCallback(_host, _hook);
        }
        catch { /* shutting down; a failed unregister dies with the process anyway */ }
        try { _host.Close(); } catch { }
    }
}
