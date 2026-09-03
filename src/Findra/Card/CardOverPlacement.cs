using System;
using Avalonia;
using SkiaSharp;

namespace Findra;

/// <summary>
/// Where the card lands when it is opened FROM THE WIDGET: its field sits exactly where the
/// widget's capsule bar was, and the whole card is then kept inside the monitor.
///
/// Everything here is physical pixels, because that is what a <see cref="PixelPoint"/> and a
/// <see cref="PixelRect"/> are, and physical is DIP times the monitor's scaling - the same idiom
/// <c>DimWindow</c> uses next door. Getting that wrong is not an offset: the clamp would reserve a
/// card a third too small at 150% and let a third of it hang off the right of the screen.
///
/// Pure and static so the arithmetic has a test. The window it positions cannot have one - it
/// needs a display - so this is the whole of the placement that can be verified headlessly.
///
/// <para>This is the capsule-opened counterpart to <c>CardPlacement.CentredGrown</c>, which does
/// the same job for the hotkey. It is a separate type only because the two live in different
/// files today; the arithmetic and the units are the same.</para>
/// </summary>
public static class CardOverPlacement
{
    /// <summary>The card at its worst case, in physical pixels: the width it always has, and the
    /// height it reaches once a full page of results lands. The card grows in place and is never
    /// moved again, so placement has to reserve the grown height up front.</summary>
    public static PixelSize GrownSize(double zoom, double screenScaling)
    {
        double s = screenScaling > 0 ? screenScaling : 1.0;
        return new PixelSize(
            (int)Math.Ceiling(SearchCardLayout.Width * zoom * s),
            (int)Math.Ceiling(SearchCardLayout.WindowHeight(SearchCardLayout.MaxRows, true, progress: true) * zoom * s));
    }

    /// <param name="widgetPos">The widget window's top-left, in physical pixels.</param>
    /// <param name="zoom">The card's own zoom: what turns the layout's unscaled units into DIPs.</param>
    /// <param name="capsule">The widget's capsule bar, in the widget's unscaled layout units.</param>
    /// <param name="screen">The monitor the whole card is kept inside, in physical pixels.</param>
    /// <param name="screenScaling">That monitor's scaling. One DIP is this many physical pixels.</param>
    public static PixelPoint Over(PixelPoint widgetPos, double zoom, SKRect capsule,
                                  PixelRect screen, double screenScaling)
    {
        double s = screenScaling > 0 ? screenScaling : 1.0;
        PixelSize size = GrownSize(zoom, s);

        // The offset is a distance in layout units, so it goes through BOTH factors before it can
        // be added to a physical position. Multiplying by zoom alone leaves the card's field 7 px
        // right and 13 px high of the bar it is replacing at 150%.
        int x = widgetPos.X + (int)Math.Round((capsule.Left - SearchCardLayout.Pad) * zoom * s);
        int y = widgetPos.Y + (int)Math.Round((capsule.Top - SearchCardLayout.FieldTop) * zoom * s);

        x = Math.Clamp(x, screen.X, Math.Max(screen.X, screen.X + screen.Width - size.Width));
        y = Math.Clamp(y, screen.Y, Math.Max(screen.Y, screen.Y + screen.Height - size.Height));
        return new PixelPoint(x, y);
    }
}
