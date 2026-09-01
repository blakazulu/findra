using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using Avalonia.Threading;
using Findra.Startup;

namespace Findra;

// Findra has no main window. It lives in the tray with a capsule on the desktop, so the
// desktop lifetime's default ShutdownMode (OnLastWindowClose) is wrong here - it would quit
// the whole application the moment the search card, which is not the "main window", is
// dismissed. OnExplicitShutdown means the process only exits when something asks it to
// (tray "Quit", or a future Shutdown() call), never as a side effect of a window closing.
public sealed class App : Application
{
    private Shell? _shell;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            // Nothing here is allowed to throw out of framework initialisation: a failure inside
            // the shell has to leave a log line and a running process, not a launch that dies
            // before anything is on screen to explain itself.
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    _shell = new Shell(this, desktop);
                    _shell.Start();
                }
                catch (Exception ex) { Log.Error("startup", "the shell could not start", ex); }
            }
        }

        base.OnFrameworkInitializationCompleted();
    }
}

/// <summary>Where the card lands when it is opened from the hotkey rather than from the capsule:
/// centred on the working area, about a third of the way down, which is where the eye already is.
/// Pure, so the arithmetic has a test even though the window it positions cannot have one.</summary>
public static class CardPlacement
{
    public const double FromTop = 0.28;

    public static PixelPoint Centred(PixelRect workingArea, int width, int height)
    {
        int x = workingArea.X + (workingArea.Width - width) / 2;
        int y = workingArea.Y + (int)Math.Round(workingArea.Height * FromTop);
        return CapsulePlacement.Clamp(new PixelPoint(x, y), workingArea, width, height);
    }
}

/// <summary>
/// Everything that has to exist for Findra to be running: the settings, the palette in force, the
/// elevated name helper, the capsule, the global hotkey, the tray, and the once-a-day update check.
///
/// Every stage is wrapped on its own. A machine with no shell to hold a tray icon, or one where no
/// hotkey combination is free, is a degraded Findra that still starts and still says what it lost.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed class Shell
{
    /// <summary>The zoom the capsule and the card are painted at. This is NOT the monitor's DPI:
    /// Avalonia's drawing context already carries the render scaling, so multiplying by it here
    /// would apply it twice and draw a capsule half again too big. A user-facing zoom setting
    /// lands with the settings surface; until then it is one.</summary>
    private const double Zoom = 1.0;

    /// <summary>Long enough that the check never competes with the first frame. It is not on a
    /// keystroke, not on a query, and it blocks nothing whatever it returns.</summary>
    private static readonly TimeSpan UpdateCheckDelay = TimeSpan.FromSeconds(2);

    // One client for the process. The only request Findra ever makes off this machine goes
    // through it (spec 9b).
    private static readonly HttpClient Http = UpdateCheck.CreateClient();

    private readonly Application _app;
    private readonly IClassicDesktopStyleApplicationLifetime _desktop;

    private Config _config = Config.Default;
    private Palette _palette = Palette.DefaultDark;

    private CapsuleWindow? _capsule;
    private HotkeyHost? _hotkey;
    private TrayIcon? _tray;
    private NativeMenuItem? _showCapsuleItem;
    private CardWindow? _card;

    private UpdateState _update = UpdateState.NotDue;
    private string? _latest;

    public Shell(Application app, IClassicDesktopStyleApplicationLifetime desktop)
    {
        _app = app;
        _desktop = desktop;
    }

    public void Start()
    {
        Stage("settings", () =>
        {
            _config = Config.LoadFromDisk();
            IReadOnlyList<Palette> palettes = PaletteStore.LoadFromDisk();
            // Follow Windows is read once, here. Switching the palette live when Windows flips
            // between light and dark mid-session lands with the settings surface in a later plan.
            _palette = Theme.Resolve(_config, Theme.WindowsIsLight(), palettes);
            Log.Info("startup", $"palette '{_palette.Name}' ({(_palette.Light ? "light" : "dark")}), mode {_config.Mode}");
        });

        // EnsureRunning asks the scheduler to start the helper and then waits up to five seconds
        // for the pipe to answer. On the UI thread that is five seconds of nothing on screen.
        Stage("names helper", () => _ = Task.Run(() =>
        {
            bool up = HelperTask.EnsureRunning();
            Log.Info("startup", up
                ? "the names helper is answering"
                : "the names helper is not answering; name search will be empty until it is registered");
        }));

        Stage("hotkey", () =>
        {
            var host = new HotkeyHost();
            host.Pressed += () => Dispatcher.UIThread.Post(OpenFromHotkey);
            host.Start(HotkeyChain.Build(_config.Hotkey, Hotkey.DefaultChain));
            _hotkey = host;
            UiStatus.Write(Environment.ProcessId, host.Landed);
        });

        Stage("capsule", () =>
        {
            if (_config.ShowCapsule) CreateCapsule();
            else Log.Info("app", "the capsule is turned off; the hotkey and the tray open the card");
        });

        Stage("tray", CreateTray);

        Stage("update check", () => _ = Task.Run(async () =>
        {
            await Task.Delay(UpdateCheckDelay).ConfigureAwait(false);
            await RunUpdateCheck(force: false).ConfigureAwait(false);
        }));
    }

    private static void Stage(string what, Action body)
    {
        try { body(); }
        catch (Exception ex) { Log.Error("startup", $"the {what} stage failed", ex); }
    }

    // ---- screens ---------------------------------------------------------------------------------

    // The hotkey host exists whether or not the capsule does, so it is the reliable monitor source.
    private Screens? ScreenSource() => _hotkey?.Screens ?? _capsule?.Screens;

    private static PixelRect Fallback => new(0, 0, 1920, 1080);

    // ---- the capsule -----------------------------------------------------------------------------

    private void CreateCapsule()
    {
        Screens? screens = ScreenSource();
        Screen? primary = screens?.Primary ?? screens?.All.FirstOrDefault();

        var saved = new PixelPoint((int)Math.Round(_config.CapsuleX), (int)Math.Round(_config.CapsuleY));
        Screen? on = screens?.ScreenFromPoint(saved);
        double scaling = (on ?? primary)?.Scaling ?? 1.0;

        int w = (int)Math.Round(CapsuleLayout.Width * Zoom * scaling);
        int h = (int)Math.Round(CapsuleLayout.Height * Zoom * scaling);

        IReadOnlyList<PixelRect> all = screens?.All.Select(s => s.Bounds).ToArray() ?? Array.Empty<PixelRect>();
        bool everPlaced = _config.CapsuleX != 0 || _config.CapsuleY != 0;

        PixelPoint at;
        if (everPlaced && all.Count > 0 && CapsulePlacement.IsOnAnyScreen(new PixelRect(saved.X, saved.Y, w, h), all))
        {
            at = saved;
        }
        else
        {
            at = CapsulePlacement.BottomCentre(primary?.WorkingArea ?? Fallback, w, h);
            Log.Info("app", everPlaced
                ? $"the saved capsule position ({saved.X},{saved.Y}) is not on any monitor; it opens at ({at.X},{at.Y}) on the primary screen"
                : $"the capsule has no saved position; it opens at ({at.X},{at.Y}) on the primary screen");
        }

        var capsule = new CapsuleWindow(_palette, Zoom) { Position = at };
        capsule.Clicked += OpenFromCapsule;
        capsule.Moved += SaveCapsulePosition;
        capsule.Show();
        capsule.Position = at;   // Show is entitled to place a window itself; this is the last word
        _capsule = capsule;
        Log.Info("app", $"the capsule is on the desktop at ({at.X},{at.Y})");
    }

    private void SaveCapsulePosition(PixelPoint at)
    {
        _config = _config with { CapsuleX = at.X, CapsuleY = at.Y };
        _config.Save();
    }

    // ---- the card --------------------------------------------------------------------------------

    private CardWindow NewCard()
    {
        var card = new CardWindow(_palette, Zoom);
        card.Closed += (_, _) => { if (ReferenceEquals(_card, card)) _card = null; };
        _card = card;
        return card;
    }

    private void CloseCard()
    {
        CardWindow? card = _card;
        _card = null;
        try { card?.Close(); } catch (Exception ex) { Log.Warn("app", "the card would not close: " + ex.Message); }
    }

    /// <summary>Opening from the capsule dims the capsule's monitor, and the card unfolds so its
    /// field lands exactly where the capsule's bar was.</summary>
    private void OpenFromCapsule()
    {
        // A click on the capsule deactivated the card BEFORE this handler ran, so without this a
        // click that dismissed the card would immediately reopen it.
        if (CardWindow.JustClosed) return;
        if (_card is not null) { CloseCard(); return; }
        if (_capsule is null) return;

        try
        {
            Screens? screens = _capsule.Screens;
            Screen? s = screens?.ScreenFromWindow(_capsule) ?? screens?.Primary;
            CardWindow card = NewCard();
            if (s is not null) card.ShowDim(s.Bounds, s.Scaling);
            card.PlaceOver(_capsule.Position, Zoom, CapsuleWindow.BarRect, s?.Bounds ?? Fallback);
            card.Show();
        }
        catch (Exception ex) { Log.Error("app", "the card could not open from the capsule", ex); _card = null; }
    }

    /// <summary>Opening from the hotkey dims the monitor under the CURSOR, which is not
    /// necessarily the one the capsule rests on. Two open paths, two dim behaviours.</summary>
    private void OpenFromHotkey()
    {
        if (_card is not null) { CloseCard(); return; }

        try
        {
            Screens? screens = ScreenSource();
            Screen? s = screens?.ScreenFromPoint(HotkeyHost.CursorPosition()) ?? screens?.Primary;
            double scaling = s?.Scaling ?? 1.0;
            PixelRect work = s?.WorkingArea ?? Fallback;

            CardWindow card = NewCard();
            if (s is not null) card.ShowDim(s.Bounds, s.Scaling);
            int w = (int)Math.Round(SearchCardLayout.Width * Zoom * scaling);
            int h = (int)Math.Round(SearchCardLayout.Height(0, false) * Zoom * scaling);
            card.Position = CardPlacement.Centred(work, w, h);
            card.Show();
        }
        catch (Exception ex) { Log.Error("app", "the card could not open", ex); _card = null; }
    }

    // ---- the tray --------------------------------------------------------------------------------

    private void CreateTray()
    {
        var menu = new NativeMenu();

        var search = new NativeMenuItem("Search");
        search.Click += (_, _) => OpenFromHotkey();
        menu.Items.Add(search);

        _showCapsuleItem = new NativeMenuItem("Show capsule")
        {
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = _config.ShowCapsule,
        };
        _showCapsuleItem.Click += (_, _) => ToggleCapsule();
        menu.Items.Add(_showCapsuleItem);

        // Present, so the shape of the menu is visible, and disabled, because the settings surface
        // is a later plan. A menu that grows later is worse than one that shows what is coming.
        menu.Items.Add(new NativeMenuItem("Settings") { IsEnabled = false });
        menu.Items.Add(new NativeMenuItemSeparator());

        var check = new NativeMenuItem("Check for updates");
        check.Click += (_, _) => _ = RunUpdateCheck(force: true);
        menu.Items.Add(check);

        var quit = new NativeMenuItem("Quit");
        quit.Click += (_, _) => Quit();
        menu.Items.Add(quit);

        var icon = new TrayIcon { Menu = menu, ToolTipText = Tooltip(), IsVisible = true };
        if (TrayIconFactory.Draw(_palette) is { } drawn) icon.Icon = drawn;
        icon.Clicked += (_, _) => OpenFromHotkey();

        TrayIcon.SetIcons(_app, new TrayIcons { icon });
        _tray = icon;
        Log.Info("app", "the tray icon is up");
    }

    private string Tooltip() => TrayText.Tooltip(Log.Version, _hotkey?.Landed, _update, _latest);

    private void RefreshTooltip()
    {
        if (_tray is not null) _tray.ToolTipText = Tooltip();
    }

    private void ToggleCapsule()
    {
        bool show = !_config.ShowCapsule;
        _config = _config with { ShowCapsule = show };
        _config.Save();
        if (_showCapsuleItem is not null) _showCapsuleItem.IsChecked = show;

        if (show)
        {
            if (_capsule is null) Stage("capsule", CreateCapsule);
        }
        else
        {
            CapsuleWindow? capsule = _capsule;
            _capsule = null;
            try { capsule?.Close(); } catch (Exception ex) { Log.Warn("app", "the capsule would not close: " + ex.Message); }
            Log.Info("app", "the capsule is turned off; the hotkey and the tray open the card");
        }
    }

    private void Quit()
    {
        Log.Info("app", "quitting");
        UiStatus.Clear();
        try { _hotkey?.Dispose(); } catch { }
        _hotkey = null;

        CloseCard();
        try { _capsule?.Close(); } catch { }
        _capsule = null;
        try { _tray?.Dispose(); } catch { }
        _tray = null;

        Log.Info("app", Log.SessionSummary());
        Log.Flush();
        _desktop.Shutdown();
    }

    // ---- the update check ------------------------------------------------------------------------

    /// <summary>The tray's "Check for updates" forces the request past the 24 hour gate; startup
    /// does not. Either way the result only ever becomes a tooltip line and a log line - Findra
    /// downloads nothing and installs nothing.</summary>
    private async Task RunUpdateCheck(bool force)
    {
        try
        {
            UpdateResult result = await UpdateCheck.CheckAsync(
                _config,
                ct => UpdateCheck.FetchLatestTagAsync(Http, Log.Version, ct),
                DateTime.UtcNow, CancellationToken.None, force).ConfigureAwait(false);

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Only the timestamp is taken back. The config may have moved on since the request
                // started - a capsule dragged, the capsule toggled off - and writing the whole
                // record back would undo it.
                if (result.Config.LastUpdateCheck != _config.LastUpdateCheck)
                {
                    _config = _config with { LastUpdateCheck = result.Config.LastUpdateCheck };
                    _config.Save();
                }

                _update = result.State;
                _latest = result.Latest;
                RefreshTooltip();
                if (result.Advice is { } advice) Log.Info("startup", advice);
            });
        }
        catch (Exception ex)
        {
            Log.Warn("startup", "the update check could not run: " + ex.Message);
        }
    }
}
