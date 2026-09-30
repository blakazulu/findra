using System;
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
/// The update window: the painted panel <see cref="UpdatePainter"/> draws, in a borderless window
/// of exactly its size, with the pointer and the keyboard wired to an <see cref="UpdateSession"/>.
/// One at a time (<see cref="Open"/>). Esc closes it, and so does Alt+F4, except while a hand-off
/// is in flight: the installer or winget is already running and cannot be called back.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class UpdateWindow : Window
{
    public static UpdateWindow? Open { get; private set; }

    private readonly UpdateCanvas _canvas;
    private readonly UpdateSession _session;

    public UpdateWindow(UpdateSession session, Palette palette)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        ArgumentNullException.ThrowIfNull(palette);
        AppIcon.Apply(this);
        _canvas = new UpdateCanvas(session, Derived.From(palette), this);
        Content = _canvas;

        Title = "Findra update";
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        CanResize = false;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowView(session.View);

        KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || !UpdateFlow.Closable(_session.View)) return;
            e.Handled = true;
            Close();
        };
        // Tab between the buttons, Enter or Space to press one. Tunnelling, so Tab is not taken
        // by Avalonia's own focus navigation first.
        AddHandler(KeyDownEvent, (_, e) => { if (_canvas.OnKey(e)) e.Handled = true; },
            Avalonia.Interactivity.RoutingStrategies.Tunnel);
        // Only a person's close is refused while a hand-off runs: Alt+F4, not Findra quitting and
        // not Windows shutting down, which a refused close would hold up.
        Closing += (_, e) =>
        {
            bool personClosing = e.CloseReason == WindowCloseReason.WindowClosing && !e.IsProgrammatic;
            if (UpdateFlow.RefusesClose(_session.View, personClosing)) e.Cancel = true;
        };
        Opened += (_, _) => { Open = this; Fit(); Activate(); _canvas.Focus(); };
        Closed += (_, _) => { if (ReferenceEquals(Open, this)) Open = null; _session.Closed(); };
    }

    /// <summary>Paint a new view and take its height: the window grows for the download bar and
    /// shrinks when the buttons go.</summary>
    public void ShowView(UpdateView v)
    {
        // A new step is said out loud; a download's progress is not, or it would talk over itself.
        if (_canvas.View.Step != v.Step) _canvas.Say(UpdateAccess.Announcement(v));
        _canvas.View = v;
        Fit();
        _canvas.InvalidateVisual();
    }

    /// <summary>Take the view's size - only when it changed, so a window somebody dragged partly
    /// off the screen is not pulled back at every step of a download.</summary>
    private void Fit()
    {
        SKRect surface = UpdatePainter.Surface(_canvas.View, Parts.Face);
        double k = ScreenFit.For(this, surface.Width, surface.Height);
        double w = surface.Width * k, h = surface.Height * k;
        _canvas.Fit = k;
        if (w == Width && h == Height) return;
        Width = w;
        Height = h;
        if (IsVisible) ScreenFit.KeepInside(this);
    }

    // Fully qualified: a bare `Control` in this namespace binds to the settings model's record.
    private sealed class UpdateCanvas : Avalonia.Controls.Control, IAccessibleSurface
    {
        private readonly UpdateSession _session;
        private readonly Derived _derived;
        private readonly Window _owner;
        private UpdatePromptTarget _hover = UpdatePromptTarget.None;

        public UpdateView View { get; set; }
        public double Fit { get; set; } = 1.0;

        public UpdateCanvas(UpdateSession session, Derived derived, Window owner)
        {
            _session = session;
            _derived = derived;
            _owner = owner;
            View = session.View;
            Focusable = true;
        }

        private Point At(PointerEventArgs e)
        {
            Point p = e.GetPosition(this);
            return new Point(p.X / Fit, p.Y / Fit);
        }

        private UpdatePromptTarget HitAt(Point p) =>
            UpdatePrompt.HitTest((float)p.X, (float)p.Y, UpdatePainter.Surface(View, Parts.Face), UpdatePrompt.Buttons(View));

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            UpdatePromptTarget over = HitAt(At(e));
            if (over == _hover) return;
            _hover = over;
            Cursor = PointerCursor.Of(Pointers.ForPrompt(over));
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            _hover = UpdatePromptTarget.None;
            Cursor = PointerCursor.Of(Pointers.ForPrompt(UpdatePromptTarget.None));
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            Focus();
            _ring = false;
            Point p = At(e);
            switch (UpdatePrompt.Press((float)p.X, (float)p.Y, UpdatePainter.Surface(View, Parts.Face),
                                       UpdatePrompt.Buttons(View), e.ClickCount))
            {
                case UpdatePromptTarget.Close: _session.PressClose(); return;
                case UpdatePromptTarget.Go: _session.PressGo(); return;
                case UpdatePromptTarget.None:
                    // A borderless window is picked up by its body - by a single press, not the
                    // second of a double-click, which UpdatePrompt.Press has already set aside.
                    if (e.ClickCount > 1) return;
                    try { _owner.BeginMoveDrag(e); }
                    catch (Exception ex) { Log.Warn("update", "the window would not move: " + ex.Message); }
                    return;
                default: throw new ArgumentOutOfRangeException(nameof(e), "no press for this target");
            }
        }

        // ---- what a screen reader and the keyboard see ----

        private string? _focusKey;
        private bool _ring;
        private AccessiblePeer? _peer;

        public string AccessName => UpdateAccess.Name;
        public IReadOnlyList<AccessNode> AccessNodes() => UpdateAccess.Nodes(View, Parts.Face);
        public double AccessScale => Fit;
        public string? FocusedKey => _focusKey;

        public void AccessInvoke(AccessNode node)
        {
            switch (node.Key)
            {
                case "close": _session.PressClose(); return;
                case "go": _session.PressGo(); return;
            }
        }

        public void Say(string text) => _peer?.Say(text);

        public bool OnKey(KeyEventArgs e)
        {
            if (e.Key == Key.Tab)
            {
                IReadOnlyList<AccessNode> nodes = AccessNodes();
                _focusKey = AccessFocus.Next(nodes, _focusKey, back: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                _ring = _focusKey is not null;
                if (AccessFocus.Find(nodes, _focusKey) is { } node) _peer?.Say(node.Spoken);
                InvalidateVisual();
                return true;
            }
            if (e.Key is Key.Enter or Key.Space && AccessFocus.Find(AccessNodes(), _focusKey) is { } focused)
            {
                AccessInvoke(focused);
                return true;
            }
            return false;
        }

        protected override Avalonia.Automation.Peers.AutomationPeer OnCreateAutomationPeer() =>
            _peer = new AccessiblePeer(this, this);

        public override void Render(DrawingContext context)
        {
            _peer?.Refresh();
            context.Custom(new DrawOp(new Rect(Bounds.Size), this));
        }

        private sealed class DrawOp(Rect bounds, UpdateCanvas c) : ICustomDrawOperation
        {
            public Rect Bounds { get; } = bounds;
            public bool HitTest(Point p) => true;
            public bool Equals(ICustomDrawOperation? other) => false;
            public void Dispose() { }

            public void Render(ImmediateDrawingContext context)
            {
                if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
                using ISkiaSharpApiLease lease = feature.Lease();
                SKCanvas canvas = lease.SkCanvas;
                canvas.Save();
                canvas.Scale((float)c.Fit);
                UpdatePainter.Paint(canvas, c.View, c._hover, c._derived, Parts.Face);
                if (c._ring && AccessFocus.Find(c.AccessNodes(), c._focusKey) is { } focused)
                    AccessRing.Draw(canvas, focused.Bounds, c._derived);
                canvas.Restore();
            }
        }
    }
}
