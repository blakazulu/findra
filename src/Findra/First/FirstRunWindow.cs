using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace Findra;

/// <summary>
/// Spec §6's first screen, in a window, on the same painted card the shot command renders.
///
/// <para>Two things it does that <see cref="SettingsWindow"/> does not, and both follow from what
/// it is for. It is <b>modal in effect but not in code</b>: it opens before the capsule, the tray
/// and the content loop, and the shell waits for <see cref="Answered"/> rather than blocking a
/// thread. And it <b>cannot be dismissed by accident</b> - there is no close cross and Escape does
/// nothing, because the two buttons are the answer and a window that vanished without one would
/// leave <c>FirstRunDone</c> unwritten and come back at the next launch.</para>
///
/// <para>The download is not started here. <see cref="Answered"/> hands the shell the state it was
/// answered with; the shell owns the models directory, the http client and the flow that owns the
/// index's writer connection, and this window only shows what it is told through
/// <see cref="NoteProgress"/> and <see cref="NoteFinished"/>.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class FirstRunWindow : Window
{
    private readonly FirstRunCanvas _canvas;

    /// <summary>Raised once, when "Not now" or "Get these" is pressed. The state carries what was
    /// chosen; <c>FirstRun.Outcome</c> turns it into a configuration.</summary>
    public event Action<FirstRunState>? Answered;

    public FirstRunWindow(FirstRunState state, Palette palette)
    {
        ArgumentNullException.ThrowIfNull(state);

        _canvas = new FirstRunCanvas(state, palette, this);
        Content = _canvas;

        Title = "Welcome to Findra";
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        CanResize = false;
        SizeToContent = SizeToContent.Manual;
        Width = FirstRunLayout.Width;
        Height = FirstRunLayout.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // Topmost, unlike settings: this is the first thing a new install shows and it is the
        // only route into content indexing and the capability list. A screen that opened behind
        // whatever was already on the desktop would look like nothing happened.
        Topmost = true;

        Opened += (_, _) => { Activate(); _canvas.Focus(); };
        _canvas.Answered += s => Answered?.Invoke(s);
    }

    /// <summary>What the shell is actually fetching. Everything a chosen capability needs that is
    /// NOT in this list was already on disk, so its bar starts full rather than empty.</summary>
    public void NoteFetching(IReadOnlyList<Model> models) => _canvas.NoteFetching(models);

    /// <summary>One file moved. Rolled up into one bar per capability, because seven bars over
    /// three capabilities is not a picture of what is happening.</summary>
    public void NoteProgress(DownloadProgress p) => _canvas.NoteProgress(p);

    /// <summary>The run is over. <paramref name="problem"/> is empty when everything arrived.
    /// </summary>
    public void NoteFinished(string problem) => _canvas.NoteFinished(problem);

    // ---- the canvas ------------------------------------------------------------------------------

    // Fully qualified, for the reason SettingsWindow's canvas is: the settings model's row type is
    // `Findra.Control`, and a type in this file's own namespace beats one arriving through a using
    // directive - so a bare `Control` here binds to a sealed record. CardWindow and CapsuleWindow
    // are written the same way.
    private sealed class FirstRunCanvas : Avalonia.Controls.Control
    {
        private readonly Window _owner;
        private readonly SKTypeface _face;
        private readonly Derived _derived;

        private readonly Dictionary<string, long> _moved = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<Model> _fetching = [];

        private FirstRunState _state;
        private bool _answered;

        public event Action<FirstRunState>? Answered;

        public FirstRunCanvas(FirstRunState state, Palette palette, Window owner)
        {
            _state = state;
            _owner = owner;
            _face = Parts.Face;
            _derived = Derived.From(palette);
            Focusable = true;
        }

        private int Rows => FirstRun.Rows(_state).Count;

        /// <summary>Which row the transcription limit sits under, or -1 where Speech is not
        /// taken. Read per event rather than stored, for the reason the row count is: ticking
        /// Speech puts a control on the screen, and a hit test working from a stale answer would
        /// aim at the row the pills have just pushed down.</summary>
        private int LimitRow => FirstRun.LimitRow(_state);

        /// <summary>Has the screen been answered? Read per event for the same reason the row
        /// count is, and handed to the hit test so that the tiles, the rows, the limit pills and
        /// the switches stop answering the moment the selection leaves for the shell. The stage
        /// check further down was a partial version of this: it stopped a click CHANGING the
        /// selection but left every control lit, hovering and offering a hand cursor.</summary>
        private bool Settled => _state.Stage != FirstRunStage.Choosing;

        /// <summary>The download is over, so a button may appear. While it runs the screen answers
        /// nothing at all and the way out is the window's own close, which is never disabled.</summary>
        private bool Finished => _state.Stage == FirstRunStage.Finished;

        public void NoteFetching(IReadOnlyList<Model> models)
        {
            _fetching = models;
            _state = _state with { Downloads = FirstRun.Progress(_state, _fetching, _moved) };
            InvalidateVisual();
        }

        public void NoteProgress(DownloadProgress p)
        {
            // Per FILE here, per CAPABILITY on screen. Photos is three files, and a bar that took
            // only the newest report would jump back to nearly empty every time one finished and
            // the next one started.
            _moved[p.File] = p.Got;
            _state = _state with { Downloads = FirstRun.Progress(_state, _fetching, _moved) };
            InvalidateVisual();
        }

        public void NoteFinished(string problem)
        {
            _state = _state with { Problem = problem, Stage = FirstRunStage.Finished };
            InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            Point p = e.GetPosition(this);
            FirstRunHit hit = FirstRunLayout.HitTest((float)p.X, (float)p.Y, Rows, LimitRow, Settled, Finished);
            if (hit.Target == _state.HoverTarget && hit.Index == _state.HoverIndex) return;
            _state = _state with { HoverTarget = hit.Target, HoverIndex = hit.Index };
            Cursor = PointerCursor.Of(Pointers.ForFirstRun(hit.Target));
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            _state = _state with { HoverTarget = FirstRunTarget.None, HoverIndex = -1 };
            Cursor = PointerCursor.Of(Pointers.ForFirstRun(FirstRunTarget.None));
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            Focus();
            Point p = e.GetPosition(this);
            FirstRunHit hit = FirstRunLayout.HitTest((float)p.X, (float)p.Y, Rows, LimitRow, Settled, Finished);

            // The title strip is the only place a borderless window can be picked up by.
            if (hit.Target == FirstRunTarget.None && p.Y < FirstRunLayout.TileTop)
            {
                try { _owner.BeginMoveDrag(e); }
                catch (Exception ex) { Log.Warn("firstrun", "the window would not move: " + ex.Message); }
                return;
            }

            if (hit.Target is FirstRunTarget.NotNow or FirstRunTarget.Go)
            {
                // The second act keeps the same window: "Close" and "Done" both just close it,
                // because the answer has already been given and the download is the shell's.
                if (_state.Stage != FirstRunStage.Choosing) { _owner.Close(); return; }

                // Once. A second press while the shell is still starting the download would run
                // the whole hand-off twice - two registrations, two download runs over one
                // directory, two gates.
                if (_answered) return;
                _answered = true;

                // "Not now" is a complete answer, so what it says is "take nothing", not "ask me
                // later" - FirstRun.Outcome writes FirstRunDone either way, and content indexing
                // is off by default so the empty selection is the safe one.
                FirstRunState answer = hit.Target == FirstRunTarget.NotNow
                    ? _state with { Chosen = new HashSet<Capability>(), ContentOn = false }
                    : _state;

                _state = answer with
                {
                    Stage = answer.Chosen.Count > 0 ? FirstRunStage.Downloading : FirstRunStage.Finished,
                };
                InvalidateVisual();

                Answered?.Invoke(answer);
                // Nothing to wait for when nothing was chosen, so the screen goes rather than
                // sitting there saying "0 of 0 done".
                if (answer.Chosen.Count == 0) _owner.Close();
                return;
            }

            if (_state.Stage != FirstRunStage.Choosing) return;   // the list is settled

            FirstRunState before = _state;
            _state = FirstRun.Apply(_state, hit);
            if (ReferenceEquals(before, _state)) return;
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
            => context.Custom(new DrawOp(new Rect(Bounds.Size), this));

        private sealed class DrawOp : ICustomDrawOperation
        {
            private readonly FirstRunCanvas _c;
            public DrawOp(Rect b, FirstRunCanvas c) { Bounds = b; _c = c; }
            public Rect Bounds { get; }
            public bool HitTest(Point p) => true;
            public bool Equals(ICustomDrawOperation? other) => false;
            public void Dispose() { }

            public void Render(ImmediateDrawingContext context)
            {
                if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
                using ISkiaSharpApiLease lease = feature.Lease();
                SKCanvas canvas = lease.SkCanvas;
                canvas.Save();
                FirstRunPainter.Paint(canvas, _c._state, _c._derived, _c._face);
                canvas.Restore();
            }
        }
    }
}
