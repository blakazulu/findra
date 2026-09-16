using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using SkiaSharp;

namespace Findra;

/// <summary>
/// The one place a video is opened for pictures - by the indexer, and by the card's preview.
///
/// <para><b>Why not the media pipeline Windows ships for editing.</b> That path took about 33
/// seconds per frame for MPEG-4 Part 2 video and handed back a black picture, on this machine,
/// at the size the indexer asks for. The decoders underneath are the same ones this file uses
/// through the source reader, which reads the same films at 17 to 24 ms a frame with real
/// pictures in them. The failure reported success at every step, which is why nothing caught it:
/// a black frame is a valid image, it embeds, it stores, and nothing re-reads a file that
/// worked.</para>
///
/// <para>Everything here reads a file somebody else wrote, so it belongs in the indexer child at
/// normal integrity and is never reachable from the elevated helper.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class VideoFrames
{
    /// <summary>An opened video, or the reason it could not be. <c>Reader</c> is
    /// <see cref="IntPtr.Zero"/> exactly when <c>Skip</c> is set.</summary>
    public readonly record struct VideoOpen(IntPtr Reader, double Seconds, string Codec, string? Skip);

    private const int TopoCodecNotFound = unchecked((int)0xC00D5212);
    private const int InvalidStreamNumber = unchecked((int)0xC00D36B3);
    private const int UnsupportedByteStream = unchecked((int)0xC00D36C4);

    /// <summary>The ordinary open failures that are facts about a file rather than faults. Null
    /// for anything else, which leaves it to the indexer's failure path: file-not-found,
    /// access-denied and a genuinely corrupt file are faults, not facts, and keep throwing.
    /// </summary>
    public static string? SkipFor(int hresult) => hresult switch
    {
        TopoCodecNotFound => "no decoder for this video format yet",
        InvalidStreamNumber => "no video stream",
        UnsupportedByteStream => "no reader for this container yet",
        _ => null,
    };

    /// <summary>A video format id carries its four-character code in its first four bytes. The
    /// name is what a person types into a search engine when Findra says it cannot read their
    /// file, so it is the code itself and never a number.</summary>
    public static string CodecName(Guid subtype)
    {
        Span<byte> b = stackalloc byte[16];
        subtype.TryWriteBytes(b);
        bool printable = true;
        for (int i = 0; i < 4; i++) if (b[i] < 0x20 || b[i] > 0x7e) printable = false;
        if (!printable) return subtype.ToString();
        return new string([(char)b[0], (char)b[1], (char)b[2], (char)b[3]]).TrimEnd();
    }

    /// <summary>Open one video for reading pictures out of it. The caller owns the reader and
    /// closes it with <see cref="Close"/>.</summary>
    public static VideoOpen Open(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        MediaFoundation.Startup();

        IntPtr attributes = IntPtr.Zero, reader = IntPtr.Zero;
        try
        {
            int hr = MediaFoundation.MFCreateAttributes(out attributes, 1);
            if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
            MediaFoundation.Call<MediaFoundation.SetUInt32Fn>(attributes, MediaFoundation.SetUINT32)(
                attributes, MediaFoundation.EnableVideoProcessing, 1);

            hr = MediaFoundation.MFCreateSourceReaderFromURL(path, attributes, out reader);
            if (hr < 0)
            {
                if (SkipFor(hr) is { } why) return new VideoOpen(IntPtr.Zero, 0, "", why);
                throw Marshal.GetExceptionForHR(hr)!;
            }

            string codec = NativeCodec(reader);

            MediaFoundation.Call<MediaFoundation.SetStreamSelectionFn>(reader, MediaFoundation.SetStreamSelection)(
                reader, MediaFoundation.AllStreams, 0);
            MediaFoundation.Call<MediaFoundation.SetStreamSelectionFn>(reader, MediaFoundation.SetStreamSelection)(
                reader, MediaFoundation.FirstVideoStream, 1);

            hr = AskForRgb32(reader);
            if (hr < 0)
            {
                Close(reader);
                reader = IntPtr.Zero;
                return new VideoOpen(IntPtr.Zero, 0, codec,
                                     SkipFor(hr) ?? "no decoder for this video format yet");
            }

            VideoOpen opened = new(reader, Seconds(reader), codec, null);
            reader = IntPtr.Zero;              // handed to the caller
            return opened;
        }
        finally
        {
            if (attributes != IntPtr.Zero) MediaFoundation.Release(attributes);
            if (reader != IntPtr.Zero) MediaFoundation.Release(reader);
        }
    }

    /// <summary>Release a reader <see cref="Open"/> handed back. Nothing else in this file may
    /// hold one past this call - the caller owns it from the moment <c>Open</c> returns.</summary>
    public static void Close(IntPtr reader) => MediaFoundation.Release(reader);

    /// <summary>The codec as the FILE states it, read before any decoder is asked for - which is
    /// what makes "Windows cannot read this one" nameable on a machine that has no decoder for
    /// it.</summary>
    private static string NativeCodec(IntPtr reader)
    {
        IntPtr type = IntPtr.Zero;
        try
        {
            int hr = MediaFoundation.Call<MediaFoundation.GetNativeMediaTypeFn>(reader, MediaFoundation.GetNativeMediaType)(
                reader, MediaFoundation.FirstVideoStream, 0, out type);
            if (hr < 0 || type == IntPtr.Zero) return "";
            hr = MediaFoundation.Call<MediaFoundation.GetGuidFn>(type, MediaFoundation.GetGUID)(
                type, MediaFoundation.Subtype, out Guid subtype);
            return hr < 0 ? "" : CodecName(subtype);
        }
        finally { if (type != IntPtr.Zero) MediaFoundation.Release(type); }
    }

    private static int AskForRgb32(IntPtr reader)
    {
        IntPtr want = IntPtr.Zero;
        try
        {
            int hr = MediaFoundation.MFCreateMediaType(out want);
            if (hr < 0) return hr;
            MediaFoundation.Call<MediaFoundation.SetGuidFn>(want, MediaFoundation.SetGUID)(
                want, MediaFoundation.MajorType, MediaFoundation.VideoMajor);
            MediaFoundation.Call<MediaFoundation.SetGuidFn>(want, MediaFoundation.SetGUID)(
                want, MediaFoundation.Subtype, MediaFoundation.Rgb32);
            return MediaFoundation.Call<MediaFoundation.SetMediaTypeFn>(reader, MediaFoundation.SetCurrentMediaType)(
                reader, MediaFoundation.FirstVideoStream, IntPtr.Zero, want);
        }
        finally { if (want != IntPtr.Zero) MediaFoundation.Release(want); }
    }

    /// <summary>How long the video is, in seconds, or zero when the container does not say. A file
    /// with no stated length still gets its first frame.</summary>
    private static double Seconds(IntPtr reader)
    {
        var pv = default(MediaFoundation.PropVariant);
        try
        {
            int hr = MediaFoundation.Call<MediaFoundation.GetPresentationAttributeFn>(reader, MediaFoundation.GetPresentationAttribute)(
                reader, MediaFoundation.MediaSource, MediaFoundation.Duration, out pv);
            return hr < 0 ? 0 : pv.Value / 1e7;
        }
        finally { MediaFoundation.PropVariantClear(ref pv); }
    }

    /// <summary>One frame: the picture, or nothing and why. <c>Empty</c> is the decoder returning
    /// a buffer with nothing in it; <c>Ended</c> is the end of the stream.</summary>
    public readonly record struct FrameResult(SKBitmap? Picture, double AtSeconds, bool Empty, bool Ended);

    /// <summary>How many samples to read past an empty one before giving up on this time.
    /// The MPEG-1 decoder returns one all-zero buffer after every seek and the real picture
    /// straight after it, so the first frame of most of a film came back black while the frames
    /// between them were fine.</summary>
    private const int PastEmpty = 8;

    /// <summary>The frame at or before <paramref name="seconds"/>, which is the key frame the
    /// decoder can produce without decoding forward. It can be up to a GOP earlier than asked -
    /// about 12 seconds on the films measured - and that is the precision the card and the index
    /// have always used.</summary>
    public static FrameResult Frame(IntPtr reader, double seconds, int maxDim)
    {
        var position = new MediaFoundation.PropVariant { Type = 20 /* VT_I8 */, Value = (ulong)(long)(seconds * 1e7) };
        MediaFoundation.Call<MediaFoundation.SetPositionFn>(reader, MediaFoundation.SetCurrentPosition)(
            reader, Guid.Empty, in position);

        FrameShape shape = ShapeOf(reader);
        for (int read = 0; read <= PastEmpty; read++)
        {
            int hr = MediaFoundation.Call<MediaFoundation.ReadSampleFn>(reader, MediaFoundation.ReadSample)(
                reader, MediaFoundation.FirstVideoStream, 0, out _, out int flags, out long timestamp, out IntPtr sample);
            if (hr < 0) return new FrameResult(null, 0, Empty: false, Ended: true);
            try
            {
                if ((flags & MediaFoundation.SampleTypeChanged) != 0) shape = ShapeOf(reader);
                if ((flags & MediaFoundation.SampleError) != 0) return new FrameResult(null, 0, false, true);
                if ((flags & MediaFoundation.SampleEndOfStream) != 0) return new FrameResult(null, 0, false, true);
                if (sample == IntPtr.Zero) continue;

                SKBitmap? raw = Copy(sample, shape);
                if (raw is null) continue;
                using (raw)
                {
                    if (IsEmpty(raw)) continue;      // the decoder produced nothing; try the next sample
                    return new FrameResult(VideoGeometry.Apply(raw, shape, maxDim), timestamp / 1e7, false, false);
                }
            }
            finally { if (sample != IntPtr.Zero) MediaFoundation.Release(sample); }
        }
        return new FrameResult(null, 0, Empty: true, Ended: false);
    }

    /// <summary>Empty means EVERY pixel is zero, and nothing looser. A dark frame is not empty -
    /// most films open with a fade from black, and a threshold on how dark a picture may be is a
    /// rule about what somebody is allowed to find. All-zero is the decoder saying it produced
    /// nothing at all.</summary>
    private static bool IsEmpty(SKBitmap b)
    {
        ReadOnlySpan<byte> px = b.GetPixelSpan();
        for (int i = 0; i + 3 < px.Length; i += 4)
            if (px[i] != 0 || px[i + 1] != 0 || px[i + 2] != 0) return false;
        return true;
    }

    private static unsafe SKBitmap? Copy(IntPtr sample, FrameShape shape)
    {
        IntPtr buffer = IntPtr.Zero;
        try
        {
            int hr = MediaFoundation.Call<MediaFoundation.ConvertToContiguousFn>(sample, MediaFoundation.ConvertToContiguousBuffer)(
                sample, out buffer);
            if (hr < 0 || buffer == IntPtr.Zero) return null;
            hr = MediaFoundation.Call<MediaFoundation.LockFn>(buffer, MediaFoundation.Lock)(
                buffer, out IntPtr data, out _, out int length);
            if (hr < 0) return null;
            try
            {
                int abs = Math.Abs(shape.Stride);
                if (abs == 0 || length < abs) return null;
                int rows = Math.Min(shape.Height, length / abs);
                var bmp = new SKBitmap(new SKImageInfo(shape.Width, rows, SKColorType.Bgra8888, SKAlphaType.Opaque));
                byte* dst = (byte*)bmp.GetPixels();
                int dstRow = bmp.RowBytes, copy = Math.Min(Math.Min(shape.Width * 4, dstRow), abs);
                int total = length / abs;
                for (int y = 0; y < rows; y++)
                {
                    int src = shape.Stride < 0 ? total - 1 - y : y;
                    byte* to = dst + (long)y * dstRow;
                    Buffer.MemoryCopy((byte*)data + (long)src * abs, to, dstRow, copy);
                    for (int x = 3; x < copy; x += 4) to[x] = 255;   // RGB32's fourth byte is not alpha
                }
                return bmp;
            }
            finally { MediaFoundation.Call<MediaFoundation.UnlockFn>(buffer, MediaFoundation.Unlock)(buffer); }
        }
        finally { if (buffer != IntPtr.Zero) MediaFoundation.Release(buffer); }
    }

    /// <summary>The buffer's shape, read again whenever the decoder says its type changed - which
    /// is when the visible area first becomes known, because a decoder pads its output and only
    /// says so once it has produced something.</summary>
    private static FrameShape ShapeOf(IntPtr reader)
    {
        IntPtr type = IntPtr.Zero;
        try
        {
            int hr = MediaFoundation.Call<MediaFoundation.GetMediaTypeFn>(reader, MediaFoundation.GetCurrentMediaType)(
                reader, MediaFoundation.FirstVideoStream, out type);
            if (hr < 0 || type == IntPtr.Zero) return default;

            var u64 = MediaFoundation.Call<MediaFoundation.GetUInt64Fn>(type, MediaFoundation.GetUINT64);
            var u32 = MediaFoundation.Call<MediaFoundation.GetUInt32Fn>(type, MediaFoundation.GetUINT32);

            int w = 0, h = 0;
            if (u64(type, MediaFoundation.FrameSize, out ulong size) >= 0) { w = (int)(size >> 32); h = (int)(uint)size; }

            int stride = u32(type, MediaFoundation.DefaultStride, out uint st) >= 0 ? unchecked((int)st) : -w * 4;
            int rotation = u32(type, MediaFoundation.Rotation, out uint rot) >= 0 ? (int)rot : 0;

            int parN = 1, parD = 1;
            if (u64(type, MediaFoundation.PixelAspect, out ulong par) >= 0 && (uint)par != 0)
            { parN = (int)(par >> 32); parD = (int)(uint)par; }

            (int cw, int ch) = Aperture(type, w, h);
            return new FrameShape(w, h, stride, cw, ch, rotation, parN, parD);
        }
        finally { if (type != IntPtr.Zero) MediaFoundation.Release(type); }
    }

    /// <summary>The visible rectangle inside a padded buffer. An MFVideoArea is two 32-bit
    /// fixed-point offsets and then the size, so the size is the last eight bytes.</summary>
    private static (int W, int H) Aperture(IntPtr type, int w, int h)
    {
        var blob = MediaFoundation.Call<MediaFoundation.GetBlobFn>(type, MediaFoundation.GetBlob);
        var buffer = new byte[16];
        int hr = blob(type, MediaFoundation.MinimumDisplayAperture, buffer, buffer.Length, out int written);
        if (hr < 0 || written < 16) return (w, h);
        int cw = BitConverter.ToInt32(buffer, 8), ch = BitConverter.ToInt32(buffer, 12);
        return cw > 0 && ch > 0 ? (cw, ch) : (w, h);
    }
}
