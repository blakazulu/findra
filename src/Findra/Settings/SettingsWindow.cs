using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Findra.Startup;   // HelperTaskState
using SkiaSharp;

namespace Findra;

/// <summary>
/// The settings window: the same painted card the shot command renders, in a window, with the
/// pointer and the keyboard wired to it.
///
/// <para>Five things it does that <see cref="CardWindow"/> does not, and each is a decision:</para>
/// <list type="number">
/// <item><description>It is not <c>Topmost</c> and it does NOT close on deactivation. The card is a
/// transient thing the user is mid-thought in; settings is a window they will click away from to
/// look at a folder in Explorer and come back to.</description></item>
/// <item><description>One instance, <see cref="Open"/>. A second settings window is a second view
/// of the same config, and the two write over each other on every click.</description></item>
/// <item><description>The palette can change while it is open, from inside itself
/// (<see cref="UsePalette"/>): the derivation is rebuilt and the canvas invalidated rather than the
/// window recreated, so a swatch click does not flash.</description></item>
/// <item><description>It holds the resolved palette LIST for the painter's swatches, so no repaint
/// touches <c>palettes.json</c>.</description></item>
/// <item><description>It captures keys while <c>_state.Capturing</c>, and only then.</description></item>
/// </list>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SettingsWindow : Window
{
    /// <summary>The one open settings window, or null. The shell reuses it rather than opening a
    /// second view of the same configuration.</summary>
    public static SettingsWindow? Open { get; private set; }

    private readonly SettingsCanvas _canvas;

    /// <summary>Raised whenever a click moved a setting - after the new configuration has already
    /// been saved. The shell re-resolves the palette, redraws the tray icon and follows the
    /// capsule switch off it.</summary>
    public event Action<Config>? Changed;

    /// <summary>Raised when the window has taken a new palette, which is the shell's cue to redraw
    /// everything that is NOT this window.</summary>
    public event Action<Palette>? PaletteChanged;

    public SettingsWindow(SettingsState state, ISettingsHost host, Func<string, bool> registerHotkey)
    {
        ArgumentNullException.ThrowIfNull(state);

        // This window is in the taskbar, and Avalonia does not hand it the executable's
        // icon, so without this it shows the shell's placeholder.
        AppIcon.Apply(this);

        _canvas = new SettingsCanvas(state, host, registerHotkey, this);
        Content = _canvas;

        Title = "Findra settings";
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        CanResize = false;
        SizeToContent = SizeToContent.Manual;
        Width = RailLayout.Width;
        Height = RailLayout.Height;
        // Topmost is deliberately NOT set, and there is no Deactivated handler: this window is
        // meant to survive a trip to Explorer and back.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        KeyDown += (_, e) => { if (_canvas.OnKey(e)) e.Handled = true; };
        Opened += (_, _) => { Open = this; Activate(); _canvas.Focus(); };
        Closed += (_, _) => { if (ReferenceEquals(Open, this)) Open = null; };

        _canvas.Changed += c => Changed?.Invoke(c);
    }

    /// <summary>Paint in a different palette without recreating the window.</summary>
    public void UsePalette(Palette p)
    {
        ArgumentNullException.ThrowIfNull(p);
        _canvas.UsePalette(p);
        PaletteChanged?.Invoke(p);
    }

    /// <summary>What the scheduled-task query found, handed in rather than asked for here:
    /// <c>HelperTask.Query</c> shells out to <c>schtasks</c> and waits up to five seconds, which is
    /// five seconds of a frozen window if it runs on this thread.</summary>
    public void UseHelperState(HelperTaskState helper) => _canvas.Refresh(s => s with { Helper = helper });

    /// <summary>What is on disk now, after a download finished. The rows for a capability that has
    /// arrived stop being buttons and start reading "installed".</summary>
    public void UseInstalled(CapabilitySet installed) => _canvas.Refresh(s => s with { Installed = installed });

    /// <summary>What the update check came back with. Without this, "Check now" is a button that
    /// makes a request and changes nothing anybody can see.</summary>
    public void NoteUpdate(UpdateState update, string? latest) =>
        _canvas.Refresh(s => s with { Update = update, Latest = latest });

    /// <summary>Go to a section, on the same terms a click on the rail does. The card can ask for
    /// settings at a particular section - the Content pill sends somebody here when there is
    /// nothing indexed to search - and a window already open has to follow rather than stay where
    /// it was last left.</summary>
    public void ShowSection(Section section) => _canvas.Refresh(s => SettingsModel.GoTo(s, section));

    /// <summary>A folder the picker returned. It is a configuration change like any other, so it
    /// saves and announces itself down the same path a click does.</summary>
    public void AddExclusion(string path) => _canvas.Refresh(s => SettingsModel.AddExclusion(s, path));

    /// <summary>A configuration changed from somewhere else - the capsule's right-click menu, or
    /// the tray's capsule tick. It is already saved and already acted on, so this only makes the
    /// window show it; announcing it again would be the shell hearing its own echo, and NOT
    /// showing it leaves a stale view that overwrites the change on its owner's next click.
    /// </summary>
    public void UseConfig(Config c) => _canvas.ShowConfig(c);

    // ---- the canvas ------------------------------------------------------------------------------

    // Fully qualified, because the settings model's row type is `Findra.Control` and a type in
    // this file's own namespace beats one arriving through a using directive - so a bare `Control`
    // here binds to a sealed record. A using alias cannot fix it: inside the namespace it collides
    // with the member, and outside it loses to the member. `CardWindow` and `CapsuleWindow` are
    // written the same way for the same reason.
    private sealed class SettingsCanvas : Avalonia.Controls.Control
    {
        private readonly ISettingsHost _host;
        private readonly Func<string, bool> _registerHotkey;
        private readonly Window _owner;
        private readonly SKTypeface _face;

        private SettingsState _state;
        private Derived _derived;

        public event Action<Config>? Changed;

        public SettingsCanvas(SettingsState state, ISettingsHost host,
                              Func<string, bool> registerHotkey, Window owner)
        {
            _state = state;
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _registerHotkey = registerHotkey ?? throw new ArgumentNullException(nameof(registerHotkey));
            _owner = owner;
            // The shipped face, not the platform's - one resolver for every surface, so the window
            // and the shot of the window are the same picture.
            _face = Parts.Face;
            _derived = Derived.From(Palette.ByName(PaintedPaletteName(state)) ?? Palette.DefaultDark);
            Focusable = true;
        }

        /// <summary>Which palette this window is painted in, which is the side
        /// <see cref="Theme.Resolve"/> would pick right now - not <c>DarkPalette</c>. Opening the
        /// settings window on a light desktop in the dark palette would be the same defect the
        /// capsule menu's tick has.</summary>
        private static string PaintedPaletteName(SettingsState s) =>
            s.Config.Mode switch
            {
                ThemeMode.AlwaysDark => s.Config.DarkPalette,
                ThemeMode.AlwaysLight => s.Config.LightPalette,
                // Fully qualified: inside a Control subclass a bare `Theme` binds to the inherited
                // StyledElement.Theme property, the same shadowing that makes the canvas derive
                // from `Avalonia.Controls.Control` by its full name.
                _ => Findra.Theme.WindowsIsLight() ? s.Config.LightPalette : s.Config.DarkPalette,
            };

        public void UsePalette(Palette p)
        {
            _derived = Derived.From(p);
            InvalidateVisual();
        }

        /// <summary>
        /// Move the state, and take the same two steps a click takes: save and announce when the
        /// configuration actually moved, then repaint. Everything the shell hands back in - the
        /// helper's state, what finished downloading, what the update check found, a folder the
        /// picker returned - comes through here, so none of them can forget to save.
        /// </summary>
        public void Refresh(Func<SettingsState, SettingsState> change)
        {
            SettingsState before = _state;
            _state = change(_state);
            Announce(before);
            InvalidateVisual();
        }

        /// <summary>Show a configuration this window did not make, without saving or announcing
        /// it: whoever made it has already done both.</summary>
        public void ShowConfig(Config c)
        {
            if (_state.Config == c) return;
            _state = _state with { Config = c };
            InvalidateVisual();
        }

        private void Announce(SettingsState before)
        {
            // Saved only when a setting actually moved - a click on a section is not a settings
            // change and must not rewrite config.json.
            if (before.Config == _state.Config) return;
            _state.Config.Save();
            Changed?.Invoke(_state.Config);
        }

        // ---- pointer ----

        private PanelHit HitAt(Point p) => RailLayout.HitTest(
            (float)p.X, (float)p.Y,
            SettingsModel.OptionCounts(_state), SettingsModel.NoteLines(_state, _face),
            SettingsModel.ListRows(_state));

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            PanelHit hit = HitAt(e.GetPosition(this));
            if (hit.Target == _state.HoverTarget && hit.Row == _state.HoverRow && hit.Option == _state.HoverOption)
                return;
            _state = _state with { HoverTarget = hit.Target, HoverRow = hit.Row, HoverOption = hit.Option };
            Cursor = PointerCursor.Of(Pointers.ForPanel(hit.Target));
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            _state = _state with { HoverTarget = PanelTarget.None, HoverRow = -1, HoverOption = -1 };
            Cursor = PointerCursor.Of(Pointers.ForPanel(PanelTarget.None));
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            Focus();
            Point p = e.GetPosition(this);
            PanelHit hit = HitAt(p);

            if (hit.Target == PanelTarget.Close) { _owner.Close(); return; }

            // The title strip is the only place a borderless window can be picked up by. Without
            // it the window cannot be moved at all, which for a window somebody is meant to leave
            // open beside Explorer is not a small thing.
            if (hit.Target == PanelTarget.None && p.Y < RailLayout.TitleH)
            {
                try { _owner.BeginMoveDrag(e); }
                catch (Exception ex) { Log.Warn("settings", "the window would not move: " + ex.Message); }
                return;
            }

            SettingsState before = _state;
            SettingsOutcome outcome = SettingsModel.Apply(_state, hit);
            _state = outcome.State;

            Announce(before);

            // And the other half of the answer. One line, one place, every action.
            //
            // AFTER the block above, and that ordering is load-bearing rather than incidental.
            // Several actions read the configuration back out of the host - a capability install
            // asks what is already selected, a recentre reads whether the capsule is shown at all.
            // Dispatching first would run them against the configuration as it was before the
            // click, so the one setting the person just moved is the one the action cannot see.
            // Save, announce, then act.
            SettingsActions.Dispatch(outcome.Action, outcome.Argument, _host);

            InvalidateVisual();
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            // The exclusions list is the only scroller in either surface (spec §7).
            if (_state.Section != Section.Searches) return;
            int rows = _state.Config.SearchExclusions.Length;
            int max = Math.Max(0, rows - RailLayout.ListRowsThatFit);
            int next = Math.Clamp(_state.ExclusionScroll - Math.Sign(e.Delta.Y) * 2, 0, max);
            if (next == _state.ExclusionScroll) return;
            _state = _state with { ExclusionScroll = next };
            InvalidateVisual();
            e.Handled = true;
        }

        // ---- keys ----

        /// <summary>True when this window consumed the key. It only ever consumes one while a
        /// chord is being captured, plus Escape, which is the way out of both.</summary>
        public bool OnKey(KeyEventArgs e)
        {
            if (!_state.Capturing)
            {
                if (e.Key != Key.Escape) return false;
                _owner.Close();
                return true;
            }

            // Escape gets out. A capture mode with no way out reads every key the person presses
            // as a hotkey, including the ones they are pressing to get out of it.
            if (e.Key == Key.Escape)
            {
                _state = _state with { Capturing = false };
                InvalidateVisual();
                return true;
            }

            // A key Findra has no name for is not a chord and not an error either: stay in capture
            // rather than saving a combination that would never register.
            if (Hotkey.VirtualKeyOf(e.Key) is not { } vk) return true;

            string? chord = SettingsModel.ChordFrom(
                e.KeyModifiers.HasFlag(KeyModifiers.Control),
                e.KeyModifiers.HasFlag(KeyModifiers.Alt),
                e.KeyModifiers.HasFlag(KeyModifiers.Shift),
                e.KeyModifiers.HasFlag(KeyModifiers.Meta),
                vk);

            // null means "not a chord yet" - a bare modifier, or a key with none. Stay in capture.
            if (chord is null) return true;

            SettingsState before = _state;
            _state = SettingsModel.Rebind(_state, chord, _registerHotkey);
            Announce(before);
            InvalidateVisual();
            return true;
        }

        // ---- paint ----

        public override void Render(DrawingContext context)
            => context.Custom(new DrawOp(new Rect(Bounds.Size), this));

        private sealed class DrawOp : ICustomDrawOperation
        {
            private readonly SettingsCanvas _c;
            public DrawOp(Rect b, SettingsCanvas c) { Bounds = b; _c = c; }
            public Rect Bounds { get; }
            public bool HitTest(Point p) => true;
            public bool Equals(ICustomDrawOperation? other) => false;
            public void Dispose() { }

            public void Render(ImmediateDrawingContext context)
            {
                if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
                using ISkiaSharpApiLease lease = feature.Lease();
                // Saved and restored around a canvas that belongs to Avalonia, exactly as the card
                // does. RailLayout is in device-independent pixels and the leased canvas already
                // carries the monitor's scaling, so nothing is scaled here.
                SKCanvas canvas = lease.SkCanvas;
                canvas.Save();
                SettingsPainter.Paint(canvas, _c._state, _c._derived, _c._face);
                canvas.Restore();
            }
        }
    }
}
