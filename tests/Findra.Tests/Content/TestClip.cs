using System.Runtime.InteropServices;
using Findra;

/// <summary>A three-second video, written by Windows' own encoder, one solid colour per second.
/// Every frame is a key frame so that seeking to the middle of a second lands in that second.
/// </summary>
public static class TestClip
{
    private static readonly Guid H264 = new("34363248-0000-0010-8000-00AA00389B71");
    private static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    private static readonly Guid InterlaceMode = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    private static readonly Guid AvgBitrate = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    private static readonly Guid MaxKeyFrameSpacing = new("c16eb52b-73a1-476f-8d62-839d6a020652");
    private static readonly Guid GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");

    private const int W = 320, H = 240, Fps = 10;
    private const long Second = 10_000_000L;

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    private static extern int MFCreateSinkWriterFromURL(string url, IntPtr byteStream, IntPtr attributes, out IntPtr writer);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out IntPtr sample);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(int maxLength, out IntPtr buffer);

    // IMFSinkWriter: IUnknown, then AddStream, SetInputMediaType, BeginWriting, WriteSample,
    // SendStreamTick, PlaceMarker, NotifyEndOfSegment, Flush, Finalize.
    private const int AddStream = 3, SetInputMediaType = 4, BeginWriting = 5, WriteSample = 6, FinalizeSlot = 11;
    // IMFSample, after IMFAttributes' 30: flags, time, duration, buffers.
    private const int SetSampleTime = 36, SetSampleDuration = 38, AddBuffer = 42;
    private const int SetCurrentLength = 6;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddStreamFn(IntPtr self, IntPtr type, out int index);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetInputFn(IntPtr self, int stream, IntPtr type, IntPtr parameters);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int NoArgsFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int WriteSampleFn(IntPtr self, int stream, IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetLongFn(IntPtr self, long value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int AddBufferFn(IntPtr self, IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetIntFn(IntPtr self, int value);

    public static string Write(string path, params (byte B, byte G, byte R)[] seconds)
    {
        ArgumentNullException.ThrowIfNull(seconds);
        MediaFoundation.Startup();

        IntPtr outType = Type(H264, bitrate: 1_000_000);
        IntPtr inType = Type(MediaFoundation.Rgb32, bitrate: 0);
        Check(MFCreateSinkWriterFromURL(path, IntPtr.Zero, IntPtr.Zero, out IntPtr writer));
        try
        {
            Check(MediaFoundation.Call<AddStreamFn>(writer, AddStream)(writer, outType, out int stream));
            Check(MediaFoundation.MFCreateAttributes(out IntPtr encoding, 1));
            MediaFoundation.Call<MediaFoundation.SetUInt32Fn>(encoding, MediaFoundation.SetUINT32)(encoding, GopSize, 1);
            Check(MediaFoundation.Call<SetInputFn>(writer, SetInputMediaType)(writer, stream, inType, encoding));
            MediaFoundation.Release(encoding);
            Check(MediaFoundation.Call<NoArgsFn>(writer, BeginWriting)(writer));

            long t = 0;
            foreach ((byte b, byte g, byte r) in seconds)
                for (int f = 0; f < Fps; f++)
                {
                    Check(MFCreateSample(out IntPtr sample));
                    Check(MFCreateMemoryBuffer(W * H * 4, out IntPtr buffer));
                    Fill(buffer, b, g, r);
                    MediaFoundation.Call<SetIntFn>(buffer, SetCurrentLength)(buffer, W * H * 4);
                    MediaFoundation.Call<AddBufferFn>(sample, AddBuffer)(sample, buffer);
                    MediaFoundation.Call<SetLongFn>(sample, SetSampleTime)(sample, t);
                    MediaFoundation.Call<SetLongFn>(sample, SetSampleDuration)(sample, Second / Fps);
                    Check(MediaFoundation.Call<WriteSampleFn>(writer, WriteSample)(writer, stream, sample));
                    MediaFoundation.Release(buffer);
                    MediaFoundation.Release(sample);
                    t += Second / Fps;
                }

            Check(MediaFoundation.Call<NoArgsFn>(writer, FinalizeSlot)(writer));
        }
        finally
        {
            MediaFoundation.Release(writer);
            MediaFoundation.Release(inType);
            MediaFoundation.Release(outType);
        }
        return path;
    }

    private static IntPtr Type(Guid subtype, int bitrate)
    {
        Check(MediaFoundation.MFCreateMediaType(out IntPtr type));
        MediaFoundation.Call<MediaFoundation.SetGuidFn>(type, MediaFoundation.SetGUID)(type, MediaFoundation.MajorType, MediaFoundation.VideoMajor);
        MediaFoundation.Call<MediaFoundation.SetGuidFn>(type, MediaFoundation.SetGUID)(type, MediaFoundation.Subtype, subtype);
        var set = MediaFoundation.Call<MediaFoundation.SetUInt32Fn>(type, MediaFoundation.SetUINT32);
        set(type, InterlaceMode, 2);
        if (bitrate > 0) { set(type, AvgBitrate, (uint)bitrate); set(type, MaxKeyFrameSpacing, 1); }
        else set(type, MediaFoundation.DefaultStride, unchecked((uint)(W * 4)));
        // A size and a rate are two 32-bit halves of one 64-bit attribute.
        var set64 = MediaFoundation.Call<Set64Fn>(type, SetUINT64);
        set64(type, MediaFoundation.FrameSize, ((ulong)W << 32) | (uint)H);
        set64(type, FrameRate, ((ulong)Fps << 32) | 1);
        set64(type, MediaFoundation.PixelAspect, (1UL << 32) | 1);
        return type;
    }

    private const int SetUINT64 = 22;
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int Set64Fn(IntPtr self, in Guid key, ulong value);

    private static unsafe void Fill(IntPtr buffer, byte b, byte g, byte r)
    {
        Check(MediaFoundation.Call<MediaFoundation.LockFn>(buffer, MediaFoundation.Lock)(buffer, out IntPtr data, out _, out _));
        try
        {
            byte* q = (byte*)data;
            for (int i = 0; i < W * H; i++) { q[i * 4] = b; q[i * 4 + 1] = g; q[i * 4 + 2] = r; q[i * 4 + 3] = 255; }
        }
        finally { MediaFoundation.Call<MediaFoundation.UnlockFn>(buffer, MediaFoundation.Unlock)(buffer); }
    }

    private static void Check(int hr) { if (hr < 0) throw Marshal.GetExceptionForHR(hr)!; }
}
