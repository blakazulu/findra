using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using Findra.Pipe;
using SkiaSharp;

namespace Findra;

// The window behind the search card. Opens OVER the widget so the card's capsule lands where the
// widget's capsule was, takes the keyboard, and closes on Esc (once the query is empty) or when it
// loses focus. It carries a text field and a drag source, on top of the shape an ordinary
// borderless popup window takes.
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class CardWindow : Window
{
    private static long _closedAt;

    /// <summary>A click on the widget deactivates this card BEFORE reaching the widget's handler, so
    /// a naive toggle would close and instantly reopen. A card that just closed is being dismissed.</summary>
    public static bool JustClosed =>
        _closedAt != 0 && Stopwatch.GetElapsedTime(_closedAt).TotalMilliseconds < 350;

    private readonly CardCanvas _canvas;
    private DimWindow? _dim;

    public CardWindow(Palette palette, double scale)
    {
        Derived derived = Derived.From(palette);
        _canvas = new CardCanvas(derived, scale, this);
        Content = _canvas;

        Title = "Findra";
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        CanResize = false;
        Topmost = true;                       // a card nobody can see is not a card
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle.HideFromAltTab(this);

        _canvas.CardResized += Resize;
        _canvas.CloseRequested += Close;
        Resize();

        // The field is drawn, not a TextBox, so the keys arrive here. TextInput carries typed
        // characters (Hebrew included); KeyDown carries everything a modifier or a control key does.
        KeyDown += (_, e) => { if (_canvas.OnKey(e)) e.Handled = true; };
        AddHandler(TextInputEvent, (_, e) => { if (!string.IsNullOrEmpty(e.Text)) { _canvas.Type(e.Text); e.Handled = true; } },
            Avalonia.Interactivity.RoutingStrategies.Tunnel);

        Opened += (_, _) => { Activate(); _canvas.Focus(); _canvas.OnOpened(); };
        Deactivated += (_, _) => { if (!_canvas.Dragging) Close(); };
        Closed += (_, _) => { _closedAt = Stopwatch.GetTimestamp(); _canvas.Stop(); _dim?.Close(); _dim = null; };
    }

    /// <summary>Darken the monitor behind the card so it stands out. Shown BEFORE the card so the
    /// card lands above it; input passes through it, and it goes when the card goes.</summary>
    public void ShowDim(PixelRect screen, double scaling)
    {
        _dim = new DimWindow(screen, scaling);
        _dim.Show();
    }

    private void Resize()
    {
        Width = _canvas.CardWidth;
        Height = _canvas.CardHeight;
    }

    /// <summary>Put the card so its capsule sits exactly over the widget's capsule.
    /// <paramref name="capsule"/> is that capsule's rectangle in the widget's own unscaled layout
    /// units (what CapsuleLayout lays out in), which <paramref name="scale"/> turns into pixels;
    /// <paramref name="screen"/> is the monitor the whole card is then kept inside.</summary>
    public void PlaceOver(PixelPoint widgetPos, double scale, SKRect capsule, PixelRect screen)
    {
        int w = (int)Math.Ceiling(_canvas.CardWidth), h = (int)Math.Ceiling(SearchCardLayout.Height(SearchCardLayout.MaxRows, true) * scale);
        int x = widgetPos.X + (int)Math.Round((capsule.Left - SearchCardLayout.Pad) * scale);
        int y = widgetPos.Y + (int)Math.Round((capsule.Top - SearchCardLayout.FieldTop) * scale);
        x = Math.Clamp(x, screen.X, Math.Max(screen.X, screen.X + screen.Width - w));
        y = Math.Clamp(y, screen.Y, Math.Max(screen.Y, screen.Y + screen.Height - h));
        Position = new PixelPoint(x, y);
    }

    // ---- the canvas ------------------------------------------------------------------------------

    private sealed class CardCanvas : Control
    {
        private readonly Derived _derived;
        private readonly SKTypeface _face;
        private readonly double _scale;
        private readonly Window _owner;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly DispatcherTimer _timer;
        private readonly DispatcherTimer _debounce;
        private readonly PreviewCache _previews = new(8);

        // The pipe client is owned HERE rather than handed in: the card is the only thing in this
        // process that asks the helper anything, and a connection nobody else can reach has no
        // reason to outlive the window. _life is cancelled when the card closes.
        private readonly CancellationTokenSource _life = new();
        private readonly SemaphoreSlim _connecting = new(1, 1);
        private NameClient? _client;
        private long _connectFailedAt;                  // Stopwatch timestamp; 0 = never failed
        private CancellationTokenSource? _search;       // the search in flight, cancelled by the next
        private volatile string _indexLine = "";

        private volatile SearchCardState _state = SearchCardState.Empty;
        private int _generation;
        private int _detailGen;
        private Point _pressAt;
        private int _pressRow = -1;
        private bool _dragArmed;
        private PointerPressedEventArgs? _pressArgs;   // DoDragDropAsync wants the PRESS, not the move

        public bool Dragging { get; private set; }
        public event Action? CloseRequested;
        public event Action? CardResized;

        public double CardWidth => SearchCardLayout.Width * _scale;
        public double CardHeight => SearchCardLayout.Height(_state.Rows.Count, _state.HasQuery, _state.AdvOpen) * _scale;

        public CardCanvas(Derived derived, double scale, Window owner)
        {
            _owner = owner;
            _scale = Math.Clamp(scale, 0.85, 1.7);
            // The real face is not embedded yet; this is the platform default until it ships
            // (SearchShot.cs renders with the same fallback for the same reason).
            _face = SKTypeface.Default;
            _derived = derived;
            Focusable = true;

            _state = _state with { IndexLine = IndexLine(), OpenedAt = 0 };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
            _timer.Tick += (_, _) =>
            {
                // the caret blinks and the index line moves; nothing else here needs frames -
                // except the unfold, which wants them faster for a quarter of a second
                _state = _state with { Clock = _clock.Elapsed.TotalSeconds, IndexLine = IndexLine() };
                InvalidateVisual();
            };
            _timer.Start();

            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(45) };
            _debounce.Tick += (_, _) => { _debounce.Stop(); RunSearch(); };
        }

        public void Stop()
        {
            _timer.Stop();
            _debounce.Stop();
            _previews.Dispose();
            // The connection goes with the window. Cancel first, so a search still in flight
            // gives up rather than reconnecting behind a card that is already closing. _life
            // itself is not disposed: awaits registered on its token may still be unwinding,
            // and there is no unmanaged resource behind it to release.
            _life.Cancel();
            NameClient? c = Interlocked.Exchange(ref _client, null);
            if (c is not null) CloseClient(c);
        }

        // The elevated helper streams its own freshness; the index line is a status readout, not
        // a query, so it is asked for ONCE when the card opens (RefreshIndexLineAsync) and the
        // 66 ms tick only ever reads the string that left behind. A status call on the tick
        // would put a pipe round trip on the UI thread fifteen times a second.
        private string IndexLine() => _indexLine;

        // ---- typing ----

        public void Type(string text)
        {
            // control characters never belong in a query; a pasted newline closes up
            var clean = new System.Text.StringBuilder();
            foreach (char c in text) if (!char.IsControl(c)) clean.Append(c);
            if (clean.Length == 0) return;
            if (_state.AdvOpen)
            {
                // the popup's fields are append-only; typing lands in the focused one
                string v = _state.Adv.Field(_state.AdvFocus) + clean;
                if (v.Length > 120) v = v[..120];
                _state = _state with { AdvRules = _state.Adv.WithField(_state.AdvFocus, v) };
                InvalidateVisual();
                return;
            }
            int caret = Math.Clamp(_state.Caret, 0, _state.Query.Length);
            string q = _state.Query.Insert(caret, clean.ToString());
            int newCaret = caret + clean.Length;
            if (q.Length > 200) { q = q[..200]; newCaret = Math.Min(newCaret, 200); }
            SetQuery(q, newCaret);
        }

        // Left is left on the screen: through a Hebrew run that is forward in the string
        private void StepVisual(int dir)
        {
            var (_, size, _) = SearchCardPainter.FieldText(SearchCardLayout.FieldRect());
            var p = SearchCardPainter.CaretStep(_state.Query, _state.Caret, _state.CaretSlot, _face, size, dir);
            _state = _state with { Caret = Math.Clamp(p.Caret, 0, _state.Query.Length), CaretSlot = p.Slot };
            InvalidateVisual();
        }

        private void MoveCaret(int to)
        {
            to = Math.Clamp(to, 0, _state.Query.Length);
            if (to == _state.Caret && _state.CaretSlot < 0) return;
            _state = _state with { Caret = to, CaretSlot = -1 };
            InvalidateVisual();
        }

        private void MoveCaret(FieldCaret.Position p)
        {
            _state = _state with { Caret = Math.Clamp(p.Caret, 0, _state.Query.Length), CaretSlot = p.Slot };
            InvalidateVisual();
        }

        // Ctrl+arrow: to the previous / next word boundary, the way every text field does it
        private int WordLeft(int from)
        {
            string q = _state.Query;
            int i = Math.Clamp(from, 0, q.Length);
            while (i > 0 && q[i - 1] == ' ') i--;
            while (i > 0 && q[i - 1] != ' ') i--;
            return i;
        }

        private int WordRight(int from)
        {
            string q = _state.Query;
            int i = Math.Clamp(from, 0, q.Length);
            while (i < q.Length && q[i] != ' ') i++;
            while (i < q.Length && q[i] == ' ') i++;
            return i;
        }

        public bool OnKey(KeyEventArgs e)
        {
            // the open popup takes the keyboard: Tab cycles its fields, Enter applies, Esc closes
            // it (never the card), Backspace edits the focused field
            if (_state.AdvOpen)
            {
                switch (e.Key)
                {
                    case Key.Escape: SetAdvOpen(false); return true;
                    case Key.Enter: ApplyAdv(); return true;
                    case Key.Tab:
                    {
                        int n = SearchAdvanced.FieldCount;
                        int f = (_state.AdvFocus + (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? n - 1 : 1)) % n;
                        _state = _state with { AdvFocus = f };
                        InvalidateVisual();
                        return true;
                    }
                    case Key.Back:
                    {
                        string v = _state.Adv.Field(_state.AdvFocus);
                        if (v.Length > 0)
                        {
                            int cut = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? Math.Max(0, v.TrimEnd().LastIndexOf(' ') + 1) : v.Length - 1;
                            _state = _state with { AdvRules = _state.Adv.WithField(_state.AdvFocus, v[..cut]) };
                            InvalidateVisual();
                        }
                        return true;
                    }
                    case Key.V when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                        _ = Paste();
                        return true;
                    default:
                        return false;   // TextInput carries the typed characters into the field
                }
            }
            switch (e.Key)
            {
                case Key.Escape:
                    if (_state.Query.Length > 0) { SetQuery(""); return true; }
                    CloseRequested?.Invoke();
                    return true;
                case Key.Back:
                {
                    int c = Math.Clamp(_state.Caret, 0, _state.Query.Length);
                    if (c == 0) return true;
                    int from = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? WordLeft(c) : c - 1;
                    SetQuery(_state.Query.Remove(from, c - from), from);
                    return true;
                }
                case Key.Delete:
                {
                    int c = Math.Clamp(_state.Caret, 0, _state.Query.Length);
                    if (c >= _state.Query.Length) return true;
                    int to = e.KeyModifiers.HasFlag(KeyModifiers.Control) ? WordRight(c) : c + 1;
                    SetQuery(_state.Query.Remove(c, to - c), c);
                    return true;
                }
                case Key.Left:
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) MoveCaret(WordLeft(_state.Caret)); else StepVisual(-1);
                    return true;
                case Key.Right:
                    if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) MoveCaret(WordRight(_state.Caret)); else StepVisual(+1);
                    return true;
                case Key.Home: MoveCaret(0); return true;
                case Key.End: MoveCaret(_state.Query.Length); return true;
                case Key.V when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                case Key.Insert when e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                    _ = Paste();
                    return true;
                case Key.Down: MoveHighlight(1); return true;
                case Key.Up: MoveHighlight(-1); return true;
                case Key.PageDown: MoveHighlight(SearchCardLayout.MaxRows); return true;
                case Key.PageUp: MoveHighlight(-SearchCardLayout.MaxRows); return true;
                case Key.Enter: Open(_state.Highlight); return true;
                case Key.Tab:
                    int n = SearchCardLayout.ChipLabels.Length;
                    SetFilter((_state.Filter + (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? n - 1 : 1)) % n);
                    return true;
                case Key.C when e.KeyModifiers.HasFlag(KeyModifiers.Control):
                    CopyPath(_state.Highlight);
                    return true;
                case Key.D1 when e.KeyModifiers.HasFlag(KeyModifiers.Control): SetSort(SearchSort.Best); return true;
                case Key.D2 when e.KeyModifiers.HasFlag(KeyModifiers.Control): SetSort(SearchSort.Newest); return true;
                case Key.D3 when e.KeyModifiers.HasFlag(KeyModifiers.Control): SetSort(SearchSort.Largest); return true;
                default:
                    return false;
            }
        }

        private async Task Paste()
        {
            try
            {
                var clip = TopLevel.GetTopLevel(this)?.Clipboard;
                if (clip is null) return;
                string? text = await Avalonia.Input.Platform.ClipboardExtensions.TryGetValueAsync(clip, DataFormat.Text);
                if (!string.IsNullOrEmpty(text)) Type(text.Length > 200 ? text[..200] : text);
            }
            catch (Exception ex) { Log.Warn("search", "paste failed: " + ex.Message); }
        }

        private void SetQuery(string q, int caret = -1)
        {
            bool had = _state.HasQuery;
            if (caret < 0) caret = q.Length;
            _state = _state with { Query = q, Caret = Math.Clamp(caret, 0, q.Length), CaretSlot = -1,
                Searching = q.Trim().Length > 0, QueryAdv = SearchQuery.IsAdvanced(q) };
            if (q.Trim().Length == 0)
            {
                _generation++;
                _state = _state with { Results = SearchResults.Empty, Rows = Array.Empty<SearchResult>(), Highlight = 0, Scroll = 0, Searching = false, StageImage = null, StageDetail = "" };
            }
            if (had != _state.HasQuery) CardResized?.Invoke();
            InvalidateVisual();
            _debounce.Stop();
            _debounce.Start();
        }

        // ---- the Advanced popup ----

        private void SetAdvOpen(bool open)
        {
            if (_state.AdvOpen == open) return;
            _state = _state with { AdvOpen = open };
            CardResized?.Invoke();   // the card grows to hold the popup
            InvalidateVisual();
        }

        /// <summary>Apply: the rules COMPOSE INTO THE FIELD - visible grammar, editable like
        /// anything typed - and the draft empties, so applying twice cannot double the terms. The
        /// pill's latch and badge follow the field from here (`QueryAdv`). Rules that ask the
        /// inside-of-files question light Content too.</summary>
        private void ApplyAdv()
        {
            string composed = _state.Adv.Compose(_state.Query.Trim());
            bool content = _state.Content || _state.Adv.WantsContent;
            _state = _state with { AdvOpen = false, Content = content, AdvRules = SearchAdvanced.Empty, AdvFocus = 0 };
            Log.Info("search", $"advanced rules applied: {(composed.Length > 0 ? composed : "none")}");
            CardResized?.Invoke();
            SetQuery(composed);
        }

        /// <summary>Clear: every rule in the form at once. The popup stays open.</summary>
        private void ClearAdv()
        {
            _state = _state with { AdvRules = SearchAdvanced.Empty, AdvFocus = 0 };
            Log.Info("search", "advanced rules cleared");
            InvalidateVisual();
        }

        // The Content toggle: pressed, the query searches what is inside files; released, names.
        // Re-runs the current query so the switch answers immediately rather than at the next key.
        private void ToggleContent()
        {
            _state = _state with { Content = !_state.Content, Searching = _state.HasQuery };
            Log.Info("search", $"content mode -> {(_state.Content ? "on" : "off")}");
            InvalidateVisual();
            if (_state.HasQuery) RunSearch();
        }

        private void SetSort(SearchSort sort)
        {
            if (sort == _state.Sort) return;
            _state = _state with { Sort = sort };
            Log.Info("search", $"sort -> {sort}");
            RunSearch();
        }

        // ---- the pipe ----

        private const string HelperMissing = "the name helper is not running";
        private static readonly TimeSpan ConnectRetryAfter = TimeSpan.FromSeconds(5);

        /// <summary>
        /// The connected client, or null when there is nothing to connect to. Connecting is lazy
        /// and its failure is a normal state, not an exception the user sees: the card opens,
        /// takes typing and says what is wrong in its index line. Retries are rate limited -
        /// a five-second connect timeout attempted once per keystroke would freeze the field.
        /// </summary>
        private async Task<NameClient?> ClientAsync(CancellationToken ct)
        {
            if (_life.IsCancellationRequested) return null;
            NameClient? have = _client;
            if (have is not null) return have;
            if (_connectFailedAt != 0 && Stopwatch.GetElapsedTime(_connectFailedAt) < ConnectRetryAfter) return null;

            await _connecting.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Both checks again inside the gate: several keystrokes can arrive while the
                // first connect is still out, and without this each of them opens its own pipe.
                if (_client is not null) return _client;
                if (_connectFailedAt != 0 && Stopwatch.GetElapsedTime(_connectFailedAt) < ConnectRetryAfter) return null;
                try
                {
                    NameClient c = await NameClient.ConnectAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
                    _connectFailedAt = 0;
                    _client = c;
                    Log.Info("card", "connected to the name helper");
                    return c;
                }
                catch (OperationCanceledException) { throw; }   // the card is closing, not a failure
                catch (Exception ex)
                {
                    _connectFailedAt = Stopwatch.GetTimestamp();
                    _indexLine = HelperMissing;
                    Log.Once("card|connect|" + ex.GetType().Name, "WARN", "card",
                        $"{HelperMissing} :: {ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            }
            finally { _connecting.Release(); }
        }

        /// <summary>The connection died under a question. Drop it so the next search opens a new
        /// one, rather than throwing forever into a client whose read pump has ended.</summary>
        private void DropClient()
        {
            _connectFailedAt = Stopwatch.GetTimestamp();
            NameClient? c = Interlocked.Exchange(ref _client, null);
            if (c is not null) CloseClient(c);
        }

        private static void CloseClient(NameClient c)
        {
            // Nothing waits for a closing pipe, but something has to observe its failure.
            _ = Task.Run(async () =>
            {
                try { await c.DisposeAsync().ConfigureAwait(false); }
                catch (Exception ex) { Log.Warn("card", "closing the helper connection: " + ex.Message); }
            });
        }

        /// <summary>The pipe round trip that answers a query. Null means the helper's answer
        /// arrived after a newer query had been written: NameClient's generation gate dropped it,
        /// and painting nothing is exactly what that counter is for.</summary>
        private async Task<QueryReply?> RunSearchAsync(string raw, CancellationToken ct)
        {
            NameClient client = await ClientAsync(ct).ConfigureAwait(false)
                ?? throw new IOException(HelperMissing);
            // MaxRows * 8: the card shows eight rows and scrolls through the rest, so one deep
            // answer beats a second round trip the moment somebody presses PageDown.
            return await client.SearchAsync(raw, SearchCardLayout.MaxRows * 8, ct).ConfigureAwait(false);
        }

        private void RunSearch()
        {
            string q = _state.Query.Trim();
            if (q.Length == 0) return;
            int gen = Interlocked.Increment(ref _generation);
            var mine = new CancellationTokenSource();
            // Cancel the previous search's OWN work - never its request; see SearchOnceAsync.
            // The replaced source is not disposed: nothing registers a callback or a wait handle
            // on this token, so there is nothing to release, and disposing one while the search
            // holding it is still deciding whether to apply its answer is the worse bug.
            Interlocked.Exchange(ref _search, mine)?.Cancel();
            SearchSort sort = _state.Sort;
            _ = Task.Run(() => SearchOnceAsync(q, gen, sort, mine.Token));
        }

        /// <summary>
        /// One query, end to end, off the UI thread. Three races have to be lost gracefully, and
        /// each needs its own guard:
        /// <list type="bullet">
        /// <item>the answer lost on the WIRE - a newer query was already written, so NameClient's
        /// generation gate hands back null and nothing is painted;</item>
        /// <item>the answer lost to the DEBOUNCE - the user typed again while this request was
        /// out, so the newer query has not reached the wire yet and the gate cannot see it. Our
        /// own token catches that, and so does the case where the field was simply cleared
        /// (SetQuery("") bumps the local generation without ever writing a query at all);</item>
        /// <item>the answer lost to the UI THREAD - the post below runs later still, so the
        /// generation is checked once more before any state is replaced.</item>
        /// </list>
        /// </summary>
        private async Task SearchOnceAsync(string raw, int gen, SearchSort sort, CancellationToken ct)
        {
            SearchResults r;
            try
            {
                long started = Stopwatch.GetTimestamp();
                // The REQUEST rides the window's lifetime token, not this search's. Frame writes
                // a frame as a single buffer, so a cancellation can no longer tear a header from
                // its payload - but a cancelled overlapped write can still leave part of a buffer
                // on the wire, and this is one shared full-duplex connection whose replies are
                // matched by id: desynchronise it once and every later search on it is wrong,
                // permanently. An abandoned answer costs a round trip that has already happened
                // and is discarded for free by generation, so it is never cancelled - only ignored.
                QueryReply? reply = await RunSearchAsync(raw, _life.Token).ConfigureAwait(false);
                if (reply is null) return;                  // stale on the wire
                if (ct.IsCancellationRequested) return;     // stale against the debounce
                double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                // Mapping stats every row it keeps, so it belongs out here on the pool: a stat is
                // tens of microseconds and there are up to MaxRows * 8 of them, which is a
                // visible hitch if it lands on the UI thread. `size:` and `modified:` are applied
                // in there too - the helper holds names, not directory entries, and answers those
                // filters unfiltered by design.
                r = ResultMapper.Build(raw, reply.Rows, new SearchQuery(raw), sort, ms);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _life.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // Nothing to connect to, or a helper that went away mid-question - the client
                // cancels every waiter when its pump ends, which is the second shape here.
                DropClient();
                _indexLine = HelperMissing;
                Log.Once("card|search|" + ex.GetType().Name, "WARN", "card", $"{HelperMissing} :: {ex.Message}");
                r = SearchResults.Empty with { Query = raw, Note = HelperMissing };
            }
            catch (Exception ex)
            {
                Log.Once("search|query|" + ex.GetType().Name, "WARN", "search", $"search failed :: {ex.Message}");
                r = SearchResults.Empty with { Query = raw, Note = "search failed - see the log" };
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (gen != _generation) return;   // a newer query is already running
                int before = _state.Rows.Count;
                var rows = SearchCardState.Filtered(r, _state.Filter);
                _state = _state with { Results = r, Rows = rows, Highlight = 0, Scroll = 0, Searching = false };
                if (rows.Count != before) CardResized?.Invoke();
                HighlightChanged();
                InvalidateVisual();
            });
        }

        /// <summary>The card is up. Ask the helper once what it holds; the timer only ever reads
        /// the line this leaves behind.</summary>
        public void OnOpened() => _ = Task.Run(() => RefreshIndexLineAsync(_life.Token));

        private async Task RefreshIndexLineAsync(CancellationToken ct)
        {
            try
            {
                NameClient? c = await ClientAsync(ct).ConfigureAwait(false);
                if (c is null) return;                      // ClientAsync has already said why
                StatusReply s = await c.StatusAsync(ct).ConfigureAwait(false);
                _indexLine = IndexLineFormatter.IndexLineFor(s);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (Exception ex)
            {
                DropClient();
                _indexLine = HelperMissing;
                Log.Once("card|status|" + ex.GetType().Name, "WARN", "card", $"{HelperMissing} :: {ex.Message}");
            }
        }

        private void SetFilter(int f)
        {
            int before = _state.Rows.Count;
            var rows = SearchCardState.Filtered(_state.Results, f);
            _state = _state with { Filter = f, Rows = rows, Highlight = 0, Scroll = 0 };
            if (rows.Count != before) CardResized?.Invoke();
            HighlightChanged();
            InvalidateVisual();
        }

        private void MoveHighlight(int delta)
        {
            int n = _state.Rows.Count;
            if (n == 0) return;
            int h = Math.Clamp(_state.Highlight + delta, 0, n - 1);
            int scroll = _state.Scroll;
            if (h < scroll) scroll = h;
            if (h >= scroll + SearchCardLayout.MaxRows) scroll = h - SearchCardLayout.MaxRows + 1;
            _state = _state with { Highlight = h, Scroll = SearchCardLayout.ClampScroll(scroll, n) };
            HighlightChanged();
            InvalidateVisual();
        }

        // The stage follows the highlight: a preview decode and a stat, both off-thread, both
        // discarded if the highlight has moved on by the time they land.
        private void HighlightChanged()
        {
            int h = _state.Highlight;
            if (h < 0 || h >= _state.Rows.Count) { _state = _state with { StageImage = null, StageDetail = "" }; return; }
            var row = _state.Rows[h];
            int gen = Interlocked.Increment(ref _detailGen);

            var cached = _previews.Get(row.Path);
            _state = _state with { StageImage = cached, StageDetail = "" };

            _ = Task.Run(() =>
            {
                string detail = "";
                try
                {
                    if (row.Kind == ResultKind.Folder)
                    {
                        var di = new DirectoryInfo(row.Path);
                        if (di.Exists) detail = $"folder · {di.LastWriteTime:d MMM yyyy HH:mm}";
                    }
                    else
                    {
                        var fi = new FileInfo(row.Path);
                        if (fi.Exists) detail = $"{Human(fi.Length)} · {fi.LastWriteTime:d MMM yyyy HH:mm}";
                    }
                }
                catch { }

                SKImage? img = cached;
                if (img is null && row.Kind is not ResultKind.Folder)
                {
                    try { img = DecodePreview(row.Path, row.Kind, 420, row.MomentSeconds); }
                    catch (Exception ex) { Log.Once("search|preview|" + ex.GetType().Name, "WARN", "search", $"preview failed for {Path.GetExtension(row.Path)} :: {ex.Message}"); }
                }

                Dispatcher.UIThread.Post(() =>
                {
                    if (img is not null) _previews.Put(row.Path, img);   // cached even if late: the next visit is free
                    if (gen != _detailGen) return;
                    _state = _state with { StageDetail = detail, StageImage = img };
                    InvalidateVisual();
                });
            });
        }

        // No decoder yet: previews (photo frames, shell thumbnails, video frames at a moment)
        // arrive with content indexing. Until then the stage falls back to its no-art tile, which
        // already handles a null image.
        private static SKImage? DecodePreview(string path, ResultKind kind, int maxDim, double moment) => null;

        private static string Human(long bytes)
        {
            if (bytes < 1024) return $"{bytes} B";
            double v = bytes / 1024.0;
            if (v < 1024) return $"{v:0.#} KB";
            v /= 1024;
            if (v < 1024) return $"{v:0.#} MB";
            return $"{v / 1024:0.##} GB";
        }

        // ---- actions ----

        private void Open(int index)
        {
            if (index < 0 || index >= _state.Rows.Count) return;
            var row = _state.Rows[index];
            NoteOpened(row.Path);
            CardActions.Open(row);
            CloseRequested?.Invoke();
        }

        private void Reveal(int index)
        {
            if (index < 0 || index >= _state.Rows.Count) return;
            NoteOpened(_state.Rows[index].Path);
            CardActions.Reveal(_state.Rows[index].Path);
            CloseRequested?.Invoke();
        }

        // Recency ranking lands with the content store; until then, opening a result changes
        // nothing about how future results are scored.
        private static void NoteOpened(string path) { }

        private void CopyPath(int index)
        {
            if (index < 0 || index >= _state.Rows.Count) return;
            string path = _state.Rows[index].Path;
            _ = Task.Run(async () =>
            {
                try
                {
                    var clip = await Dispatcher.UIThread.InvokeAsync(() => TopLevel.GetTopLevel(this)?.Clipboard);
                    if (clip is null) return;
                    var data = new DataTransfer();
                    data.Add(DataTransferItem.Create(DataFormat.Text, path));
                    await clip.SetDataAsync(data);
                    Log.Info("search", "copied a path");
                }
                catch (Exception ex) { Log.Warn("search", "copy failed: " + ex.Message); }
            });
        }

        // ---- pointer ----

        private SearchHit HitAt(Point p)
            => SearchCardLayout.HitTest((float)(p.X / _scale), (float)(p.Y / _scale),
                _state.Rows.Count, _state.Scroll, _state.HasQuery, _state.AdvOpen);

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            var p = e.GetPosition(this);
            if (_dragArmed && _pressRow >= 0 && !Dragging && _pressArgs is { } press
                && Math.Abs(p.X - _pressAt.X) + Math.Abs(p.Y - _pressAt.Y) > 8)
            {
                _dragArmed = false;
                _ = StartDrag(press, _pressRow);
                return;
            }
            var hit = HitAt(p);
            if (hit.Target == _state.HoverTarget && hit.Index == _state.HoverIndex) return;
            _state = _state with { HoverTarget = hit.Target, HoverIndex = hit.Index };
            InvalidateVisual();
        }

        protected override void OnPointerExited(PointerEventArgs e)
        {
            _state = _state with { HoverTarget = SearchTarget.None, HoverIndex = -1 };
            InvalidateVisual();
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            Focus();
            var p = e.GetPosition(this);
            var hit = HitAt(p);
            var props = e.GetCurrentPoint(this).Properties;
            if (props.IsRightButtonPressed)
            {
                if (hit.Target == SearchTarget.Row) Reveal(hit.Index);
                else if (hit.Target == SearchTarget.Stage) Reveal(_state.Highlight);
                e.Handled = true;
                return;
            }
            switch (hit.Target)
            {
                case SearchTarget.Field:
                {
                    // the same window the painter drew, so the click lands on the glyph it is over
                    var (left, size, maxW) = SearchCardPainter.FieldText(SearchCardLayout.FieldRect());
                    float x = (float)(p.X / _scale) - left;
                    MoveCaret(SearchCardPainter.CaretAt(_state.Query, _state.Caret, _face, size, maxW, x));
                    break;
                }
                case SearchTarget.Content: ToggleContent(); break;
                case SearchTarget.Adv: SetAdvOpen(!_state.AdvOpen); break;
                case SearchTarget.AdvField:
                    _state = _state with { AdvFocus = hit.Index };
                    break;
                case SearchTarget.AdvCheck:
                    _state = _state with { AdvRules = hit.Index == 0
                        ? _state.Adv with { MatchCase = !_state.Adv.MatchCase }
                        : _state.Adv with { WholeWords = !_state.Adv.WholeWords } };
                    break;
                case SearchTarget.AdvKind:
                    _state = _state with { AdvRules = _state.Adv with { Kind = hit.Index } };
                    break;
                case SearchTarget.AdvButton:
                    if (hit.Index == 0) ClearAdv();
                    else if (hit.Index == 1) SetAdvOpen(false);
                    else ApplyAdv();
                    break;
                case SearchTarget.None when _state.AdvOpen && hit.Index == -2:
                    SetAdvOpen(false);   // a click outside the open popup dismisses it, not the card
                    break;
                case SearchTarget.Chip: SetFilter(hit.Index); break;
                case SearchTarget.Row:
                    // first click highlights (and arms a drag); a click on the highlighted row opens
                    if (_state.Highlight == hit.Index && !_dragArmed) { Open(hit.Index); break; }
                    _state = _state with { Highlight = hit.Index };
                    HighlightChanged();
                    _pressAt = p; _pressRow = hit.Index; _dragArmed = true; _pressArgs = e;
                    break;
                case SearchTarget.Open: Open(_state.Highlight); break;
                case SearchTarget.Reveal: Reveal(_state.Highlight); break;
                case SearchTarget.Copy: CopyPath(_state.Highlight); break;
                case SearchTarget.Stage:
                    _pressAt = p; _pressRow = _state.Highlight; _dragArmed = true; _pressArgs = e;
                    break;
            }
            InvalidateVisual();
            e.Handled = true;
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            // the press highlighted the row and armed a drag; letting go without moving is a click,
            // and the NEXT click on the same row opens it (see OnPointerPressed)
            _dragArmed = false;
            _pressRow = -1;
            _pressArgs = null;
        }

        protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
        {
            int next = SearchCardLayout.ClampScroll(_state.Scroll - Math.Sign(e.Delta.Y) * 2, _state.Rows.Count);
            if (next == _state.Scroll) return;
            _state = _state with { Scroll = next };
            InvalidateVisual();
            e.Handled = true;
        }

        // The row becomes a real shell drag: Explorer, a browser upload box, a chat window all
        // accept it, which is what a dragged result is for.
        private async Task StartDrag(PointerPressedEventArgs e, int row)
        {
            if (row < 0 || row >= _state.Rows.Count) return;
            var r = _state.Rows[row];
            try
            {
                var sp = TopLevel.GetTopLevel(this)?.StorageProvider;
                if (sp is null) return;
                var uri = new Uri(r.Path);
                Avalonia.Platform.Storage.IStorageItem? item = r.Kind == ResultKind.Folder
                    ? await sp.TryGetFolderFromPathAsync(uri)
                    : await sp.TryGetFileFromPathAsync(uri);
                if (item is null) return;
                var data = new DataTransfer();
                data.Add(DataTransferItem.CreateFile(item));
                data.Add(DataTransferItem.Create(DataFormat.Text, r.Path));
                Dragging = true;
                Log.Info("search", $"drag out: {r.Name}");
                await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy | DragDropEffects.Link);
            }
            catch (Exception ex) { Log.Warn("search", "drag failed: " + ex.Message); }
            finally
            {
                Dragging = false;
                // the drop went elsewhere, so we are no longer the active window
                if (!_owner.IsActive) CloseRequested?.Invoke();
            }
        }

        // ---- paint ----

        public override void Render(DrawingContext context)
            => context.Custom(new DrawOp(new Rect(Bounds.Size), this));

        private sealed class DrawOp : ICustomDrawOperation
        {
            private readonly CardCanvas _c;
            public DrawOp(Rect b, CardCanvas c) { Bounds = b; _c = c; }
            public Rect Bounds { get; }
            public bool HitTest(Point p) => true;
            public bool Equals(ICustomDrawOperation? other) => false;
            public void Dispose() { }

            public void Render(ImmediateDrawingContext context)
            {
                if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature) return;
                using var lease = feature.Lease();
                var canvas = lease.SkCanvas;
                // The clock is read HERE, at paint time, not from the last timer tick: an animation
                // sampled at the timer's cadence steps in 66 ms lumps. While the unfold runs, the
                // next frame is asked for as soon as this one is painted, so it moves at the
                // display's rate and the timer never gets a say.
                var st = _c._state with { Clock = _c._clock.Elapsed.TotalSeconds };
                canvas.Save();
                canvas.Scale((float)_c._scale);
                SearchCardPainter.Paint(canvas, st, _c._derived, _c._face);
                canvas.Restore();
                if (SearchCardPainter.Unfolding(st))
                    Dispatcher.UIThread.Post(_c.InvalidateVisual, DispatcherPriority.Render);
            }
        }
    }
}
