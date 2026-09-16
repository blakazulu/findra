using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

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
}
