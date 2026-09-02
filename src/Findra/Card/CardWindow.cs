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

    /// <summary><paramref name="content"/> is the process's ONE open content index, or null when
    /// this session has none. The window borrows it and never disposes it: the store outlives
    /// every card, and a second connection would mean a second schema check and a second place a
    /// migration could run.
    ///
    /// <para><paramref name="semantic"/> is borrowed on the same terms and for a sharper reason:
    /// a query encoder is a hundred milliseconds and a hundred megabytes, so it is opened once
    /// for the process and never per card. Null is ordinary - it is what a machine that took no
    /// model has. <paramref name="installed"/> is read once when the shell starts, never per
    /// keystroke, and only decides what an empty answer may offer.</para></summary>
    public CardWindow(Palette palette, double scale, ContentDb? content = null,
                      Semantic? semantic = null, CapabilitySet installed = default)
    {
        Derived derived = Derived.From(palette);
        _canvas = new CardCanvas(derived, scale, this, content, semantic, installed);
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
    /// units (what CapsuleLayout lays out in), which <paramref name="scale"/> turns into DIPs;
    /// <paramref name="screen"/> is the monitor the whole card is then kept inside.
    ///
    /// <para><paramref name="widgetPos"/> and <paramref name="screen"/> are PHYSICAL pixels, so
    /// the card's size and the offset have to be physical too - physical is DIP times the
    /// monitor's scaling. <paramref name="screenScaling"/> says what that monitor's scaling is;
    /// leave it at zero and it is looked up from the point the widget is at, which is right
    /// whenever the widget is on a monitor Avalonia can name.</para></summary>
    public void PlaceOver(PixelPoint widgetPos, double scale, SKRect capsule, PixelRect screen,
                          double screenScaling = 0)
    {
        double s = screenScaling > 0 ? screenScaling : ScalingAt(widgetPos);
        Position = CardOverPlacement.Over(widgetPos, scale, capsule, screen, s);
    }

    /// <summary>The scaling of the monitor the widget is on. Asked of the screen rather than of
    /// this window, because the card has not been shown yet and its own RenderScaling is whatever
    /// the platform guessed before it landed anywhere.</summary>
    private double ScalingAt(PixelPoint widgetPos)
    {
        try
        {
            Screens? screens = Screens;
            Avalonia.Platform.Screen? s = screens?.ScreenFromPoint(widgetPos) ?? screens?.Primary;
            if (s is not null && s.Scaling > 0) return s.Scaling;
        }
        catch (Exception ex) { Log.Warn("card", "could not read the monitor scaling: " + ex.Message); }
        return RenderScaling > 0 ? RenderScaling : 1.0;
    }

    // ---- the canvas ------------------------------------------------------------------------------

    // Fully qualified, because the settings model's row type is `Findra.Control` and a type in
    // this file's own namespace beats one arriving through a using directive - so a bare `Control`
    // here binds to a sealed record. A using alias cannot fix it: inside the namespace it collides
    // with the member, and outside it loses to the member.
    private sealed class CardCanvas : Avalonia.Controls.Control
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
        // Neither field can carry the `volatile` keyword - _client is passed by ref to Interlocked,
        // which warns that a volatile field is not treated as volatile there, and C# does not allow
        // volatile on a long at all. Volatile.Read/Write on every access is the same fence.
        private NameClient? _client;
        private long _connectFailedAt;                  // Stopwatch timestamp; 0 = never failed
        private CancellationTokenSource? _search;       // the search in flight, cancelled by the next
        private volatile string _indexLine = "";

        // The content index. Borrowed from the process, never opened or disposed here. Null is a
        // normal state: a session with no content store answers the Content pill with a sentence
        // rather than an empty card.
        private readonly ContentDb? _db;
        // The model-backed half of a content query, and what this machine has installed. Both are
        // borrowed from the process for the same reason the store is: an encoder is opened once,
        // and the installed set is a fact about the disk read once when the shell starts rather
        // than restated on every keystroke. Null and default are ordinary - a machine that took
        // no model searches the words in its documents through exactly the same call.
        private readonly Semantic? _semantic;
        private readonly CapabilitySet _installed;
        // ContentDb wraps one SQLite connection, which is not re-entrant. These are the card's own
        // two readers - a query and the once-a-second status line - and this keeps them off each
        // other. It says nothing about other holders of the same instance; see the note on the
        // constructor's parameter.
        private readonly object _dbGate = new();
        private volatile string _contentLine = "";
        private long _contentReadAt;                    // Stopwatch timestamp; 0 = never read
        private int _contentReading;                    // 0 = idle, 1 = a read is out

        private volatile SearchCardState _state = SearchCardState.Empty;
        private readonly SearchGate _gate = new();
        private readonly SearchIssueQueue _issue = new();
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

        public CardCanvas(Derived derived, double scale, Window owner, ContentDb? db,
                          Semantic? semantic = null, CapabilitySet installed = default)
        {
            _owner = owner;
            _db = db;
            _semantic = semantic;
            _installed = installed;
            _scale = Math.Clamp(scale, 0.85, 1.7);
            // The shipped face, not the platform's - one resolver for every surface, so the card
            // and the shot of the card are the same picture. Parts.Face falls back to the system
            // default on its own if the resource is missing.
            _face = Parts.Face;
            _derived = derived;
            Focusable = true;

            _state = _state with { IndexLine = IndexLine(), OpenedAt = 0 };

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(66) };
            _timer.Tick += (_, _) =>
            {
                // the caret blinks and the index line moves; nothing else here needs frames -
                // except the unfold, which wants them faster for a quarter of a second
                PumpContentLine();
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

        // The elevated helper streams its own freshness; the names half of the index line is a
        // status readout, not a query, so it is asked for ONCE when the card opens
        // (RefreshIndexLineAsync) and the 66 ms tick only ever reads the string that left behind.
        // A status call on the tick would put a pipe round trip on the UI thread fifteen times a
        // second. The content half is a local database rather than a pipe, so it can be re-read
        // while the card is up - but not per frame; see PumpContentLine.
        private string IndexLine()
        {
            string names = _indexLine, content = _contentLine;
            if (names.Length == 0) return content;
            if (content.Length == 0) return names;
            return names + " · " + content;
        }

        /// <summary>How often the content half of the index line is re-read. Fifteen reads a
        /// second for a string that changes every few seconds is fifteen SQLite queries a second
        /// nobody asked for.</summary>
        private static readonly TimeSpan ContentStatusEvery = TimeSpan.FromSeconds(1);

        /// <summary>
        /// Called from the 66 ms tick, on the UI thread, and it only ever SCHEDULES: the read
        /// itself is four small SELECTs against a file, which belongs on the pool like every other
        /// disk touch here. At most one is in flight and at most one a second is started.
        /// </summary>
        private void PumpContentLine()
        {
            ContentDb? db = _db;
            if (db is null || _life.IsCancellationRequested) return;
            long last = Volatile.Read(ref _contentReadAt);
            if (last != 0 && Stopwatch.GetElapsedTime(last) < ContentStatusEvery) return;
            if (Interlocked.CompareExchange(ref _contentReading, 1, 0) != 0) return;
            // Stamped when the read STARTS, not when it lands: a slow read must not be followed
            // immediately by another the moment it finishes.
            Volatile.Write(ref _contentReadAt, Stopwatch.GetTimestamp());
            _ = Task.Run(() =>
            {
                try { _contentLine = ContentStatusLine(db); }
                catch (Exception ex)
                {
                    // A status line is a nicety. The card keeps whatever it last said rather than
                    // replacing a true sentence with an error nobody can act on.
                    Log.Once("card|indexstatus|" + ex.GetType().Name, "WARN", "card",
                        "could not read the content index status :: " + ex.Message);
                }
                finally { Volatile.Write(ref _contentReading, 0); }
            });
        }

        /// <summary>The content half of the index line, read from the meta rows the indexer child
        /// writes. Synchronous on purpose: every ContentDb call is, and wrapping a synchronous
        /// SQLite read in an async signature would only promise a yield it does not make.</summary>
        private string ContentStatusLine(ContentDb db)
        {
            string state, beat, pid;
            long pending, indexed;
            bool rebuilt, contentOn;
            lock (_dbGate)
            {
                // The card has no Config - it reads through its own read-only connection - so it
                // asks the one row the interface writes the switch to. Absent means off, which is
                // the setting's own default: an index nobody has asked for must not be described
                // by the counts alone, because zero queued and zero indexed is byte-for-byte what
                // a finished index looks like.
                contentOn = db.Get("index:paused") == "0";
                state = db.Get("indexer:state") ?? "off";
                beat = db.Get("indexer:beat") ?? "";
                // The pid goes with the heartbeat, always. IndexStatus.Alive owns the rule that
                // reads the two together, so this card, the capsule, --searchprobe and
                // --searchindex give one answer about one pair of rows rather than four.
                pid = db.Get("indexer:pid") ?? "";
                pending = db.PendingCount();
                indexed = db.IndexedCount();
                // WasRebuilt is a fact about the OPEN that rebuilt the file, and this card reads
                // through its own read-only connection, which did no such thing. The session that
                // owns the writer records the answer in the index itself for exactly that reason,
                // and the property still counts for a card handed the writer directly.
                rebuilt = db.WasRebuilt || db.Get("index:rebuilt") == "1";
            }
            return IndexStatus.Line(contentOn, state, pending, indexed, IndexStatus.Alive(beat, pid), rebuilt);
        }

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
                // Nothing is written, so the wire's gate can never see this: the abandoned
                // generation is what stops an answer to the old text painting over an empty card,
                // and it is checked on the UI thread, where the post lands.
                _gate.Abandon();
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

        /// <summary>Is a connect attempt still inside the backoff a failed one opened? Only a
        /// connection that really died stamps that timestamp, so this cannot slide.</summary>
        private bool RateLimited()
        {
            long failed = Volatile.Read(ref _connectFailedAt);
            return failed != 0 && Stopwatch.GetElapsedTime(failed) < ConnectRetryAfter;
        }

        /// <summary>
        /// The connected client, or null when there is nothing to connect to. Connecting is lazy
        /// and its failure is a normal state, not an exception the user sees: the card opens,
        /// takes typing and says what is wrong in its index line. Retries are rate limited -
        /// a five-second connect timeout attempted once per keystroke would freeze the field.
        /// </summary>
        private async Task<NameClient?> ClientAsync(CancellationToken ct)
        {
            if (_life.IsCancellationRequested) return null;
            NameClient? have = Volatile.Read(ref _client);
            if (have is not null) return have;
            if (RateLimited()) return null;

            await _connecting.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // Both checks again inside the gate: several keystrokes can arrive while the
                // first connect is still out, and without this each of them opens its own pipe.
                NameClient? again = Volatile.Read(ref _client);
                if (again is not null) return again;
                if (RateLimited()) return null;

                // Was the last attempt a failure? Then this is a RECONNECT, and the index line
                // underneath the rows is still saying the helper is not running. See below.
                bool reconnecting = Volatile.Read(ref _connectFailedAt) != 0;
                try
                {
                    NameClient c = await NameClient.ConnectAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);

                    // The card can close while the connect is out, and Stop() has then already
                    // taken the field and cancelled _life. Storing this one now would strand a
                    // live pipe session nobody will ever read or dispose - and the helper only
                    // serves a handful of them, so a few stranded sessions stop search working
                    // altogether until it is restarted.
                    if (_life.IsCancellationRequested) { CloseClient(c); return null; }

                    Volatile.Write(ref _connectFailedAt, 0);
                    Volatile.Write(ref _client, c);
                    Log.Info("card", "connected to the name helper");

                    // The index line is asked for once, when the card opens. If the helper was
                    // absent then, it still reads "not running" - which would sit there
                    // contradicting the rows for the rest of the card's life. Ask again now that
                    // there is someone to ask. Re-entrancy is safe: _client is already stored, so
                    // the call below takes the early return above and never reaches this gate.
                    if (reconnecting) _ = Task.Run(() => RefreshIndexLineAsync(_life.Token));
                    return c;
                }
                catch (OperationCanceledException) { throw; }   // the card is closing, not a failure
                catch (Exception ex)
                {
                    Volatile.Write(ref _connectFailedAt, Stopwatch.GetTimestamp());
                    _indexLine = HelperMissing;
                    Log.Once("card|connect|" + ex.GetType().Name, "WARN", "card",
                        $"{HelperMissing} :: {ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            }
            finally { _connecting.Release(); }
        }

        /// <summary>The connection died under a question. Drop it so the next search opens a new
        /// one, rather than throwing forever into a client whose read pump has ended.
        ///
        /// <para>Only the instance that actually failed. Requests are never cancelled, so
        /// overlapping searches are the normal case: an unconditional exchange lets a search that
        /// is still unwinding tear down the healthy connection a later search has just opened, and
        /// then rate-limit the next five seconds of typing into "the helper is not running" -
        /// exactly the helper-restart case this code exists to handle.</para>
        ///
        /// <para>And only a connection that really died stamps the backoff. A rate-limited skip
        /// re-stamping it turns "retry no more than every five seconds" into "five seconds after
        /// the last keystroke", so someone typing while the helper starts never reconnects at
        /// all.</para></summary>
        private void DropClient(NameClient? failed)
        {
            if (failed is null) return;
            if (!ReferenceEquals(Interlocked.CompareExchange(ref _client, null, failed), failed)) return;
            Volatile.Write(ref _connectFailedAt, Stopwatch.GetTimestamp());
            CloseClient(failed);
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
            try
            {
                // MaxRows * 8: the card shows eight rows and scrolls through the rest, so one deep
                // answer beats a second round trip the moment somebody presses PageDown.
                return await client.SearchAsync(raw, SearchCardLayout.MaxRows * 8, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Dropped HERE, where the instance that failed is still in hand. The caller only
                // sees an exception and could not tell this client from the one another search
                // may have opened in the meantime.
                DropClient(client);
                throw;
            }
        }

        private void RunSearch()
        {
            string q = _state.Query.Trim();
            if (q.Length == 0) return;
            int gen = _gate.Issue();
            var mine = new CancellationTokenSource();
            // Cancel the previous search's OWN work - never its request; see SearchOnceAsync.
            // The replaced source is not disposed: nothing registers a callback or a wait handle
            // on this token, so there is nothing to release, and disposing one while the search
            // holding it is still deciding whether to apply its answer is the worse bug.
            Interlocked.Exchange(ref _search, mine)?.Cancel();
            SearchSort sort = _state.Sort;
            // Read HERE, on the UI thread, in the same breath as the generation - never inside the
            // task. The pill can be pressed between scheduling the work and the pool thread
            // reading _state, and the answer would then come back from the wrong half of the card.
            bool content = _state.Content;

            // Queued HERE, on the UI thread, in the same breath as the generation above - that
            // adjacency is the whole fix. The card numbers its searches locally, the pipe client
            // numbers them again on the wire, and nothing used to make the two orders agree:
            // handing both searches to the pool and letting them race for the write lock inverted
            // them a few times in a hundred, and an inverted pair loses BOTH answers - the older
            // one to its own cancelled token, the newer one to a wire gate that has already seen a
            // higher number. The card then kept stale rows with the indicator still spinning until
            // another key was pressed. Serialising issuance costs nothing: the helper answers one
            // request at a time per connection anyway (it reads a frame, writes the reply, then
            // reads the next), so the overlap was never parallelism - only a way to disagree.
            // Timed from here, not from where the answer is awaited: with issuance ordered, a
            // search can sit behind an older one, and that wait is time the user spent looking at
            // the indicator. Starting the clock later would report a round trip that had already
            // finished as having taken no time at all.
            long started = Stopwatch.GetTimestamp();
            if (content)
            {
                // A different question, not a second opinion: the content answer is never merged
                // with the name answer, because blending them lets a file merely NAMED "lease"
                // outrank the lease itself, found by its words - which is the thing the pill
                // exists to ask for. No _issue queue either: there is no wire here to keep in
                // order, only a local file, so the generation and the token are the whole guard.
                _ = Task.Run(() => ContentOnce(q, gen, sort, started, mine.Token));
                return;
            }
            Task<QueryReply?> reply = _issue.Enqueue(() => RunSearchAsync(q, _life.Token));
            _ = Task.Run(() => SearchOnceAsync(reply, q, gen, sort, started, mine.Token));
        }

        private const string NoContentIndex = "the content index is not open in this session";

        /// <summary>
        /// The Content pill's answer, off the UI thread. The store is the interface process's own
        /// file at normal integrity, so this asks it directly - the elevated helper holds names in
        /// RAM and has never seen this database.
        ///
        /// <para>Not async, and deliberately: every ContentDb call is synchronous, so an async
        /// signature here would promise a yield it never makes. It runs on the pool because
        /// <see cref="RunSearch"/> put it there.</para>
        /// </summary>
        private void ContentOnce(string raw, int gen, SearchSort sort, long started, CancellationToken ct)
        {
            SearchResults r;
            try
            {
                ContentDb? db = _db;
                if (db is null)
                {
                    // Nothing to ask. Saying so is the answer; an empty card would read as "your
                    // words are in no file", which is a different and untrue claim.
                    r = SearchResults.Empty with { Query = raw, ContentReady = true, Note = NoContentIndex };
                }
                else
                {
                    // MaxRows * 8: the card shows eight rows and scrolls through the rest.
                    lock (_dbGate)
                        r = ContentBranch.Search(db, raw, SearchCardLayout.MaxRows * 8, sort,
                                                 semantic: _semantic, installed: _installed);
                }
            }
            catch (Exception ex)
            {
                Log.Once("search|content|" + ex.GetType().Name, "WARN", "search",
                    $"content search failed :: {ex.Message}");
                r = SearchResults.Empty with { Query = raw, ContentReady = true, Note = "content search failed - see the log" };
            }

            // The same gate the name path uses, for the same reasons - except that nothing here
            // came off a wire, so there is no third verdict to fold in.
            if (!_gate.MayApply(gen, replyIsNull: false, ct.IsCancellationRequested))
            {
                ClearSearchingIfNewest(gen);
                return;
            }
            Apply(r with { ContentMs = Stopwatch.GetElapsedTime(started).TotalMilliseconds }, gen);
        }

        /// <summary>
        /// One query, end to end, off the UI thread. Three races have to be lost gracefully, and
        /// each needs its own guard:
        /// <list type="bullet">
        /// <item>the answer lost on the WIRE - a newer query was already written, so NameClient's
        /// generation gate hands back null and nothing is painted;</item>
        /// <item>the answer lost to the DEBOUNCE - the user typed again while this request was
        /// out, so the newer query has not reached the wire yet and the gate cannot see it. Our
        /// own token catches that one;</item>
        /// <item>the answer lost to the UI THREAD - the post below runs later still, so the
        /// generation is checked once more before any state is replaced. This is also the guard
        /// that catches a field simply cleared: SetQuery("") abandons the generation without ever
        /// writing a query, and never touches this search's token.</item>
        /// </list>
        /// The three are one decision, taken by <see cref="SearchGate"/> so it can be tested
        /// without a display. Dropping an answer paints nothing, but it must never leave the
        /// indicator up: issuance is ordered, so a dropped answer always has a newer search behind
        /// it that will land - and if it somehow does not, the drop clears the indicator itself.
        /// </summary>
        private async Task SearchOnceAsync(Task<QueryReply?> issued, string raw, int gen,
                                           SearchSort sort, long started, CancellationToken ct)
        {
            SearchResults r;
            try
            {
                // The REQUEST rides the window's lifetime token, not this search's. Frame writes
                // a frame as a single buffer, so a cancellation can no longer tear a header from
                // its payload - but a cancelled overlapped write can still leave part of a buffer
                // on the wire, and this is one shared full-duplex connection whose replies are
                // matched by id: desynchronise it once and every later search on it is wrong,
                // permanently. An abandoned answer costs a round trip that has already happened
                // and is discarded for free by generation, so it is never cancelled - only ignored.
                QueryReply? reply = await issued.ConfigureAwait(false);
                if (!_gate.MayApply(gen, reply is null, ct.IsCancellationRequested))
                {
                    ClearSearchingIfNewest(gen);
                    return;
                }
                double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                // Mapping stats every row it keeps, so it belongs out here on the pool: a stat is
                // tens of microseconds and there are up to MaxRows * 8 of them, which is a
                // visible hitch if it lands on the UI thread. `size:` and `modified:` are applied
                // in there too - the helper holds names, not directory entries, and answers those
                // filters unfiltered by design.
                r = ResultMapper.Build(raw, reply!.Rows, new SearchQuery(raw), sort, ms);
            }
            catch (ObjectDisposedException) when (_life.IsCancellationRequested)
            {
                // Stop() disposes the transport under a search that is still on the wire. The card
                // is going: there is no state to post and nothing here is worth a line in the log,
                // which is where a WARN for a closed window sends the next reader hunting.
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested || _life.IsCancellationRequested)
            {
                ClearSearchingIfNewest(gen);
                return;
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // Nothing to connect to, or a helper that went away mid-question - the client
                // cancels every waiter when its pump ends, which is the second shape here. The
                // client that failed was already dropped where it was still in hand
                // (RunSearchAsync); there is nothing to identify from out here.
                _indexLine = HelperMissing;
                Log.Once("card|search|" + ex.GetType().Name, "WARN", "card", $"{HelperMissing} :: {ex.Message}");
                r = SearchResults.Empty with { Query = raw, Note = HelperMissing };
            }
            catch (Exception ex)
            {
                Log.Once("search|query|" + ex.GetType().Name, "WARN", "search", $"search failed :: {ex.Message}");
                r = SearchResults.Empty with { Query = raw, Note = "search failed - see the log" };
            }

            Apply(r, gen);
        }

        /// <summary>Put an answer on the card. Shared by both halves of the pill so the two paths
        /// cannot drift on what "newest" means or on which state a new answer resets.</summary>
        private void Apply(SearchResults r, int gen)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (!_gate.IsNewest(gen)) return;   // a newer query is already running
                int before = _state.Rows.Count;
                var rows = SearchCardState.Filtered(r, _state.Filter);
                _state = _state with { Results = r, Rows = rows, Highlight = 0, Scroll = 0, Searching = false };
                if (rows.Count != before) CardResized?.Invoke();
                HighlightChanged();
                InvalidateVisual();
            });
        }

        /// <summary>An answer was dropped and nothing was painted. If this search is still the
        /// newest one, nothing else is coming either, so the indicator has to come down here -
        /// otherwise the card says "searching" until the next keystroke. When a newer search is
        /// already running this is a no-op and that search takes the indicator down instead.</summary>
        private void ClearSearchingIfNewest(int gen)
        {
            if (_life.IsCancellationRequested) return;   // the card is closing; nothing to say
            Dispatcher.UIThread.Post(() =>
            {
                if (!_gate.IsNewest(gen) || !_state.Searching) return;
                _state = _state with { Searching = false };
                InvalidateVisual();
            });
        }

        /// <summary>The card is up. Ask the helper once what it holds; the timer only ever reads
        /// the line this leaves behind.</summary>
        public void OnOpened() => _ = Task.Run(() => RefreshIndexLineAsync(_life.Token));

        private async Task RefreshIndexLineAsync(CancellationToken ct)
        {
            NameClient? c = null;
            try
            {
                c = await ClientAsync(ct).ConfigureAwait(false);
                if (c is null) return;                      // ClientAsync has already said why
                StatusReply s = await c.StatusAsync(ct).ConfigureAwait(false);
                _indexLine = IndexLineFormatter.IndexLineFor(s);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (_life.IsCancellationRequested) { }   // the card closed under it
            catch (Exception ex)
            {
                DropClient(c);   // the one that failed, if the failure was even on a connection
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

        // A photo decoded at preview size, the shell's thumbnail for everything Skia cannot read,
        // and for a video matched at a moment, that frame rather than the file's poster. Null is
        // an ordinary answer - the stage falls back to its no-art tile, which already handles it.
        // The caller runs this off the UI thread and keeps the result in PreviewCache.
        // The version test is what the decoder's own platform annotation asks for: this window is
        // declared for Windows generally, and the thumbnail and frame projections start at 19041.
        private static SKImage? DecodePreview(string path, ResultKind kind, int maxDim, double moment)
            => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
                   ? PreviewDecoder.Decode(path, kind, maxDim, moment)
                   : null;

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
