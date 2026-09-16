using System;
using SkiaSharp;

namespace Findra;

/// <summary>What the decoder handed back, as opposed to what the film looks like: the buffer's
/// own size and stride, the part of it that is the picture, how the picture is stored rotated, and
/// how square its pixels are.</summary>
public readonly record struct FrameShape(int Width, int Height, int Stride,
                                         int CropWidth, int CropHeight,
                                         int Rotation, int ParNum, int ParDen);

/// <summary>
/// Turning a decoded buffer into the picture somebody would recognise.
///
/// <para>Three corrections, each from a real file. A decoder pads its output to a multiple of 16
/// rows, and the padding is not black - it is a green strip along the bottom of every H.264 frame.
/// A phone writes its video sideways and a flag saying so, and on a real library most videos are
/// phone videos. And a 720x576 broadcast rip has rectangular pixels, so a square fit squashes
/// everybody in it.</para>
///
/// <para>Pure, so all three are testable without opening a file.</para>
/// </summary>
public static class VideoGeometry
{
    /// <summary>Crop to the visible picture, square its pixels, turn it upright, and fit it inside
    /// <paramref name="maxDim"/>. Never enlarges: a 208x160 clip stays 208x160.</summary>
    public static SKBitmap Apply(SKBitmap raw, FrameShape shape, int maxDim)
    {
        ArgumentNullException.ThrowIfNull(raw);

        int cw = shape.CropWidth > 0 ? Math.Min(shape.CropWidth, raw.Width) : raw.Width;
        int ch = shape.CropHeight > 0 ? Math.Min(shape.CropHeight, raw.Height) : raw.Height;

        // The picture's size once its pixels are square.
        double wide = cw * (shape.ParNum > 0 && shape.ParDen > 0 ? shape.ParNum / (double)shape.ParDen : 1.0);
        double tall = ch;

        bool quarter = shape.Rotation is 90 or 270;
        double uprightW = quarter ? tall : wide;
        double uprightH = quarter ? wide : tall;

        double scale = Math.Min(1.0, maxDim / Math.Max(uprightW, uprightH));
        int outW = Math.Max(1, (int)Math.Round(uprightW * scale));
        int outH = Math.Max(1, (int)Math.Round(uprightH * scale));

        var result = new SKBitmap(new SKImageInfo(outW, outH, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var canvas = new SKCanvas(result))
        {
            canvas.Clear(SKColors.Black);
            canvas.Translate(outW / 2f, outH / 2f);
            // The flag says the picture is stored rotated counter-clockwise, so it is turned back
            // the other way to be seen the right way up.
            canvas.RotateDegrees(shape.Rotation);
            float drawW = (float)(wide * scale), drawH = (float)(tall * scale);
            var dest = new SKRect(-drawW / 2f, -drawH / 2f, drawW / 2f, drawH / 2f);
            var src = new SKRect(0, 0, cw, ch);
            using SKImage image = SKImage.FromBitmap(raw);
            canvas.DrawImage(image, src, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None), null);
        }
        return result;
    }
}
