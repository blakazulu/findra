using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using System.Threading.Tasks;
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

    /// <summary>Raised when "Open settings" is pressed on the answered page, just before the
    /// window closes. The shell opens Settings once the screen is gone: while it is up, it is the
    /// only door into Findra and Settings would only bring it forward again.</summary>
    public event Action? SettingsRequested;

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
        SizeTo(FirstRunLayout.SurfaceHeight(state));
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
            // Sized against the primary screen before it had one of its own.
            _canvas.Refit();
            Activate();
            _canvas.Focus();
            Topmost = false;
        };
        _canvas.Answered += s => Answered?.Invoke(s);
        _canvas.StartReadingRequested += () => StartReadingRequested?.Invoke();
        _canvas.SettingsRequested += () => SettingsRequested?.Invoke();
    }

    /// <summary>
    /// Size the window to a page <paramref name="height"/> units tall, shrunk as a whole where
    /// the screen has less room than that (<see cref="ScreenFit"/>). The choosing page is 928
    /// units, taller than a 1080p screen at 125%, and this window has no title bar to drag it
    /// back up by - so without this its two buttons sit below the bottom edge and the only screen
    /// that lets anybody into Findra cannot be answered.
    /// </summary>
    private void SizeTo(float height)
    {
        double k = ScreenFit.For(this, FirstRunLayout.Width, height);
        _canvas.Fit = k;
        Width = FirstRunLayout.Width * k;
        Height = height * k;
        ScreenFit.KeepInside(this);
    }

    /// <summary>The chord the hotkey actually registered, once the answer has built it - or null
    /// where nothing would register. The answered page names it.</summary>
    public void NoteHotkey(string? chord) => _canvas.NoteHotkey(chord);

    /// <summary>What the shell is actually fetching. Everything a chosen capability needs that is
    /// NOT in this list was already on disk, so its bar starts full rather than empty.</summary>
    public void NoteFetching(IReadOnlyList<Model> models) => _canvas.NoteFetching(models);

    /// <summary>One file moved. Rolled up into one bar per capability, because seven bars over
    /// three capabilities is not a picture of what is happening.</summary>
    public void NoteProgress(DownloadProgress p) => _canvas.NoteProgress(p);

    /// <summary>The run is over. <paramref name="problem"/> is empty when everything arrived.
    /// </summary>
    public void NoteFinished(string problem) => _canvas.NoteFinished(problem);

    /// <summary>One step of the work behind "Get these" has really finished; the card ticks it.
    /// The shell yields after calling this so the tick is drawn before the next step starts.</summary>
    public void StepDone(int step) => _canvas.StepDone(step);

    /// <summary>The work is over: the card goes and the next page is shown.</summary>
    public void EndWork() => _canvas.EndWork();

    /// <summary>Give the window a frame to draw in. Background runs after rendering, so awaiting
    /// this is what makes a tick visible before the interface thread is busy again.</summary>
    public static async Task Frame() => await Dispatcher.UIThread.InvokeAsync(static () => { }, DispatcherPriority.Background);

    // ---- the canvas ------------------------------------------------------------------------------

    // Fully qualified, for the reason SettingsWindow's canvas is: the settings model's row type is
    // `Findra.Control`, and a type in this file's own namespace beats one arriving through a using
    // directive - so a bare `Control` here binds to a sealed record. CardWindow and CapsuleWindow
    // are written the same way.
    private sealed class FirstRunCanvas : Avalonia.Controls.Control
    {
        private readonly FirstRunWindow _owner;

        /// <summary>How much smaller than its layout the page is drawn, so it fits the screen:
        /// the painter's canvas is scaled by it and every pointer position divided by it.</summary>
        public double Fit { get; set; } = 1.0;
        private readonly SKTypeface _face;
        private readonly Derived _derived;

        private readonly Dictionary<string, long> _moved = new(StringComparer.OrdinalIgnoreCase);
        private IReadOnlyList<Model> _fetching = [];

        private FirstRunState _state;
        private bool _answered;

        /// <summary>The page the answer leads to, shown once the work behind it is over.</summary>
        private FirstRunStage _next = FirstRunStage.Choosing;

        /// <summary>Turns the spinner while the work card is up, and only then.</summary>
        private DispatcherTimer? _spinner;

        public event Action<FirstRunState>? Answered;
        public event Action? StartReadingRequested;
        public event Action? SettingsRequested;

        public FirstRunCanvas(FirstRunState state, Palette palette, FirstRunWindow owner)
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
        /// <summary>The pointer in the layout's own units, undoing the fit the page is drawn at.
        /// </summary>
        private Point At(PointerEventArgs e)
        {
            Point p = e.GetPosition(this);
            return new Point(p.X / Fit, p.Y / Fit);
        }

        /// <summary>Fit the page again, to the screen the window actually landed on.</summary>
        public void Refit() => _owner.SizeTo(FirstRunLayout.SurfaceHeight(_state));

        private FirstRunHit HitAt(Point p) => FirstRunLayout.HitTest((float)p.X, (float)p.Y, _state);

        /// <summary>Is the last question on the screen? Two buttons instead of one, a taller
        /// window to hold the question, and a "Start reading" that means something rather than
        /// a second way to close.</summary>
        private bool Asking => FirstRun.Asks(_state);

        /// <summary>The same question, for the shell, after the window has gone. A pure function
        /// of the final state, so it needs no flag of its own.</summary>
        public bool AskedAboutReading => Asking;

        public void NoteHotkey(string? chord)
        {
            _state = _state with { Hotkey = chord };
            InvalidateVisual();
        }

        public void NoteFetching(IReadOnlyList<Model> models)
        {
            _fetching = models;
            _state = _state with { Downloads = FirstRun.Progress(_state, _fetching, _moved) };
            // The bars are rows on the answered page, so knowing how many there are is knowing
            // how tall it is. This arrives straight after the answer, while the pointer is still
            // on the button that has just stopped existing.
            _owner.SizeTo(FirstRunLayout.SurfaceHeight(_state));
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
            // here - downwards, so everything above the question stays where it was.
            _owner.SizeTo(FirstRunLayout.SurfaceHeight(_state));
            InvalidateVisual();
        }

        private void ShowWork(FirstRunWork work)
        {
            _state = _state with { Work = work, HoverTarget = FirstRunTarget.None, HoverIndex = -1 };
            Cursor = PointerCursor.Of(PointerShape.Arrow);
            _spinner ??= new DispatcherTimer(TimeSpan.FromMilliseconds(40), DispatcherPriority.Render, (_, _) =>
            {
                if (_state.Work is null) return;
                _state = _state with { Spin = (Environment.TickCount64 % 900) / 900f };
                InvalidateVisual();
            });
            _spinner.Start();
            InvalidateVisual();
        }

        public void StepDone(int step)
        {
            if (_state.Work is not { } work) return;
            _state = _state with { Work = work.Done(step) };
            InvalidateVisual();
        }

        public void EndWork()
        {
            _spinner?.Stop();
            // The answer's page, unless the shell has already moved it on - everything chosen was
            // already on disk, so it went straight to finished while the card was still up.
            FirstRunStage stage = _state.Stage == FirstRunStage.Choosing ? _next : _state.Stage;
            _state = _state with { Work = null, Stage = stage };
            _owner.SizeTo(FirstRunLayout.SurfaceHeight(_state));
            InvalidateVisual();
        }

        /// <summary>The answered page's closing buttons: the card, what they start, then the window
        /// goes. Posted, so the card is drawn before anything runs.</summary>
        private void CloseWithWork(bool startReading)
        {
            ShowWork(FirstRunWork.Closing(startReading));
            Dispatcher.UIThread.Post(async () =>
            {
                StepDone(0);
                await FirstRunWindow.Frame();
                if (startReading)
                {
                    StartReadingRequested?.Invoke();
                    StepDone(1);
                    await FirstRunWindow.Frame();
                }
                _spinner?.Stop();
                _owner.Close();
            }, DispatcherPriority.Background);
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            if (_state.Work is not null) return;   // the card is the only thing up
            Point p = At(e);
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
            // Nothing answers while the work card is up - not the page, not the title strip.
            if (_state.Work is not null) return;
            Point p = At(e);
            FirstRunHit hit = HitAt(p);

            // The title strip is the only place a borderless window can be picked up by.
            if (hit.Target == FirstRunTarget.None && p.Y < FirstRunLayout.TileTop)
            {
                try { _owner.BeginMoveDrag(e); }
                catch (Exception ex) { Log.Warn("firstrun", "the window would not move: " + ex.Message); }
                return;
            }

            // The answered page's own controls. "Open settings" closes the page first, because
            // while it is up it is the only door into Findra; the shell opens Settings behind it.
            // Pressed over the last question it is "Later" with somewhere to go: nothing starts
            // reading, and Settings' own "Start now" is right there.
            if (hit.Target == FirstRunTarget.Settings)
            {
                SettingsRequested?.Invoke();
                _owner.Close();
                return;
            }
            if (hit.Target == FirstRunTarget.Link)
            {
                if (hit.Index >= 0 && hit.Index < Welcome.Links.Count) OpenLink(Welcome.Links[hit.Index].Url);
                return;
            }

            if (hit.Target is FirstRunTarget.NotNow or FirstRunTarget.Go)
            {
                // The answered page keeps the same window: "Done" closes it, because the answer
                // has already been given and the download is the shell's, which carries on in the
                // tray without it.
                //
                // Unless the last question is still on it, in which case the right-hand button is
                // the answer to that question and the left one declines it. Both close - the
                // difference is whether anything starts reading, which is what "Later" is for: the
                // preference from the first act is saved either way and this is only about now.
                if (_state.Stage != FirstRunStage.Choosing)
                {
                    CloseWithWork(startReading: Asking && hit.Target == FirstRunTarget.Go);
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

                // The page stays where it is, under a card that says what is happening, until the
                // shell has done the work behind the answer (EndWork). The next page and the one
                // resize this window does happen then: the second act draws no tiles, switches or
                // notes, and the choosing height would leave a large empty band under it.
                _next = answer.Chosen.Count > 0 ? FirstRunStage.Downloading : FirstRunStage.Finished;
                _state = answer;
                ShowWork(FirstRunWork.SettingUp(downloading: answer.Chosen.Count > 0));

                // Posted, so the card is drawn before the shell's first step takes the thread. No
                // early close when nothing was chosen: the answered page is where somebody who took
                // "Just names" learns where Findra went, which they need as much as anyone.
                Dispatcher.UIThread.Post(() => Answered?.Invoke(answer), DispatcherPriority.Background);
                return;
            }

            if (_state.Stage != FirstRunStage.Choosing) return;   // the list is settled

            FirstRunState before = _state;
            _state = FirstRun.Apply(_state, hit);
            if (ReferenceEquals(before, _state)) return;
            InvalidateVisual();
        }

        /// <summary>In the browser, which is the only thing that fetches it. A browser that will
        /// not start is a log line: the page has nothing else to offer for it.</summary>
        private static void OpenLink(string url)
        {
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
            catch (Exception ex) { Log.Warn("firstrun", $"could not open {url}: {ex.Message}"); }
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
                canvas.Scale((float)_c.Fit);
                FirstRunPainter.Paint(canvas, _c._state, _c._derived, _c._face);
                canvas.Restore();
            }
        }
    }
}
