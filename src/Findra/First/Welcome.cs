using System;
using System.Collections.Generic;
using System.Globalization;

using SkiaSharp;

namespace Findra;

/// <summary>
/// The first-run screen once it has been answered: where Findra lives, what happens now, and who
/// made it.
///
/// <para>The answer builds the capsule, the hotkey and the tray icon, and none of them had been
/// on the display before it - so the moment somebody presses "Get these" is the moment they are
/// looking for a window that has just turned into three things they have never seen. This page is
/// the answer to "where did it go", given while the screen still has their attention, and the
/// download it used to be about is one section of it rather than the whole of it.</para>
///
/// <para>Every string is here and none is in the painter, for the reason <see cref="FirstRun"/>
/// holds its own: the tests measure what is drawn.</para>
/// </summary>
public static class Welcome
{
    public const string Title = "You're all set";

    public static string Subtitle(FirstRunState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (FirstRun.Asks(s)) return "Findra is running. One thing is left to decide, below.";
        return s.Stage == FirstRunStage.Downloading
            ? "Findra is running. Here is where to find it while your downloads finish."
            : "Findra is running. Here is where to find it.";
    }

    public const string WhereHeading = "Where Findra lives";
    public const string NowHeading = "What happens now";
    public const string AboutHeading = "About Findra";

    public const string CapsuleTitle = "The capsule on your desktop";
    public const string CapsuleNote = "Click it to search. Drag it wherever it suits you.";

    /// <summary>The chord that actually REGISTERED, which is not always the one asked for: the
    /// hotkey walks a fallback chain and takes the first that Windows gives it. Null is a machine
    /// where none of them would register, and saying a shortcut that does nothing would be worse
    /// than saying there is none.</summary>
    public static string HotkeyTitle(string? chord) =>
        chord is null ? "No shortcut is set yet" : chord + " searches from anywhere";

    public static string HotkeyNote(string? chord) =>
        chord is null
            ? "Every shortcut Findra tried was already taken. Settings, under Opening it, can set one."
            : "In any app, without reaching for the mouse. Settings, under Opening it, can change it.";

    public const string TrayTitle = "The tray, by the clock";

    /// <summary>Windows 11 puts a new tray icon in the overflow rather than on the taskbar, so the
    /// first place somebody looks is the one place it is not. Said here because nothing else
    /// will say it.</summary>
    public const string TrayNote = "Click it to search; right-click it for Settings or Quit. It may be under the ^ arrow.";

    /// <summary>
    /// What happens to the inside of files, under the downloads.
    ///
    /// <para>Two shapes, because the third belongs to the question: a screen that took reading and
    /// has finished downloading ASKS (<see cref="FirstRun.Asks"/>), and the question replaces this
    /// sentence rather than following it.</para>
    /// </summary>
    public static string Reading(FirstRunState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.ContentOn
            ? "Names are searchable now. When the downloads finish, Findra asks here before it " +
              "starts reading inside your files."
            : "Names are searchable now. Findra will not look inside your files until you turn " +
              "that on in Settings, under Content.";
    }

    /// <summary>Who made it, and the privacy promise in one sentence - which has two versions,
    /// because the one request Findra makes by itself is the update check and that is a switch
    /// on the page before this one.</summary>
    public static string About(FirstRunState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return "I'm Liraz Amir, and I make Findra on my own, as blakazulu. It is free and open " +
               "source, with no account, no analytics and no company behind it. " +
               (s.CheckUpdates
                   ? "What you search for and what it reads stay on this PC; the one request it " +
                     "makes by itself is a daily check for a newer version."
                   : "What you search for and what it reads stay on this PC, and it makes no " +
                     "requests by itself.");
    }

    /// <summary>The two links, in the order they are drawn. Opened in the browser when pressed;
    /// the page itself fetches nothing.</summary>
    public static IReadOnlyList<(string Label, string Url)> Links { get; } =
    [
        ("findra-search.netlify.app", "https://findra-search.netlify.app/"),
        ("github.com/blakazulu/findra", "https://github.com/blakazulu/findra"),
    ];

    public const string SettingsLabel = "Open settings";

    /// <summary>The status line under the bars: <see cref="FirstRun.Summary"/>, which already
    /// says how many are done, which one is moving and what went wrong. Empty when there was
    /// nothing to fetch, where "0 of 0 done" would be arithmetic rather than news.</summary>
    public static string Status(FirstRunState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return s.Downloads.Count == 0 && s.Problem.Length == 0 ? "" : FirstRun.Summary(s);
    }

    /// <summary>A bar's percentage, whole and floored, so a bar is never called 100% while it
    /// still has bytes to go.</summary>
    public static string Percent(CapabilityProgress p) =>
        (p.Total <= 0 ? 0 : (int)Math.Min(100, p.Got * 100 / p.Total)).ToString(CultureInfo.InvariantCulture) + "%";
}

/// <summary>
/// Where everything on the answered page sits. A pure function of the state, like
/// <see cref="FirstRunLayout"/>, so the painter and the hit test cannot measure different pages.
/// </summary>
public static class WelcomeLayout
{
    private const float Inset = FirstRunLayout.Pad + 12f;
    private static float Right => FirstRunLayout.Width - Inset;

    public const float WhereTop = 96f;
    public const float PlacesTop = 122f;
    public const float PlaceH = 50f;
    public const int Places = 3;

    /// <summary>The picture column, wide enough for the widest chord the fallback chain can land
    /// on ("Ctrl+Shift+Space" as three keycaps).</summary>
    public const float IconW = 132f;

    public const float BarRowH = 28f;
    public const float BarW = 240f;
    public const float PercentW = 44f;

    /// <summary>Two lines of the lead, which is what the longest status - a failure naming a
    /// host - needs.</summary>
    public static readonly float StatusH = Parts.LeadHeight(2) + 2f;

    public static readonly float ReadingH = Parts.NoteHeight(2) + 4f;
    public const float AskH = 100f;
    public static readonly float AboutTextH = Parts.NoteHeight(3) + 2f;

    public const float LinkH = 26f;
    public const float LinkW = 214f;
    public const float LinkGap = 12f;
    public const float LinkPad = 8f;

    public static float TextLeft => Inset + IconW + 16f;

    public static SKRect PlaceRect(int i) =>
        new(Inset, PlacesTop + i * PlaceH, Right, PlacesTop + i * PlaceH + PlaceH - 6f);

    public static SKRect IconRect(int i)
    {
        SKRect r = PlaceRect(i);
        return new SKRect(r.Left, r.MidY - 15f, r.Left + IconW, r.MidY + 15f);
    }

    public static float NowTop => PlacesTop + Places * PlaceH + 20f;
    public static float BarsTop => NowTop + 26f;

    public static SKRect BarRowRect(int i) =>
        new(Inset, BarsTop + i * BarRowH, Right, BarsTop + (i + 1) * BarRowH);

    public static bool ShowsStatus(FirstRunState s) => Welcome.Status(s).Length > 0;

    public static SKRect StatusRect(FirstRunState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        float top = BarsTop + s.Downloads.Count * BarRowH + (s.Downloads.Count > 0 ? 6f : 0f);
        return new SKRect(Inset, top, Right, top + (ShowsStatus(s) ? StatusH : 0f));
    }

    /// <summary>The reading sentence, or the last question in its place.</summary>
    public static SKRect NextRect(FirstRunState s)
    {
        float top = StatusRect(s).Bottom;
        return new SKRect(Inset, top, Right, top + (FirstRun.Asks(s) ? AskH : ReadingH));
    }

    public static float AboutTop(FirstRunState s) => NextRect(s).Bottom + 20f;

    public static SKRect AboutTextRect(FirstRunState s)
    {
        float top = AboutTop(s) + 22f;
        return new SKRect(Inset, top, Right, top + AboutTextH);
    }

    public static SKRect LinkRect(int i, FirstRunState s)
    {
        // Pushed out by the text's own padding, so the words line up with the column above
        // and the hover fill is what reaches past it.
        float top = AboutTextRect(s).Bottom + 4f;
        float x = Inset - LinkPad + i * (LinkW + LinkGap);
        return new SKRect(x, top, x + LinkW, top + LinkH);
    }

    public static float Height(FirstRunState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return LinkRect(0, s).Bottom + 28f + FirstRunLayout.ButtonH + 20f;
    }

    /// <summary>"Open settings", on the left, apart from the answer on the right.</summary>
    public static SKRect SettingsRect(float height) =>
        new(Inset, height - FirstRunLayout.ButtonH - 20f, Inset + FirstRunLayout.ButtonW, height - 20f);

    /// <summary>
    /// The answered page's controls: "Open settings", the two links, and the way out - with
    /// "Later" beside it only while the last question is on the page.
    ///
    /// <para>Live while the download runs as well as after it. The old second act answered
    /// nothing until the last byte, because its only button would have been a second way to
    /// close; this page's buttons each do something the download does not need, and "Done"
    /// leaving the download running in the tray is what the status line has always said.</para>
    /// </summary>
    public static FirstRunHit HitTest(float x, float y, FirstRunState s)
    {
        ArgumentNullException.ThrowIfNull(s);
        float h = Height(s);
        if (x < 0 || x > FirstRunLayout.Width || y < 0 || y > h) return new FirstRunHit(FirstRunTarget.None, -1);

        if (SettingsRect(h).Contains(x, y)) return new FirstRunHit(FirstRunTarget.Settings, -1);
        for (int i = 0; i < Welcome.Links.Count; i++)
            if (LinkRect(i, s).Contains(x, y)) return new FirstRunHit(FirstRunTarget.Link, i);
        if (FirstRun.Asks(s) && FirstRunLayout.ButtonRect(0, h).Contains(x, y))
            return new FirstRunHit(FirstRunTarget.NotNow, -1);
        if (FirstRunLayout.ButtonRect(1, h).Contains(x, y)) return new FirstRunHit(FirstRunTarget.Go, -1);
        return new FirstRunHit(FirstRunTarget.None, -1);
    }
}

/// <summary>Draws the answered page on the parts every other surface uses. Knows no policy:
/// every string comes out of <see cref="Welcome"/> or <see cref="FirstRun"/>.</summary>
public static class WelcomePainter
{
    private const float TitleSize = 20f;
    private const float KeySize = Parts.NoteSize;
    public const float KeyPad = 14f;
    public const float KeyGap = 4f;

    /// <summary>The tray icon exactly as the taskbar gets it, decoded once. The mark's geometry
    /// lives in <see cref="TrayIconFactory"/> and nowhere else, so the picture of the tray on this
    /// page cannot drift from the icon it is a picture of.</summary>
    private static readonly Lazy<SKImage?> TrayMark = new(() =>
    {
        try
        {
            using SKData png = TrayIconFactory.Render(Palette.DefaultDark);
            return SKImage.FromEncodedData(png);
        }
        catch (Exception ex)
        {
            Log.Warn("firstrun", "the tray mark could not be drawn for the welcome page: " + ex.Message);
            return null;
        }
    });

    public static void Paint(SKCanvas canvas, FirstRunState s, Derived d, SKTypeface face)
    {
        ArgumentNullException.ThrowIfNull(canvas);
        ArgumentNullException.ThrowIfNull(s);
        ArgumentNullException.ThrowIfNull(d);

        float left = WelcomeLayout.PlaceRect(0).Left;
        float right = WelcomeLayout.PlaceRect(0).Right;

        CardText.Draw(canvas, Welcome.Title, left, 44, TitleSize, face, d.Ink);
        CardText.Draw(canvas, Welcome.Subtitle(s), left, 70, Parts.LabelSize, face, d.Fade(170));

        // ---- where Findra lives
        Parts.Header(canvas, Welcome.WhereHeading, new SKRect(left, WelcomeLayout.WhereTop, right, WelcomeLayout.PlacesTop), d, face);
        Place(canvas, 0, Welcome.CapsuleTitle, Welcome.CapsuleNote, d, face);
        Capsule(canvas, WelcomeLayout.IconRect(0), d, face);
        Place(canvas, 1, Welcome.HotkeyTitle(s.Hotkey), Welcome.HotkeyNote(s.Hotkey), d, face);
        Keys(canvas, WelcomeLayout.IconRect(1), s.Hotkey, d, face);
        Place(canvas, 2, Welcome.TrayTitle, Welcome.TrayNote, d, face);
        Tray(canvas, WelcomeLayout.IconRect(2), d, face);

        // ---- what happens now
        Parts.Header(canvas, Welcome.NowHeading,
                     new SKRect(left, WelcomeLayout.NowTop, right, WelcomeLayout.BarsTop), d, face);
        for (int i = 0; i < s.Downloads.Count; i++)
        {
            CapabilityProgress p = s.Downloads[i];
            SKRect r = WelcomeLayout.BarRowRect(i);
            float baseline = r.MidY + Parts.LabelSize * 0.36f;
            CardText.Draw(canvas, Capabilities.Title(p.Capability), r.Left, baseline, Parts.LabelSize, face, d.Ink);
            float barRight = r.Right - WelcomeLayout.PercentW;
            Parts.Bar(canvas, new SKRect(barRight - WelcomeLayout.BarW, r.MidY - 3.5f, barRight, r.MidY + 3.5f),
                      p.Total > 0 ? (float)((double)p.Got / p.Total) : 0f, d);
            CardText.DrawRight(canvas, Welcome.Percent(p), r.Right, baseline, Parts.LabelSize, face, d.Ink);
        }

        if (WelcomeLayout.ShowsStatus(s))
            Parts.Lead(canvas, Welcome.Status(s), WelcomeLayout.StatusRect(s), d, face);

        SKRect next = WelcomeLayout.NextRect(s);
        bool asking = FirstRun.Asks(s);
        if (asking)
        {
            // The last question, on the terms it always had: nothing reads until it is answered,
            // and the warning that is the reason for asking sits under it.
            Rule(canvas, next.Left, next.Right, next.Top + 6f, d);
            CardText.Draw(canvas, FirstRun.AskTitle, next.Left, next.Top + 34f, Parts.LeadSize, face, d.Ink);
            Parts.Note(canvas, FirstRun.AskNote, new SKRect(next.Left, next.Top + 42f, next.Right, next.Bottom), d, face);
        }
        else
        {
            Parts.Note(canvas, Welcome.Reading(s), next, d, face);
        }

        // ---- about
        float aboutTop = WelcomeLayout.AboutTop(s);
        Rule(canvas, left, right, aboutTop - 10f, d);
        Parts.Header(canvas, Welcome.AboutHeading, new SKRect(left, aboutTop, right, aboutTop + 20f), d, face);
        Parts.Note(canvas, Welcome.About(s), WelcomeLayout.AboutTextRect(s), d, face);
        for (int i = 0; i < Welcome.Links.Count; i++)
            Link(canvas, WelcomeLayout.LinkRect(i, s), Welcome.Links[i].Label,
                 s.HoverTarget == FirstRunTarget.Link && s.HoverIndex == i, d, face);

        // ---- the way out
        float h = WelcomeLayout.Height(s);
        Parts.Pill(canvas, WelcomeLayout.SettingsRect(h), Welcome.SettingsLabel,
                   chosen: false, hovered: s.HoverTarget == FirstRunTarget.Settings, d, face);
        if (asking)
            Parts.Pill(canvas, FirstRunLayout.ButtonRect(0, h), FirstRun.LaterLabel,
                       chosen: false, hovered: s.HoverTarget == FirstRunTarget.NotNow, d, face);
        Parts.Pill(canvas, FirstRunLayout.ButtonRect(1, h), FirstRun.GoLabel(s),
                   chosen: true, hovered: s.HoverTarget == FirstRunTarget.Go, d, face);
    }

    private static void Place(SKCanvas canvas, int i, string title, string note, Derived d, SKTypeface face)
    {
        SKRect r = WelcomeLayout.PlaceRect(i);
        float x = WelcomeLayout.TextLeft;
        CardText.Draw(canvas, title, x, r.Top + 18f, Parts.LabelSize, face, d.Ink);
        CardText.Draw(canvas, note, x, r.Top + 36f, Parts.NoteSize, face, d.Fade(150));
    }

    private static void Rule(SKCanvas canvas, float left, float right, float y, Derived d)
    {
        using var p = new SKPaint { Color = d.Edge, IsAntialias = false, StrokeWidth = 1 };
        canvas.DrawLine(left, y, right, y, p);
    }

    /// <summary>The capsule, small: its field, its lens and its placeholder, in the palette on
    /// screen - the same object the person is about to find on their desktop.</summary>
    private static void Capsule(SKCanvas canvas, SKRect box, Derived d, SKTypeface face)
    {
        var r = new SKRect(box.Left, box.MidY - 13f, box.Left + 120f, box.MidY + 13f);
        var rr = new SKRoundRect(r, r.Height / 2f);
        using (var fill = new SKPaint { Color = d.Tile, IsAntialias = true }) canvas.DrawRoundRect(rr, fill);
        using (var edge = new SKPaint { Color = d.Accent.WithAlpha(90), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.2f })
            canvas.DrawRoundRect(rr, edge);
        using (var lens = new SKPaint
        { Color = d.Accent, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.8f, StrokeCap = SKStrokeCap.Round })
        {
            float cx = r.Left + 15f, cy = r.MidY - 1f;
            canvas.DrawCircle(cx, cy, 4.8f, lens);
            canvas.DrawLine(cx + 3.6f, cy + 3.6f, cx + 7f, cy + 7f, lens);
        }
        CardText.Draw(canvas, "Search", r.Left + 30f, r.MidY + KeySize * 0.36f, KeySize, face, d.Fade(150));
    }

    /// <summary>The chord as keycaps, one per key, left to right as it is pressed.</summary>
    private static void Keys(SKCanvas canvas, SKRect box, string? chord, Derived d, SKTypeface face)
    {
        string[] keys = chord is null ? ["?"] : chord.Split('+', StringSplitOptions.RemoveEmptyEntries);
        float x = box.Left;
        foreach (string key in keys)
        {
            float w = CardText.Measure(key, face, KeySize) + KeyPad;
            var r = new SKRect(x, box.MidY - 12f, x + w, box.MidY + 12f);
            var rr = new SKRoundRect(r, 6f);
            using (var fill = new SKPaint { Color = d.Chip, IsAntialias = true }) canvas.DrawRoundRect(rr, fill);
            using (var edge = new SKPaint { Color = d.Edge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f })
                canvas.DrawRoundRect(rr, edge);
            // A heavier edge along the bottom, which is what makes a rounded rectangle read as a key.
            using (var lip = new SKPaint { Color = d.Fade(90), IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1.4f })
                canvas.DrawLine(r.Left + 4f, r.Bottom - 0.5f, r.Right - 4f, r.Bottom - 0.5f, lip);
            CardText.DrawCentred(canvas, key, r.MidX, r.MidY + KeySize * 0.36f, KeySize, face, d.Ink);
            x = r.Right + KeyGap;
        }
    }

    /// <summary>A slice of taskbar: the overflow arrow and the mark beside it, which is exactly
    /// where Windows 11 puts a new tray icon.</summary>
    private static void Tray(SKCanvas canvas, SKRect box, Derived d, SKTypeface face)
    {
        var r = new SKRect(box.Left, box.MidY - 13f, box.Left + 86f, box.MidY + 13f);
        var rr = new SKRoundRect(r, 7f);
        using (var fill = new SKPaint { Color = d.Tile, IsAntialias = true }) canvas.DrawRoundRect(rr, fill);
        using (var edge = new SKPaint { Color = d.Edge, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1f })
            canvas.DrawRoundRect(rr, edge);
        CardText.DrawCentred(canvas, "^", r.Left + 16f, r.MidY + KeySize * 0.5f, KeySize, face, d.Fade(170));

        if (TrayMark.Value is { } mark)
        {
            var at = new SKRect(r.Left + 32f, r.MidY - 9f, r.Left + 50f, r.MidY + 9f);
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawImage(mark, at, new SKSamplingOptions(SKCubicResampler.Mitchell), paint);
        }

        // Two quiet dots for the icons beside it, so the mark reads as one of several rather
        // than as a logo in a box.
        using var other = new SKPaint { Color = d.Fade(70), IsAntialias = true };
        canvas.DrawCircle(r.Left + 62f, r.MidY, 3.5f, other);
        canvas.DrawCircle(r.Left + 74f, r.MidY, 3.5f, other);
    }

    /// <summary>A link: full ink, underlined, with the row's hover fill. Not the accent - as TEXT
    /// the accent is the weakest ink on a light palette.</summary>
    private static void Link(SKCanvas canvas, SKRect r, string label, bool hovered, Derived d, SKTypeface face)
    {
        if (hovered)
            using (var fill = new SKPaint { Color = d.RowHover, IsAntialias = true })
                canvas.DrawRoundRect(new SKRoundRect(r, 6f), fill);
        float x = r.Left + WelcomeLayout.LinkPad;
        float baseline = r.MidY + Parts.LabelSize * 0.36f;
        CardText.Draw(canvas, label, x, baseline, Parts.LabelSize, face, d.Ink);
        using var line = new SKPaint { Color = d.Fade(hovered ? (byte)220 : (byte)130), IsAntialias = true, StrokeWidth = 1f };
        canvas.DrawLine(x, baseline + 3f, x + CardText.Measure(label, face, Parts.LabelSize), baseline + 3f, line);
    }
}
