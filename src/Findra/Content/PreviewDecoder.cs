using System;
using System.IO;
using System.Runtime.Versioning;

using SkiaSharp;

namespace Findra;

/// <summary>
/// The picture the card's stage shows for the selected row: a photo decoded at preview size by
/// Skia with its EXIF orientation honoured, and - for everything Skia cannot read, which is HEIC,
/// RAW, video, PDF and Office files - the shell's own thumbnail through WinRT, the same one
/// Explorer draws, from its cache when it has one.
///
/// <para>Neither path decodes a whole image into memory: a 48-megapixel photo is sampled down by
/// the codec, and a video is never opened here at all. And neither path throws for a file that
/// turns out not to be a picture, because the caller runs this over whatever row somebody has
/// arrowed onto.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class PreviewDecoder
{
    public static SKImage? Decode(string path, ResultKind kind, int maxDim, double moment = -1)
    {
        if (!File.Exists(path)) return null;
        // a matched moment shows THAT frame, not the file's poster
        if (kind == ResultKind.Video && moment >= 0)
        {
            try
            {
                var frames = Media.Frames(path, new[] { moment }, maxDim).GetAwaiter().GetResult();
                if (frames.Count > 0 && frames[0] is { } f) { using (f) return SKImage.FromBitmap(f); }
            }
            catch (Exception ex) { Log.Once("card|frame|" + ex.GetType().Name, "WARN", "card", $"frame at {moment:0}s failed :: {ex.Message}"); }
        }
        if (kind == ResultKind.Photo)
        {
            var img = DecodeWithSkia(path, maxDim);
            if (img is not null) return img;
        }
        if (kind is ResultKind.Photo or ResultKind.Video or ResultKind.Document)
            return ShellThumbnail(path, maxDim);
        return null;
    }

    public static SKImage? DecodeWithSkia(string path, int maxDim)
    {
        using var codec = SKCodec.Create(path);
        if (codec is null) return null;
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0) return null;

        float want = (float)maxDim / Math.Max(info.Width, info.Height);
        var dims = want < 1 ? codec.GetScaledDimensions(want) : new SKSizeI(info.Width, info.Height);
        var target = new SKImageInfo(dims.Width, dims.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        var bmp = new SKBitmap(target);
        var result = codec.GetPixels(target, bmp.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
        {
            bmp.Dispose();
            return null;
        }

        // the codec's scaled dims can still be large for a huge source; finish with a resize
        if (Math.Max(bmp.Width, bmp.Height) > maxDim * 1.5)
        {
            float s = (float)maxDim / Math.Max(bmp.Width, bmp.Height);
            var small = bmp.Resize(new SKImageInfo((int)(bmp.Width * s), (int)(bmp.Height * s), SKColorType.Rgba8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
            bmp.Dispose();
            if (small is null) return null;
            bmp = small;
        }

        var origin = codec.EncodedOrigin;
        if (origin == SKEncodedOrigin.TopLeft) return SKImage.FromBitmap(bmp);

        // phone photos carry their rotation in EXIF; without this every portrait shot lies on its side
        bool swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom or SKEncodedOrigin.LeftBottom;
        int w = swap ? bmp.Height : bmp.Width, h = swap ? bmp.Width : bmp.Height;
        using var surface = SKSurface.Create(new SKImageInfo(w, h, SKColorType.Rgba8888, SKAlphaType.Premul));
        var c = surface.Canvas;
        switch (origin)
        {
            case SKEncodedOrigin.TopRight: c.Translate(w, 0); c.Scale(-1, 1); break;
            case SKEncodedOrigin.BottomRight: c.Translate(w, h); c.RotateDegrees(180); break;
            case SKEncodedOrigin.BottomLeft: c.Translate(0, h); c.Scale(1, -1); break;
            case SKEncodedOrigin.LeftTop: c.RotateDegrees(90); c.Scale(1, -1); break;
            case SKEncodedOrigin.RightTop: c.Translate(w, 0); c.RotateDegrees(90); break;
            case SKEncodedOrigin.RightBottom: c.Translate(w, 0); c.RotateDegrees(90); c.Translate(0, 0); c.Scale(-1, 1); c.Translate(-bmp.Width, 0); break;
            case SKEncodedOrigin.LeftBottom: c.Translate(0, h); c.RotateDegrees(-90); break;
        }
        c.DrawBitmap(bmp, 0, 0);
        bmp.Dispose();
        return surface.Snapshot();
    }

    /// <summary>The thumbnail Explorer would show - from its cache when it has one, otherwise from
    /// the codec or handler registered for the type (HEIC/RAW with the codec pack, video frames,
    /// PDF and Office first pages).</summary>
    public static SKImage? ShellThumbnail(string path, int maxDim)
    {
        try
        {
            var file = Windows.Storage.StorageFile.GetFileFromPathAsync(path).AsTask().GetAwaiter().GetResult();
            using var thumb = file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.SingleItem,
                (uint)maxDim, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale)
                .AsTask().GetAwaiter().GetResult();
            if (thumb is null || thumb.Size == 0) return null;
            // an icon is not a preview: the shell hands back the file-type glyph when it has no
            // picture, and drawing that large reads as a broken thumbnail rather than none
            if (thumb.Type == Windows.Storage.FileProperties.ThumbnailType.Icon) return null;
            using var stream = System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(thumb);
            using var bmp = SKBitmap.Decode(stream);
            return bmp is null ? null : SKImage.FromBitmap(bmp);
        }
        catch (Exception ex)
        {
            Log.Once("card|thumb|" + ex.GetType().Name, "WARN", "card", $"shell thumbnail failed :: {ex.GetType().Name}: {ex.Message}");
            return null;
        }
    }
}
