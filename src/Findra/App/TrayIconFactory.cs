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
/// The tray icon, drawn at runtime rather than shipped as a binary asset: the capsule glyph in
/// whichever palette is in force, so the icon in the tray belongs to the same look as the card.
/// Nothing binary enters the repository, and a new palette needs no new file.
/// </summary>
public static class TrayIconFactory
{
    public const int Size = 32;

    /// <summary>Returns null rather than throwing. A machine with no shell to put an icon in is a
    /// degraded state, not a reason to fail a launch.</summary>
    public static WindowIcon? Draw(Palette palette)
    {
        try
        {
            var info = new SKImageInfo(Size, Size, SKColorType.Bgra8888, SKAlphaType.Premul);
            using SKSurface surface = SKSurface.Create(info);
            SKCanvas canvas = surface.Canvas;
            canvas.Clear(SKColors.Transparent);

            // A filled pill in the accent - the capsule, at 32 px - with the magnifier cut out of
            // it in the ground colour so the shape reads at tray size instead of turning to mush.
            var bar = new SKRect(2f, 9f, Size - 2f, Size - 9f);
            using (var fill = new SKPaint { Color = palette.Accent, IsAntialias = true })
                canvas.DrawRoundRect(new SKRoundRect(bar, bar.Height / 2f), fill);

            using (var glyph = new SKPaint
            {
                Color = palette.Ground, IsAntialias = true, Style = SKPaintStyle.Stroke,
                StrokeWidth = 2.2f, StrokeCap = SKStrokeCap.Round,
            })
            {
                canvas.DrawCircle(13f, 15f, 4.2f, glyph);
                canvas.DrawLine(16.2f, 18.2f, 19.5f, 21.5f, glyph);
            }

            canvas.Flush();
            using SKImage image = surface.Snapshot();
            using SKData data = image.Encode(SKEncodedImageFormat.Png, 100);

            var stream = new MemoryStream(data.ToArray());
            return new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            Log.Warn("app", "the tray icon could not be drawn: " + ex.Message);
            return null;
        }
    }
}
