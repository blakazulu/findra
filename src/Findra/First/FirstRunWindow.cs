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

    /// <summary>Raised when the last question is answered with "Start reading". Nothing reads
    /// until it is: <c>FirstRun.Asks</c> says why, and the shell holds the indexer until this
    /// arrives or the window closes without it.</summary>
    public event Action? StartReadingRequested;

    /// <summary>Whether the last question ever reached the screen. Read by the shell when the
    /// window closes, because the hold on reading has three endings and only two of them raise an
    /// event: "Start reading" clears it, "Later" deliberately keeps it for the session, and a
    /// screen that never asked at all has nobody to clear it. Without this the third case leaves a
    /// hold nothing can release, and every switch that turns reading on reads as on and reads
    /// nothing.</summary>
    public bool AskedAboutReading => _canvas.AskedAboutReading;

    public FirstRunWindow(FirstRunState state, Palette palette)
    {
        ArgumentNullException.ThrowIfNull(state);

        // This window is in the taskbar, and Avalonia does not hand it the executable's
        // icon, so without this it shows the shell's placeholder.
        AppIcon.Apply(this);

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
        // Pinned to ARRIVE, and only to arrive. Windows will not reliably let a process that is
        // still starting take the foreground, so a window that merely called Activate() can open
        // behind whatever was already on the desktop - which on the first screen of a fresh
        // install reads as an installer that did nothing.
        //
        // It is released in Opened, below, the moment it is actually on the display. Keeping it
        // set was the first attempt and it was wrong in a way only a real install showed: this is
        // a screen somebody reads, thinks about, and then leaves running while 2.9 GB arrives, and
        // for all of that it sat over every other window on the machine with no way to put it
        // behind anything. What makes it the only door into Findra is the gate in App - the
        // hotkey, the tray, the capsule and settings all raise this window instead of opening -
        // and not a flag that outranks the whole desktop.
        Topmost = true;

        Opened += (_, _) =>
        {
            Activate();
            _canvas.Focus();
            Topmost = false;
        };
        _canvas.Answered += s => Answered?.Invoke(s);
        _canvas.StartReadingRequested += () => StartReadingRequested?.Invoke();
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
        public event Action? StartReadingRequested;

        public FirstRunCanvas(FirstRunState state, Palette palette, Window owner)
        {
            _state = state;
            _owner = owner;
            _face = Parts.Face;
            _derived = Derived.From(palette);
            Focusable = true;
        }

        /// <summary>
        /// Where the pointer is, on the screen as it is RIGHT NOW - read per event rather than
        /// stored, because ticking Speech puts a control on the screen and a hit test working
        /// from a stale answer aims at the row the pills have just pushed down.
        ///
        /// <para>Every bound comes off the state, and that is the whole point of it. Handing the
        /// five arguments in by hand was five chances to measure a screen that is not the one on
        /// the display, and one of them was already wrong: the transcription band's row went in as
        /// <c>FirstRun.LimitRow</c>, which names Speech's row in every act, while the window's
        /// height and the painter both read <c>FirstRunLayout.BandRow</c>, which drops it the
        /// moment the screen is answered. On a machine offered Hebrew, anybody who took Speech got
        /// a last question whose two buttons were tested for 64 px below the bottom edge of the
        /// window they were painted in - so "Start reading" could not be hovered, did not change
        /// the cursor and could not be pressed.</para>
        /// </summary>
        private FirstRunHit HitAt(Point p) => FirstRunLayout.HitTest((float)p.X, (float)p.Y, _state);

        /// <summary>Is the last question on the screen? Two buttons instead of one, a taller
        /// window to hold the question, and a "Start reading" that means something rather than
        /// a second way to close.</summary>
        private bool Asking => FirstRun.Asks(_state);

        /// <summary>The same question, for the shell, after the window has gone. A pure function
        /// of the final state, so it needs no flag of its own.</summary>
        public bool AskedAboutReading => Asking;

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
            // The last question needs room that the download screen did not, so the window grows
            // here. Safe for the same reason the shrink was: no button is drawn while a download
            // runs, so there is nothing under the pointer for a resize to move out from under.
            _owner.Height = FirstRunLayout.SurfaceHeight(_state);
            InvalidateVisual();
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            Point p = e.GetPosition(this);
            FirstRunHit hit = HitAt(p);
            if (hit.Target == _state.HoverTarget && hit.Index == _state.HoverIndex) return;
            _state = _state with { HoverTarget = hit.Target, HoverIndex = hit.Index };
            // The free "Words in documents" row is not a choice - Apply returns the state
            // unchanged for it - and the painter already knows, suppressing its hover fill. The
            // cursor did not, so the one row on the screen that cannot be ticked was the row the
            // pointer offered to tick. The fill and the shape now read the same rule.
            Cursor = PointerCursor.Of(FirstRun.Apply(_state, hit) == _state
                ? PointerShape.Arrow : Pointers.ForFirstRun(hit.Target));
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
            FirstRunHit hit = HitAt(p);

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
                //
                // Unless the last question is still on it, in which case the right-hand button is
                // the answer to that question and the left one declines it. Both close - the
                // difference is whether anything starts reading, which is what "Later" is for: the
                // preference from the first act is saved either way and this is only about now.
                if (_state.Stage != FirstRunStage.Choosing)
                {
                    if (Asking && hit.Target == FirstRunTarget.Go) StartReadingRequested?.Invoke();
                    _owner.Close();
                    return;
                }

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
                // The one resize this window does. The choosing height is sized for its tallest
                // configuration so that ticking Speech cannot move the window under the pointer
                // that ticked it - but the second act draws no tiles, no switches, no limit row
                // and no notes, and the fixed height left a large empty band under the summary.
                // Here is the safe moment: a deliberate click on a button that then stops
                // existing, with nothing left under the pointer to be hit.
                _owner.Height = FirstRunLayout.SurfaceHeight(_state);
                InvalidateVisual();

                Answered?.Invoke(answer);
                // Nothing to wait for when nothing was chosen, so the screen goes rather than
                // sitting there saying "0 of 0 done" - unless it has a question left to ask, which
                // is the case for anybody who took no models and still turned reading on. Closing
                // there would ask nothing and start nothing, which is the one outcome that loses
                // the answer.
                if (answer.Chosen.Count == 0 && !FirstRun.Asks(_state)) _owner.Close();
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
