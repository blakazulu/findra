using System;
using System.IO;
using Avalonia.Controls;
using SkiaSharp;

namespace Findra;

/// <summary>
/// The tray's words. Pure string composition, kept out of the tray plumbing so the one thing a
/// user reads when something went wrong - "no hotkey could be registered" - has a test.
/// </summary>
public static class TrayText
{
    /// <summary>The tooltip: the version, then the hotkey, then the update state when it is
    /// known. Windows truncates a tray tooltip, so nothing decorative goes in here.</summary>
    public static string Tooltip(string version, string? hotkey, UpdateState update, string? latest)
    {
        var lines = new List<string>(3) { "Findra " + version };
        lines.Add(hotkey is null ? "No hotkey could be registered" : "Hotkey: " + hotkey);

        string? state = update switch
        {
            UpdateState.Available when !string.IsNullOrWhiteSpace(latest) => "Update available: " + latest,
            UpdateState.Current => "Up to date",
            _ => null,
        };
        if (state is not null) lines.Add(state);

        return string.Join(Environment.NewLine, lines);
    }
}

/// <summary>
/// The tray icon: Findra's mark, drawn at runtime rather than shipped as a binary asset, in
/// whichever palette is in force - so the icon in the tray belongs to the same look as the card,
/// and a new palette needs no new file.
///
/// <para>It is the same mark the taskbar shows, from the same numbers, but not the same picture.
/// The taskbar reads a fixed <c>.ico</c> off the executable, so that one is orange on the Mond
/// ground whatever theme is running, and it sits on a rounded plate. This one has no plate: a
/// tray icon is composited straight onto a taskbar whose colour belongs to Windows, and a plate
/// there is a dark square somebody did not ask for. The glyph fills the box instead.</para>
///
/// <para>The design-unit constants below are the ones in <c>build/Make-Icon.mjs</c>, which
/// generates <c>assets/icon/findra.ico</c>. Two copies of a number is two marks waiting to
/// happen, so <c>IconTests</c> holds them to each other.</para>
/// </summary>
public static class TrayIconFactory
{
    public const int Size = 32;

    /// <summary>The mark's own bounding box in the 256-unit design space: the lens at
    /// (110, 108) r 64 and the handle's round cap out at (200, 198) with a 30-unit bar, so
    /// x and y both run 46 to 215. Not the 256 square - there is no plate here, and scaling to
    /// the square would leave four empty pixels down two sides, which reads as a tray icon
    /// somebody drew smaller than everyone else's.</summary>
    public static readonly SKRect Bounds = new(46f, 44f, 215f, 213f);

    /// <summary>Returns null rather than throwing. A machine with no shell to put an icon in is a
    /// degraded state, not a reason to fail a launch.</summary>
    public static WindowIcon? Draw(Palette palette)
    {
        try
        {
            using SKData data = Render(palette);
            return new WindowIcon(new MemoryStream(data.ToArray()));
        }
        catch (Exception ex)
        {
            Log.Warn("app", "the tray icon could not be drawn: " + ex.Message);
            return null;
        }
    }

    /// <summary>The pixels, as PNG, with no Avalonia type anywhere near them.
    ///
    /// <para>Split out from <see cref="Draw"/> so the drawing has a test. A <c>WindowIcon</c>
    /// wants a running Avalonia, and a test that cannot construct one is a test that would have
    /// had to read this method's source instead - which cannot fail for the defect worth
    /// catching, that the lens's slot gets filled in with a colour rather than left as a hole.
    /// <c>IconTests</c> decodes what this returns and looks at that pixel.</para></summary>
    public static SKData Render(Palette palette)
    {
        var info = new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul);
        using SKSurface surface = SKSurface.Create(info);
        SKCanvas canvas = surface.Canvas;
        canvas.Clear(SKColors.Transparent);

        // Design units from here down, so the numbers below read the same as the ones the icon
        // generator uses. One pixel of margin, because a round cap that lands exactly on the
        // edge loses its outer half to the antialiasing.
        const float pad = 1f;
        canvas.Translate(pad, pad);
        canvas.Scale((Size - pad * 2f) / Bounds.Width);
        canvas.Translate(-Bounds.Left, -Bounds.Top);

        using var accent = new SKPaint { Color = palette.Accent, IsAntialias = true };

        // The lens, with the capsule's own search field cut out of it. Even-odd rather than a
        // second paint in the ground colour: the hole has to be a HOLE. Windows composites this
        // onto a taskbar whose colour it chose, and a slot filled with Findra's own ground is a
        // dark smudge sitting on a light taskbar.
        using (var lens = new SKPath { FillType = SKPathFillType.EvenOdd })
        {
            lens.AddCircle(110f, 108f, 64f);
            lens.AddRoundRect(new SKRoundRect(new SKRect(76f, 94f, 144f, 122f), 14f));
            canvas.DrawPath(lens, accent);
        }

        using (var handle = new SKPaint
        {
            Color = palette.Accent, IsAntialias = true, Style = SKPaintStyle.Stroke,
            StrokeWidth = 30f, StrokeCap = SKStrokeCap.Round,
        }) canvas.DrawLine(158f, 156f, 200f, 198f, handle);

        canvas.Flush();
        using SKImage image = surface.Snapshot();
        return image.Encode(SKEncodedImageFormat.Png, 100);
    }
}
