using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using SkiaSharp;

namespace Findra;

/// <summary>
/// Where the capsule is allowed to rest. Pure arithmetic over rectangles, deliberately separate
/// from the window: a saved position that lands on a monitor that has since been unplugged is the
/// one case that must not be discovered by a user staring at an empty desktop, and it is the one
/// case a window cannot be unit tested for.
/// </summary>
public static class CapsulePlacement
{
    /// <summary>How much of the capsule has to be on a monitor for the saved position to count
    /// as usable. A few pixels peeking over an edge is not a widget anyone can click.</summary>
    public const int MinVisibleWidth = 64;
    public const int MinVisibleHeight = 32;

    /// <summary>How far above the bottom of the working area a fresh capsule sits.</summary>
    public const int BottomMargin = 48;

    public static bool IsOnAnyScreen(PixelRect capsule, IReadOnlyList<PixelRect> screens)
    {
        foreach (PixelRect s in screens)
        {
            int w = Math.Min(capsule.Right, s.Right) - Math.Max(capsule.X, s.X);
            int h = Math.Min(capsule.Bottom, s.Bottom) - Math.Max(capsule.Y, s.Y);
            if (w >= Math.Min(MinVisibleWidth, capsule.Width) &&
                h >= Math.Min(MinVisibleHeight, capsule.Height)) return true;
        }
        return false;
    }

    /// <summary>The resting place for a capsule that has never been dragged: centred across the
    /// working area, a little above its bottom edge, clear of the taskbar.</summary>
    public static PixelPoint BottomCentre(PixelRect workingArea, int width, int height)
    {
        int x = workingArea.X + (workingArea.Width - width) / 2;
        int y = workingArea.Y + workingArea.Height - height - BottomMargin;
        return Clamp(new PixelPoint(x, y), workingArea, width, height);
    }

    /// <summary>Pull a position back until the whole capsule is inside the rectangle. A working
    /// area smaller than the capsule clamps to its top-left rather than inverting.</summary>
    public static PixelPoint Clamp(PixelPoint at, PixelRect workingArea, int width, int height)
    {
        int maxX = Math.Max(workingArea.X, workingArea.Right - width);
        int maxY = Math.Max(workingArea.Y, workingArea.Bottom - height);
        return new PixelPoint(Math.Clamp(at.X, workingArea.X, maxX), Math.Clamp(at.Y, workingArea.Y, maxY));
    }

    /// <summary>Index of the screen containing a point, or -1 when the point is in the gap
    /// between two monitors or outside every one of them.</summary>
    public static int ScreenIndexAt(PixelPoint at, IReadOnlyList<PixelRect> screens)
    {
        for (int i = 0; i < screens.Count; i++)
        {
            PixelRect s = screens[i];
            if (at.X >= s.X && at.X < s.Right && at.Y >= s.Y && at.Y < s.Bottom) return i;
        }
        return -1;
    }
}

/// <summary>
/// The resting look on the desktop: a borderless, transparent capsule that sits at the BOTTOM of
/// the Z order, never takes focus, never appears in Alt+Tab, and unfolds into the card when it is
/// clicked. Dragging it moves it; the position is written to the config once, on release, not on
/// every pixel of the drag.
///
/// Everything visual comes from <see cref="CapsulePainter"/> - the same painter `--searchshot`
/// renders - through a Skia custom draw operation, so what ships and what a screenshot shows are
/// the same code.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class CapsuleWindow : Window
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_NOACTIVATE = 0x08000000;

    private const uint WM_WINDOWPOSCHANGING = 0x0046;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private static readonly IntPtr HWND_BOTTOM = new(1);

    /// <summary>A press that travels further than this is a drag, not a click.</summary>
    private const double ClickSlop = 4.0;

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
                                            int x, int y, int cx, int cy, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public IntPtr Hwnd;
        public IntPtr HwndInsertAfter;
        public int X, Y, Cx, Cy;
        public uint Flags;
    }

    private readonly CapsuleCanvas _canvas;
    private readonly Win32Properties.CustomWndProcHookCallback _hook;
    private bool _hooked;

    /// <summary>Raised on a press that did not turn into a drag - the gesture that opens the card.</summary>
    public event Action? Clicked;

    /// <summary>Raised once, when a drag ends, with the new top-left in physical pixels. The
    /// shell writes it to the config; the window itself owns no settings.</summary>
    public event Action<PixelPoint>? Moved;

    /// <summary>The line under the bar. Empty means no line is drawn at all - an idle widget with
    /// a permanently visible empty progress bar looks busy when it is not. Content indexing sets
    /// this in a later plan; it is a property rather than a constant so that wiring is one line.</summary>
    public string Progress
    {
        get => _canvas.Progress;
        set { _canvas.Progress = value; _canvas.InvalidateVisual(); }
    }

    public float ProgressFraction
    {
        get => _canvas.ProgressFraction;
        set { _canvas.ProgressFraction = value; _canvas.InvalidateVisual(); }
    }

    /// <summary>The capsule's bar in layout units, which is what <see cref="CardWindow.PlaceOver"/>
    /// wants: the card lands so its field sits exactly where this bar was.</summary>
    public static SKRect BarRect => new(
        0, (CapsuleLayout.Height - CapsuleLayout.BarH) / 2f,
        CapsuleLayout.Width, (CapsuleLayout.Height + CapsuleLayout.BarH) / 2f);

    public CapsuleWindow(Palette palette, double scale)
    {
        _canvas = new CapsuleCanvas(Derived.From(palette), scale);
        Content = _canvas;

        Title = "Findra";
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;      // clicking the desktop must not hand the desktop's focus away
        CanResize = false;
        Topmost = false;            // the opposite: it lives under everything (see OnMessage)
        Focusable = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        SizeToContent = SizeToContent.Manual;
        Width = CapsuleLayout.Width * scale;
        Height = CapsuleLayout.Height * scale;
        WindowStyle.HideFromAltTab(this);

        _canvas.Clicked += () => Clicked?.Invoke();
        _canvas.Moved += p => Moved?.Invoke(p);

        // WM_WINDOWPOSCHANGING is the only reliable way to stay at the bottom: without it, one
        // click on the capsule (or any Z-order change Windows makes on its own) lifts it above the
        // windows it is meant to sit beneath, and re-asserting afterwards flickers.
        _hook = OnMessage;
        Opened += (_, _) =>
        {
            ApplyNoActivate();
            HookMessages();
            PushToBottom();
        };
        Closed += (_, _) => UnhookMessages();
    }

    private void HookMessages()
    {
        if (_hooked) return;
        try { Win32Properties.AddWndProcHookCallback(this, _hook); _hooked = true; }
        catch (Exception ex) { Log.Warn("app", "the capsule could not hook window messages: " + ex.Message); }
    }

    private void UnhookMessages()
    {
        if (!_hooked) return;
        try { Win32Properties.RemoveWndProcHookCallback(this, _hook); }
        catch { /* the window is going away anyway */ }
        _hooked = false;
    }

    private IntPtr OnMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_WINDOWPOSCHANGING && lParam != IntPtr.Zero)
        {
            WindowPos wp = Marshal.PtrToStructure<WindowPos>(lParam);
            wp.HwndInsertAfter = HWND_BOTTOM;
            wp.Flags &= ~SWP_NOZORDER;
            Marshal.StructureToPtr(wp, lParam, false);
        }
        return IntPtr.Zero;   // never handled: Avalonia still needs every one of these
    }

    private void PushToBottom()
    {
        IntPtr h = Handle();
        if (h == IntPtr.Zero) return;
        SetWindowPos(h, HWND_BOTTOM, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    // ShowActivated=false stops Avalonia asking for activation; WS_EX_NOACTIVATE stops Windows
    // granting it when the window is clicked, which is the case that actually matters here.
    private void ApplyNoActivate()
    {
        IntPtr h = Handle();
        if (h == IntPtr.Zero) return;
        long ex = (long)GetWindowLongPtr(h, GWL_EXSTYLE);
        long want = ex | WS_EX_NOACTIVATE;
        if (want != ex) SetWindowLongPtr(h, GWL_EXSTYLE, (IntPtr)want);
    }

    private IntPtr Handle() => TryGetPlatformHandle() is { } h ? h.Handle : IntPtr.Zero;

    // ---- the canvas ------------------------------------------------------------------------------

    private sealed class CapsuleCanvas : Control
    {
        private readonly Derived _derived;
        private readonly SKTypeface _face;
        private readonly double _scale;

        private bool _pressed;
        private Point _grab;              // where in the window the pointer took hold, in DIPs
        private PixelPoint _grabPhysical; // the same offset in physical pixels
        private double _travelled;

        public string Progress { get; set; } = "";
        public float ProgressFraction { get; set; }

        public event Action? Clicked;
        public event Action<PixelPoint>? Moved;

        public CapsuleCanvas(Derived derived, double scale)
        {
            _derived = derived;
            _scale = Math.Clamp(scale, 0.85, 1.7);
            // The real face is not embedded yet; this is the platform default until it ships
            // (SearchShot renders with the same fallback for the same reason).
            _face = SKTypeface.Default;
            Focusable = false;
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _pressed = true;
            _travelled = 0;
            _grab = e.GetPosition(this);
            double s = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            _grabPhysical = new PixelPoint((int)Math.Round(_grab.X * s), (int)Math.Round(_grab.Y * s));
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (!_pressed || TopLevel.GetTopLevel(this) is not Window w) return;
            Point p = e.GetPosition(this);
            _travelled = Math.Max(_travelled, Math.Abs(p.X - _grab.X) + Math.Abs(p.Y - _grab.Y));
            if (_travelled < ClickSlop) return;

            // PointToScreen already accounts for the window's current position, so subtracting the
            // grab offset gives an absolute target rather than an accumulating error.
            PixelPoint screen = w.PointToScreen(p);
            w.Position = new PixelPoint(screen.X - _grabPhysical.X, screen.Y - _grabPhysical.Y);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            if (!_pressed) return;
            _pressed = false;
            e.Pointer.Capture(null);
            e.Handled = true;

            if (_travelled >= ClickSlop)
            {
                // Once, here, rather than on every pixel of the drag: this is a disk write.
                if (TopLevel.GetTopLevel(this) is Window w) Moved?.Invoke(w.Position);
                return;
            }
            Clicked?.Invoke();
        }

        public override void Render(DrawingContext context)
            => context.Custom(new DrawOp(new Rect(Bounds.Size), this));

        private sealed class DrawOp : ICustomDrawOperation
        {
            private readonly CapsuleCanvas _c;
            private readonly string _progress;
            private readonly float _fraction;

            public DrawOp(Rect b, CapsuleCanvas c)
            {
                Bounds = b;
                _c = c;
                _progress = c.Progress;
                _fraction = c.ProgressFraction;
            }

            public Rect Bounds { get; }
            public bool HitTest(Point p) => true;
            public bool Equals(ICustomDrawOperation? other) => false;
            public void Dispose() { }

            public void Render(ImmediateDrawingContext context)
            {
                if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
                using var lease = feature.Lease();
                SKCanvas canvas = lease.SkCanvas;
                canvas.Save();
                canvas.Scale((float)_c._scale);
                CapsulePainter.Paint(canvas, "Search files, photos, words…",
                                     _progress, _fraction, _c._derived, _c._face);
                canvas.Restore();
            }
        }
    }
}
