using System;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace Findra;

/// <summary>The dark layer behind an open card: the whole monitor, black, fading to 45% over
/// 200 ms, input-transparent so a click on the desktop still lands on the desktop (and closes the
/// card by deactivating it). Topmost, shown just before the card, closed with it.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DimWindow : Window
{
    private readonly DispatcherTimer _fade;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private const double Target = 0.45, Seconds = 0.2;

    public DimWindow(PixelRect screen, double scaling)
    {
        WindowDecorations = Avalonia.Controls.WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = new SolidColorBrush(Colors.Black, 0);
        ShowInTaskbar = false;
        CanResize = false;
        Topmost = true;
        ShowActivated = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle.HideFromAltTab(this);
        Position = new PixelPoint(screen.X, screen.Y);
        Width = screen.Width / (scaling <= 0 ? 1 : scaling);
        Height = screen.Height / (scaling <= 0 ? 1 : scaling);
        Opened += (_, _) => WindowStyle.MakeInputTransparent(this);

        // one brush, its opacity moved every frame at render priority: swapping brushes from a
        // timer stepped visibly
        var brush = new SolidColorBrush(Colors.Black, 0);
        Background = brush;
        _fade = new DispatcherTimer(TimeSpan.FromMilliseconds(8), DispatcherPriority.Render, (_, _) =>
        {
            double t = Math.Clamp(_clock.Elapsed.TotalSeconds / Seconds, 0, 1);
            brush.Opacity = Target * (1 - (1 - t) * (1 - t));
            if (t >= 1) _fade!.Stop();
        });
        _fade.Start();
        Closed += (_, _) => _fade.Stop();
    }
}
