# Video frames Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Read video frames through Media Foundation's Source Reader, put a time limit on every
file the indexer opens, record a missing codec as a re-queueable skip, and re-read the black frames
already stored.

**Architecture:** A new `VideoFrames` owns every video open, through hand-written COM interop in the
shape `GpuAdapter` already uses for DXGI. The indexer child reports progress while it works and the
interface kills a child that stops making it. A video Windows cannot decode is a skip naming the
codec, brought back when the machine's decoders change. Schema step 6 re-reads stored video frames
without re-transcribing.

**Tech Stack:** .NET 10, C#, Avalonia, SkiaSharp, SQLite (Microsoft.Data.Sqlite), xunit, Media
Foundation via `DllImport` + vtable calls. No new NuGet package.

**Spec:** `docs/superpowers/specs/2026-09-16-video-frames-design.md`

## Global Constraints

- **Build clean, always:** `dotnet build -warnaserror -t:Rebuild` must pass with zero warnings.
  `-t:Rebuild` because an incremental build reports a false clean.
- **TDD for all new code.** Write the failing test first, watch it fail, then implement.
- **`CHANGELOG.md` is updated in the same commit**, under `## [Unreleased]`, in Added / Changed /
  Fixed. Write each line for a person reading release notes.
- **No lineage.** Nothing in code, comments, tests, docs or commit messages may describe Findra as
  derived from another project, name one, or cite a plan, task number or review. Name the thing,
  not the document that asked for it.
- **User-facing text:** no em-dashes or en-dashes (use a plain hyphen), no emoji.
- **Every number a person sees is formatted invariantly** (`ToString("N0", CultureInfo.InvariantCulture)`).
- **New public members carry a `<summary>`** saying why, in the voice of the surrounding code.
- **Never commit or push unless the user asks.** Each task's commit step is written out; run it only
  when the user has said to commit.
- **The elevated helper never parses file content.** Everything in this plan runs in the indexer
  child (`findra.exe --index`) or the interface, never in `findra.exe --names`.

---

### Task 1: Media Foundation interop, and opening a video

**Files:**
- Create: `src/Findra/Content/MediaFoundation.cs`
- Create: `src/Findra/Content/VideoFrames.cs`
- Create: `tests/Findra.Tests/Content/VideoFramesTests.cs`

**Interfaces:**
- Consumes: nothing from earlier tasks.
- Produces:
  - `MediaFoundation.Startup()`, `MediaFoundation.Call<T>(IntPtr obj, int slot)`, `MediaFoundation.Release(IntPtr)`
  - `VideoFrames.Open(string path) -> VideoOpen` where
    `readonly record struct VideoOpen(IntPtr Reader, double Seconds, string Codec, string? Skip)`
  - `VideoFrames.CodecName(Guid subtype) -> string`
  - `VideoFrames.SkipFor(int hresult) -> string?`
  - `Decoders.NoVideoCodec` constant is NOT defined here; Task 3 adds it. Task 1 returns the raw
    sentence `"no decoder for this video format yet"` from `SkipFor` and Task 3 moves it to the
    constant.

**Why the vtable slots are written out:** a hand-written COM call is a number, and a wrong number
is a crash or silence. The slot table below counts from the interface chain. Step 5's test is what
proves them: it opens a real file and reads its duration, which fails immediately if any slot in
the chain is wrong.

- [ ] **Step 1: Write the failing test for the pure parts**

Create `tests/Findra.Tests/Content/VideoFramesTests.cs`:

```csharp
using Findra;
using Xunit;

public class VideoFramesTests
{
    [Theory]
    // The subtype GUID for a video format carries its FourCC in the first four bytes.
    [InlineData("34363248-0000-0010-8000-00AA00389B71", "H264")]
    [InlineData("43564548-0000-0010-8000-00AA00389B71", "HEVC")]
    [InlineData("5634504d-0000-0010-8000-00AA00389B71", "MP4V")]
    [InlineData("63766964-0000-0010-8000-00AA00389B71", "divc")]
    public void ACodecIsNamedByTheFourCharactersItsFormatIdCarries(string guid, string expected)
        => Assert.Equal(expected, VideoFrames.CodecName(new Guid(guid)));

    [Fact]
    public void AFormatIdThatIsNotAFourCharacterCodeIsNamedByItsGuid()
    {
        string name = VideoFrames.CodecName(new Guid("11111111-2222-3333-4444-555555555555"));
        Assert.Contains("11111111", name, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(unchecked((int)0xC00D5212), "no decoder")]      // MF_E_TOPO_CODEC_NOT_FOUND
    [InlineData(unchecked((int)0xC00D36B3), "no video stream")] // MF_E_INVALIDSTREAMNUMBER
    [InlineData(unchecked((int)0xC00D36C4), "container")]       // MF_E_UNSUPPORTED_BYTESTREAM_TYPE
    public void TheOrdinaryOpenFailuresBecomeReasonsRatherThanErrors(int hresult, string fragment)
        => Assert.Contains(fragment, VideoFrames.SkipFor(hresult), StringComparison.Ordinal);

    [Fact]
    public void AnythingElseIsNotASkipAndIsLeftToTheFailurePath()
        => Assert.Null(VideoFrames.SkipFor(unchecked((int)0x80004005)));   // E_FAIL
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/Findra.Tests --filter VideoFramesTests`
Expected: FAIL, `VideoFrames` does not exist.

- [ ] **Step 3: Write the interop**

Create `src/Findra/Content/MediaFoundation.cs`:

```csharp
using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Findra;

/// <summary>
/// The Media Foundation calls Findra makes, as plain function pointers off each object's vtable.
///
/// <para>Written by hand rather than taken from a package, for the reason <see cref="GpuAdapter"/>
/// is: what is needed is a handful of interfaces, and the one typed wrapper available brings a
/// pre-release COM runtime with it. The slot numbers below are counted from the interface chain,
/// not guessed, and <c>VideoFramesTests</c> opens a real file - which is what actually proves
/// them, because a wrong slot is a call into the wrong function.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class MediaFoundation
{
    // IUnknown occupies 0-2 on every interface below. The slot is named ReleaseSlot because
    // Release below is the method that uses it, and a const and a method cannot share a name.
    public const int ReleaseSlot = 2;

    // IMFAttributes: 3..32, in the order the interface declares them.
    public const int GetUINT32 = 7, GetUINT64 = 8, GetGUID = 10, GetBlobSize = 14, GetBlob = 15,
                     SetUINT32 = 21, SetGUID = 24;

    // IMFMediaType adds five after IMFAttributes' 30.
    // IMFSample adds its own after the same 30: flags, time, duration, then the buffers.
    public const int ConvertToContiguousBuffer = 41;

    // IMFMediaBuffer: IUnknown, then Lock, Unlock, GetCurrentLength...
    public const int Lock = 3, Unlock = 4;

    // IMFSourceReader: IUnknown, then the ten below.
    public const int GetStreamSelection = 3, SetStreamSelection = 4, GetNativeMediaType = 5,
                     GetCurrentMediaType = 6, SetCurrentMediaType = 7, SetCurrentPosition = 8,
                     ReadSample = 9, Flush = 10, GetServiceForStream = 11, GetPresentationAttribute = 12;

    public const int FirstVideoStream = unchecked((int)0xFFFFFFFC);
    public const int AllStreams = unchecked((int)0xFFFFFFFE);
    public const int MediaSource = unchecked((int)0xFFFFFFFF);

    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid VideoMajor = new("73646976-0000-0010-8000-00AA00389B71");
    public static readonly Guid Rgb32 = new("00000016-0000-0010-8000-00AA00389B71");
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid DefaultStride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");
    public static readonly Guid PixelAspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid Rotation = new("c380465d-2271-428c-9b83-ecea3b4a85c1");
    public static readonly Guid MinimumDisplayAperture = new("d7388766-18fe-48c6-a177-ee894867c8c4");
    public static readonly Guid Duration = new("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
    public static readonly Guid EnableVideoProcessing = new("fb394f3d-ccf1-42ee-bbb3-f9b845d5681d");

    public const int SampleEndOfStream = 0x2, SampleError = 0x1, SampleTypeChanged = 0x20;

    [DllImport("mfplat.dll")] public static extern int MFStartup(int version, int flags);
    [DllImport("mfplat.dll")] public static extern int MFShutdown();
    [DllImport("mfplat.dll")] public static extern int MFCreateAttributes(out IntPtr attributes, int initialSize);
    [DllImport("mfplat.dll")] public static extern int MFCreateMediaType(out IntPtr type);
    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    public static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);
    [DllImport("ole32.dll")] public static extern int PropVariantClear(ref PropVariant pv);

    /// <summary>A PROPVARIANT, as much of it as a UI8 needs. Cleared through
    /// <see cref="PropVariantClear"/> whatever came back, because a variant carrying a pointer
    /// leaks otherwise.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct PropVariant
    {
        public ushort Type, Reserved1, Reserved2, Reserved3;
        public ulong Value;
        public ulong Padding;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate uint ReleaseFn(IntPtr self);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SetStreamSelectionFn(IntPtr self, int stream, int selected);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GetMediaTypeFn(IntPtr self, int stream, out IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GetNativeMediaTypeFn(IntPtr self, int stream, int index, out IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SetMediaTypeFn(IntPtr self, int stream, IntPtr reserved, IntPtr type);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SetPositionFn(IntPtr self, in Guid timeFormat, in PropVariant position);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int ReadSampleFn(IntPtr self, int stream, int controlFlags, out int actualStream, out int streamFlags, out long timestamp, out IntPtr sample);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GetPresentationAttributeFn(IntPtr self, int stream, in Guid key, out PropVariant value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GetUInt32Fn(IntPtr self, in Guid key, out uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GetUInt64Fn(IntPtr self, in Guid key, out ulong value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GetGuidFn(IntPtr self, in Guid key, out Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int GetBlobFn(IntPtr self, in Guid key, byte[] buffer, int size, out int written);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SetUInt32Fn(IntPtr self, in Guid key, uint value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int SetGuidFn(IntPtr self, in Guid key, in Guid value);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int ConvertToContiguousFn(IntPtr self, out IntPtr buffer);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int LockFn(IntPtr self, out IntPtr data, out int maxLength, out int currentLength);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)] public delegate int UnlockFn(IntPtr self);

    /// <summary>One vtable slot as a callable delegate. Built on demand rather than cached in a
    /// static, which is the same reasoning <c>Parts.Face</c> is written under: a type initialiser
    /// that throws is unreportable.</summary>
    public static T Call<T>(IntPtr obj, int slot) where T : Delegate
    {
        IntPtr vtable = Marshal.ReadIntPtr(obj);
        return Marshal.GetDelegateForFunctionPointer<T>(Marshal.ReadIntPtr(vtable, slot * IntPtr.Size));
    }

    public static void Release(IntPtr obj)
    {
        if (obj != IntPtr.Zero) Call<ReleaseFn>(obj, ReleaseSlot)(obj);
    }

    private static bool _started;
    private static readonly object Gate = new();

    /// <summary>Start the platform once per process. Started again it is merely reference counted,
    /// but the child opens thousands of files and a lock here is cheaper than finding that out.
    /// </summary>
    public static void Startup()
    {
        lock (Gate)
        {
            if (_started) return;
            int hr = MFStartup(0x00020070, 0);      // MF_VERSION for Windows 7 and later, full
            if (hr < 0) throw Marshal.GetExceptionForHR(hr)!;
            _started = true;
        }
    }
}
```

Create `src/Findra/Content/VideoFrames.cs` with the open half only (frames come in Task 2):

```csharp
using System;
using System.Globalization;
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

    /// <summary>The three open failures that are ordinary facts about a file rather than faults.
    /// Null for anything else - a missing file, a refused one, a genuinely broken one - which
    /// leaves it to the indexer's failure path.
    ///
    /// <para>The container case is its own reason rather than the codec one. It happens before any
    /// codec is known: the bytes were not recognised as a container at all, so there is nothing to
    /// name. Filing it as a codec gap would name a reason that is not the reason.</para></summary>
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
```

- [ ] **Step 4: Run the pure tests and watch them pass**

Run: `dotnet test tests/Findra.Tests --filter VideoFramesTests`
Expected: PASS.

- [ ] **Step 5: Prove the slot numbers against a real file**

This is the test that catches a wrong vtable slot. Add to `VideoFramesTests.cs`:

```csharp
    [Fact]
    public void AFileThatIsNotAVideoIsARecordedReasonRatherThanAThrow()
    {
        string path = Path.Combine(Path.GetTempPath(), "findra-notavideo-" + Guid.NewGuid().ToString("N") + ".mp4");
        File.WriteAllText(path, "this is not a video");
        try
        {
            VideoFrames.VideoOpen opened = VideoFrames.Open(path);
            Assert.False(string.IsNullOrEmpty(opened.Skip));
            Assert.Equal(IntPtr.Zero, opened.Reader);
        }
        finally { File.Delete(path); }
    }
```

Run: `dotnet test tests/Findra.Tests --filter VideoFramesTests`
Expected: PASS. A wrong slot in the open chain shows up here as an access violation or a hang
rather than a reason; if that happens, check the slot table against the interface declaration order
before changing anything else.

- [ ] **Step 6: Build clean**

Run: `dotnet build -warnaserror -t:Rebuild`
Expected: zero warnings, zero errors.

- [ ] **Step 7: Commit**

```bash
git add src/Findra/Content/MediaFoundation.cs src/Findra/Content/VideoFrames.cs tests/Findra.Tests/Content/VideoFramesTests.cs CHANGELOG.md
git commit -m "Open videos through the source reader, and name the codec a file states"
```

`CHANGELOG.md`, under `## [Unreleased]` / `### Added`:

```markdown
- **Findra can say which video codec a file needs.** A video Windows has no decoder for is now
  recognised by the format the file itself states, so the reason recorded names the codec rather
  than saying the file could not be read.
```

---

### Task 2: One frame, upright and cropped

**Files:**
- Modify: `src/Findra/Content/VideoFrames.cs`
- Create: `src/Findra/Content/VideoGeometry.cs`
- Create: `tests/Findra.Tests/Content/VideoGeometryTests.cs`
- Create: `tests/Findra.Tests/Content/TestClip.cs`
- Modify: `tests/Findra.Tests/Content/VideoFramesTests.cs`

**Interfaces:**
- Consumes: `VideoFrames.Open`, `MediaFoundation.*` from Task 1.
- Produces:
  - `readonly record struct FrameShape(int Width, int Height, int Stride, int CropWidth, int CropHeight, int Rotation, int ParNum, int ParDen)`
  - `VideoGeometry.Apply(SKBitmap raw, FrameShape shape, int maxDim) -> SKBitmap`
  - `VideoFrames.Frame(IntPtr reader, double seconds, int maxDim) -> FrameResult`
    where `readonly record struct FrameResult(SKBitmap? Picture, double AtSeconds, bool Empty, bool Ended)`
  - `TestClip.Write(string path, params (byte B, byte G, byte R)[] seconds) -> string`

- [ ] **Step 1: Write the failing geometry tests**

Create `tests/Findra.Tests/Content/VideoGeometryTests.cs`:

```csharp
using Findra;
using SkiaSharp;
using Xunit;

public class VideoGeometryTests
{
    private static SKBitmap Solid(int w, int h, SKColor colour)
    {
        var b = new SKBitmap(new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Opaque));
        using (var c = new SKCanvas(b)) c.Clear(colour);
        return b;
    }

    [Fact]
    public void ThePaddedRowsADecoderAddsAreCroppedAway()
    {
        // H.264 decodes padded to a multiple of 16: 536 rows arrive as 544, and the last 8 are
        // whatever the decoder left there.
        using SKBitmap raw = Solid(1280, 544, SKColors.Red);
        using SKBitmap fitted = VideoGeometry.Apply(raw, new FrameShape(1280, 544, 1280 * 4, 1280, 536, 0, 1, 1), 320);
        Assert.Equal(320, fitted.Width);
        Assert.Equal((int)Math.Round(320 * 536.0 / 1280), fitted.Height);
    }

    [Fact]
    public void APictureStoredRotatedIsTurnedBackUpright()
    {
        // A phone records 1920x1080 and says "rotated 90 counter-clockwise". Upright it is taller
        // than it is wide.
        using SKBitmap raw = Solid(1920, 1080, SKColors.Blue);
        using SKBitmap fitted = VideoGeometry.Apply(raw, new FrameShape(1920, 1080, 1920 * 4, 1920, 1080, 90, 1, 1), 320);
        Assert.True(fitted.Height > fitted.Width);
    }

    [Fact]
    public void NonSquarePixelsAreStretchedBeforeTheFit()
    {
        using SKBitmap raw = Solid(720, 576, SKColors.Green);
        using SKBitmap fitted = VideoGeometry.Apply(raw, new FrameShape(720, 576, 720 * 4, 720, 576, 0, 4, 3), 320);
        // 720 * 4/3 = 960 wide against 576 tall, so the fitted picture is wider than it is tall.
        Assert.True(fitted.Width > fitted.Height);
    }

    [Fact]
    public void AFrameAlreadySmallerThanTheFitIsLeftAlone()
    {
        using SKBitmap raw = Solid(208, 160, SKColors.Gray);
        using SKBitmap fitted = VideoGeometry.Apply(raw, new FrameShape(208, 160, 208 * 4, 208, 160, 0, 1, 1), 320);
        Assert.Equal(208, fitted.Width);
        Assert.Equal(160, fitted.Height);
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Findra.Tests --filter VideoGeometryTests`
Expected: FAIL, `VideoGeometry` does not exist.

- [ ] **Step 3: Write the geometry**

Create `src/Findra/Content/VideoGeometry.cs`:

```csharp
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
            canvas.DrawBitmap(raw, src, dest, new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
        }
        return result;
    }
}
```

- [ ] **Step 4: Run the geometry tests and watch them pass**

Run: `dotnet test tests/Findra.Tests --filter VideoGeometryTests`
Expected: PASS.

- [ ] **Step 5: Confirm the test clip writer is there**

`tests/Findra.Tests/Content/TestClip.cs` was pulled forward into Task 1, so that the vtable slots
the open chain uses were proved by a real file before this task extended the table with
`ReadSample`, `ConvertToContiguousBuffer`, `Lock` and `Unlock`. It should already exist, with
`AllowUnsafeBlocks` set in the test project. If it does, skip to Step 6 and use it. The code and
the reasoning are kept below because they are what that file has to contain.

Tests generate their own video: nothing binary is checked in, and no tool has to be installed.
Media Foundation's own encoder writes it. Every frame is a key frame, because a key-frame seek into
a clip with one key frame returns the first frame for every time asked for - which is what happens
with the encoder's default spacing, and it made the first version of this test pass for the wrong
reason.

Create `tests/Findra.Tests/Content/TestClip.cs`:

```csharp
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
```

`tests/Findra.Tests/Findra.Tests.csproj` needs `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>` if it
does not have it already.

- [ ] **Step 6: Write the failing frame test**

Add to `tests/Findra.Tests/Content/VideoFramesTests.cs`:

```csharp
    private static (byte B, byte G, byte R) MiddlePixel(SKBitmap b)
    {
        SKColor c = b.GetPixel(b.Width / 2, b.Height / 2);
        return (c.Blue, c.Green, c.Red);
    }

    [Fact]
    public void SeekingIntoASecondReturnsTheFrameFromThatSecond()
    {
        string path = Path.Combine(Path.GetTempPath(), "findra-clip-" + Guid.NewGuid().ToString("N") + ".mp4");
        TestClip.Write(path, (40, 40, 220), (40, 200, 40), (220, 60, 40));
        try
        {
            VideoFrames.VideoOpen opened = VideoFrames.Open(path);
            Assert.Null(opened.Skip);
            Assert.InRange(opened.Seconds, 2.5, 3.5);
            try
            {
                foreach ((double at, (byte B, byte G, byte R) want) in new[]
                         {
                             (0.5, ((byte)40, (byte)40, (byte)220)),
                             (1.5, ((byte)40, (byte)200, (byte)40)),
                             (2.5, ((byte)220, (byte)60, (byte)40)),
                         })
                {
                    VideoFrames.FrameResult f = VideoFrames.Frame(opened.Reader, at, 320);
                    Assert.NotNull(f.Picture);
                    using SKBitmap picture = f.Picture!;
                    (byte b, byte g, byte r) = MiddlePixel(picture);
                    Assert.InRange(b, want.B - 30, want.B + 30);
                    Assert.InRange(g, want.G - 30, want.G + 30);
                    Assert.InRange(r, want.R - 30, want.R + 30);
                }
            }
            finally { VideoFrames.Close(opened.Reader); }
        }
        finally { File.Delete(path); }
    }
```

- [ ] **Step 7: Run it and watch it fail**

Run: `dotnet test tests/Findra.Tests --filter SeekingIntoASecondReturnsTheFrameFromThatSecond`
Expected: FAIL, `VideoFrames.Frame` does not exist.

- [ ] **Step 8: Implement `Frame`**

Add to `src/Findra/Content/VideoFrames.cs`:

```csharp
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
```

`Copy` reads the sample's contiguous buffer into a bitmap, honouring a negative stride (a
bottom-up buffer starts at the last row):

```csharp
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
```

Add `using SkiaSharp;` to the file.

- [ ] **Step 9: Run the frame test and watch it pass**

Run: `dotnet test tests/Findra.Tests --filter VideoFramesTests`
Expected: PASS, all of them.

- [ ] **Step 10: Build clean and commit**

Run: `dotnet build -warnaserror -t:Rebuild`

```bash
git add src/Findra/Content/VideoFrames.cs src/Findra/Content/VideoGeometry.cs tests/Findra.Tests CHANGELOG.md
git commit -m "Read one video frame, cropped, upright and square-pixelled"
```

`CHANGELOG.md`, under `### Fixed`:

```markdown
- **Video frames are read the right way up, without a green strip along the bottom.** Frames from
  phone videos were stored on their side, and frames from most H.264 films carried a strip of
  decoder padding. Both are corrected from what the file itself says.
```

---

### Task 3: The indexer reads frames through it

**Files:**
- Modify: `src/Findra/Content/Decoders.cs`
- Modify: `src/Findra/Content/Media.cs` (delete `Frames`, `VideoDuration`)
- Create: `src/Findra/Content/VideoRead.cs`
- Create: `tests/Findra.Tests/Content/VideoReadTests.cs`
- Modify: `tests/Findra.Tests/Content/MediaTests.cs` (drop tests of the deleted members if any)

**Interfaces:**
- Consumes: `VideoFrames.Open/Frame/Close`, `FrameResult` (Task 2).
- Produces:
  - `interface IVideoSource : IDisposable { double Seconds { get; } string Codec { get; } VideoFrames.FrameResult Frame(double seconds, int maxDim); }`
  - `VideoRead.Open(string path) -> (IVideoSource? Source, string? Skip)`
  - `VideoRead.Plan(double duration) -> IReadOnlyList<double>` (delegates to `Media.SampleTimes`)
  - `VideoRead.Take(IVideoSource source, IReadOnlyList<double> times, Action beat, Func<TimeSpan> elapsed) -> VideoTake`
    where `readonly record struct VideoTake(List<(double At, SKBitmap Picture)> Frames, string? Skip)`
  - `Decoders.NoVideoCodec = "no decoder for this video format yet"`,
    `Decoders.NoVideoStream = "no video stream"`, `Decoders.NoFrames = "no frames"`
  - `Decoders` constructor gains `Func<string, (IVideoSource? Source, string? Skip)>? videos = null`

- [ ] **Step 1: Write the failing tests for the taking rules**

Create `tests/Findra.Tests/Content/VideoReadTests.cs`:

```csharp
using Findra;
using SkiaSharp;
using Xunit;

public class VideoReadTests
{
    private sealed class FakeVideo : IVideoSource
    {
        private readonly Func<double, VideoFrames.FrameResult> _answer;
        public FakeVideo(double seconds, Func<double, VideoFrames.FrameResult> answer)
        { Seconds = seconds; _answer = answer; }
        public double Seconds { get; }
        public string Codec => "TEST";
        public int Asked { get; private set; }
        public VideoFrames.FrameResult Frame(double seconds, int maxDim) { Asked++; return _answer(seconds); }
        public void Dispose() { }
    }

    private static SKBitmap Pixel() => new(new SKImageInfo(2, 2, SKColorType.Bgra8888, SKAlphaType.Opaque));

    private static VideoFrames.FrameResult Picture(double at) => new(Pixel(), at, false, false);
    private static VideoFrames.FrameResult Empty() => new(null, 0, true, false);

    [Fact]
    public void EveryFrameThatCameBackIsKept()
    {
        var video = new FakeVideo(100, Picture);
        VideoTake take = VideoRead.Take(video, [1, 11, 21], () => { }, () => TimeSpan.Zero);
        Assert.Equal(3, take.Frames.Count);
        Assert.Null(take.Skip);
        foreach ((_, SKBitmap p) in take.Frames) p.Dispose();
    }

    [Fact]
    public void ThreeEmptyFramesInARowWithNothingDecodedEndTheFile()
    {
        var video = new FakeVideo(1000, _ => Empty());
        VideoTake take = VideoRead.Take(video, [1, 11, 21, 31, 41, 51], () => { }, () => TimeSpan.Zero);
        Assert.Empty(take.Frames);
        Assert.Equal(Decoders.NoFrames, take.Skip);
        Assert.Equal(3, video.Asked);        // it stopped asking
    }

    [Fact]
    public void AFilmThatStartsBlackAndThenHasPicturesIsStillRead()
    {
        // Two empty frames then a picture: the give-up rule must not fire on a fade-in that the
        // decoder could not produce, only on a file with nothing in it at all.
        int n = 0;
        var video = new FakeVideo(1000, at => ++n <= 2 ? Empty() : Picture(at));
        VideoTake take = VideoRead.Take(video, [1, 11, 21, 31], () => { }, () => TimeSpan.Zero);
        Assert.Equal(2, take.Frames.Count);
        Assert.Null(take.Skip);
        foreach ((_, SKBitmap p) in take.Frames) p.Dispose();
    }

    [Fact]
    public void NoVideoSpendsMoreThanTheBudgetOnFrames()
    {
        var clock = TimeSpan.Zero;
        var video = new FakeVideo(10_000, Picture);
        VideoTake take = VideoRead.Take(video, [.. Enumerable.Range(0, 90).Select(i => i * 10.0)],
                                        () => { }, () => clock += TimeSpan.FromMinutes(1));
        // The budget is five minutes, so it stops after the fifth frame rather than taking ninety.
        Assert.True(take.Frames.Count <= 6, $"took {take.Frames.Count} frames");
        foreach ((_, SKBitmap p) in take.Frames) p.Dispose();
    }

    [Fact]
    public void EveryFrameReportsProgress()
    {
        int beats = 0;
        var video = new FakeVideo(100, Picture);
        VideoTake take = VideoRead.Take(video, [1, 11, 21], () => beats++, () => TimeSpan.Zero);
        Assert.Equal(3, beats);
        foreach ((_, SKBitmap p) in take.Frames) p.Dispose();
    }
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Findra.Tests --filter VideoReadTests`
Expected: FAIL, `VideoRead` does not exist.

- [ ] **Step 3: Write `VideoRead`**

Create `src/Findra/Content/VideoRead.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Runtime.Versioning;
using SkiaSharp;

namespace Findra;

/// <summary>A video, open, as the indexer needs it. An interface so the rules below can be tested
/// without a file, a decoder or a model.</summary>
public interface IVideoSource : IDisposable
{
    double Seconds { get; }
    string Codec { get; }
    VideoFrames.FrameResult Frame(double seconds, int maxDim);
}

/// <summary>What came of taking frames out of one video.</summary>
public readonly record struct VideoTake(List<(double At, SKBitmap Picture)> Frames, string? Skip);

/// <summary>
/// The rules about taking frames from a video: how many, when to stop, and what to record when
/// nothing came back.
///
/// <para>Separate from the decoding because the rules are the part that must not be wrong, and
/// they are the part a test can drive. A queue that hands out one file at a time needs a limit on
/// every file: without one the slowest file sets the pace for everything behind it, and "slow" can
/// mean forever.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class VideoRead
{
    /// <summary>The size a frame is stored at, which is what the vision tower wants.</summary>
    public const int FrameSize = 320;

    /// <summary>Empty frames in a row, with nothing decoded yet, before the file is given up on.
    /// Three rather than one: a real film can open on frames the decoder will not produce.</summary>
    public const int GiveUpAfter = 3;

    /// <summary>The most any one video may spend on frames.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromMinutes(5);

    public static (IVideoSource? Source, string? Skip) Open(string path)
    {
        VideoFrames.VideoOpen opened = VideoFrames.Open(path);
        if (opened.Skip is { } why)
            return (null, why == Decoders.NoVideoCodec && opened.Codec.Length > 0
                          ? $"{Decoders.NoVideoCodec} ({opened.Codec})"
                          : why);
        return (new SourceReaderVideo(opened), null);
    }

    public static IReadOnlyList<double> Plan(double duration) => Media.SampleTimes(duration);

    /// <summary>Take the frames, stopping when the file is not worth more time.
    /// <paramref name="beat"/> is called per frame so that a watching parent can tell work from a
    /// hang; <paramref name="elapsed"/> is how long this file has had, injected so the budget is
    /// testable without waiting five minutes.</summary>
    public static VideoTake Take(IVideoSource source, IReadOnlyList<double> times, Action beat, Func<TimeSpan> elapsed)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(times);
        ArgumentNullException.ThrowIfNull(beat);
        ArgumentNullException.ThrowIfNull(elapsed);

        var frames = new List<(double, SKBitmap)>();
        int emptyInARow = 0;
        foreach (double at in times)
        {
            VideoFrames.FrameResult f = source.Frame(at, FrameSize);
            beat();
            if (f.Picture is { } picture)
            {
                frames.Add((f.AtSeconds > 0 ? f.AtSeconds : at, picture));
                emptyInARow = 0;
            }
            else
            {
                if (f.Ended) break;
                emptyInARow++;
                if (frames.Count == 0 && emptyInARow >= GiveUpAfter) break;
            }
            if (elapsed() >= Budget) break;
        }
        return new VideoTake(frames, frames.Count == 0 ? Decoders.NoFrames : null);
    }

    private sealed class SourceReaderVideo(VideoFrames.VideoOpen opened) : IVideoSource
    {
        private IntPtr _reader = opened.Reader;
        public double Seconds { get; } = opened.Seconds;
        public string Codec { get; } = opened.Codec;

        public VideoFrames.FrameResult Frame(double seconds, int maxDim)
            => _reader == IntPtr.Zero ? new VideoFrames.FrameResult(null, 0, false, true)
                                      : VideoFrames.Frame(_reader, seconds, maxDim);

        public void Dispose()
        {
            if (_reader == IntPtr.Zero) return;
            VideoFrames.Close(_reader);
            _reader = IntPtr.Zero;
        }
    }
}
```

- [ ] **Step 4: Run the rule tests and watch them pass**

Run: `dotnet test tests/Findra.Tests --filter VideoReadTests`
Expected: PASS.

- [ ] **Step 5: Point `Decoders` at it**

In `src/Findra/Content/Decoders.cs`, add the three reason constants beside `NoFormatReader`:

```csharp
    /// <summary>Windows has no decoder for this video's codec. The codec is named in brackets
    /// after it, so a re-queue matches on the sentence and a person reads which one. It is a skip
    /// and never a failure: installing the codec makes exactly these files readable.</summary>
    public const string NoVideoCodec = "no decoder for this video format yet";

    /// <summary>A container with no video stream in it - an audio-only MP4, which is ordinary.
    /// The sound track is still transcribed.</summary>
    public const string NoVideoStream = "no video stream";

    /// <summary>Opened, and no picture came out of it. Recorded rather than stored: a video whose
    /// frames are all empty must not leave black frames in the index answering queries.</summary>
    public const string NoFrames = "no frames";

    /// <summary>Windows did not recognise the file as a container at all, which happens before any
    /// codec is known - so it is not the codec reason, which names one. A skip rather than a
    /// failure: an extension that brings a container source with it makes these readable, and the
    /// re-queue that watches the machine's decoders picks exactly these rows up.</summary>
    public const string NoContainerReader = "no reader for this container yet";
```

`VideoFrames.SkipFor` now returns these constants rather than its own literals, and
`VideoRead.Open` below compares against them - one sentence, in one place:

```csharp
        TopoCodecNotFound => Decoders.NoVideoCodec,
        InvalidStreamNumber => Decoders.NoVideoStream,
        UnsupportedByteStream => Decoders.NoContainerReader,
```

Replace `Decoders.Video` and `Decoders.Frames` (currently `Decoders.cs:415-473`):

```csharp
    private KindResult Video(string path)
    {
        (IVideoSource? source, string? openSkip) = _videos(path);
        using (source)
        {
            double duration = source?.Seconds ?? 0;
            var segs = new List<ContentDb.Segment>();
            string? frameSkip = openSkip;

            if (source is not null && Installed.Has(Capability.Photos))
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                VideoTake take = VideoRead.Take(source, VideoRead.Plan(duration), Beat, () => clock.Elapsed);
                frameSkip = take.Skip;
                segs.AddRange(Embed(take));
            }

            // The sound track is a separate capability and a separate question: a video whose
            // pictures cannot be read may still be worth hearing, and the file opens either way.
            string? tooLong = null;
            if (Installed.Has(Capability.Speech))
            {
                double heard = duration > 0 ? duration : Media.Duration(path);
                if (!TranscribeLimit.Covers(_transcribeMinutes(), heard)) tooLong = TooLong;
                else
                    try
                    {
                        (float[] samples, _) = Media.Decode(path, MaxDecodeSeconds);
                        if (samples.Length >= Media.SampleRate) segs.AddRange(Transcribe(path, samples, null));
                    }
                    catch (Exception ex)
                    {
                        Log.Once($"index|videoaudio|{ex.GetType().Name}", "WARN", "index",
                                 $"a video sound track could not be read :: {ex.GetType().Name}: {ex.Message}");
                    }
            }

            return segs.Count > 0
                ? new KindResult(segs, null, tooLong ?? frameSkip)
                : new KindResult(segs, frameSkip ?? tooLong ?? NoFrames);
        }
    }

    /// <summary>The frames, through the vision tower, in batches. Every picture handed in is one
    /// the decoder produced: an empty frame never reaches here, so nothing embeds black.</summary>
    private List<ContentDb.Segment> Embed(VideoTake take)
    {
        var segs = new List<ContentDb.Segment>();
        if (take.Frames.Count == 0) return segs;
        ClipImageEncoder vision = Vision();
        var batch = new List<float[]>();
        var batchTimes = new List<double>();

        void Flush()
        {
            if (batch.Count == 0) return;
            float[][] vs = vision.Encode(batch);
            for (int i = 0; i < vs.Length; i++)
                segs.Add(new ContentDb.Segment(ContentDb.SegFrame, batchTimes[i], batchTimes[i],
                                               Append(vs[i], ContentDb.SegFrame), ""));
            batch.Clear();
            batchTimes.Clear();
            Beat();
        }

        foreach ((double at, SKBitmap picture) in take.Frames)
            using (picture)
            {
                batch.Add(ClipImageEncoder.Preprocess(picture));
                batchTimes.Add(at);
                if (batch.Count == 8) Flush();
            }
        Flush();
        return segs;
    }
```

Add the two new constructor arguments and the `Beat` helper to `Decoders`:

```csharp
    private readonly Func<string, (IVideoSource? Source, string? Skip)> _videos;
    private readonly Action _beat;

    private void Beat() => _beat();
```

The constructor gains, after `modelDir`:

```csharp
                    Func<string, (IVideoSource? Source, string? Skip)>? videos = null,
                    Action? beat = null)
```

and in the body:

```csharp
        _videos = videos ?? VideoRead.Open;
        _beat = beat ?? (() => { });
```

`ForThisMachine` gains a `beat` parameter and passes it through:

```csharp
    public static Decoders ForThisMachine(Func<int> transcribeMinutes, string? modelDir = null, Action? beat = null)
        => new(() => CapabilitySet.Installed(modelDir), new VectorStore(writer: true), transcribeMinutes,
               modelDir, ownsVectors: true, beat: beat);
```

- [ ] **Step 6: Delete the old decoder**

In `src/Findra/Content/Media.cs`, delete `Frames` and `VideoDuration` entirely, and the
`<para><b>Video frames</b>...</para>` paragraph in the type summary. Replace that paragraph with:

```csharp
/// <para><b>Video frames are not here.</b> They come from <see cref="VideoFrames"/>, which reads
/// them through the source reader; this file is sound alone.</para>
```

Delete any test in `tests/Findra.Tests/Content/MediaTests.cs` that calls the removed members.
`SampleTimes` stays and so do its tests.

- [ ] **Step 7: Run every test**

Run: `dotnet test`
Expected: PASS. Fix any caller the compiler names - `PreviewDecoder` is Task 4 and may reference
`Media.Frames`; comment its frame branch out only if it blocks the build, and restore it in Task 4.

- [ ] **Step 8: Build clean and commit**

Run: `dotnet build -warnaserror -t:Rebuild`

```bash
git add src/Findra/Content tests/Findra.Tests/Content CHANGELOG.md
git commit -m "Index video frames through the source reader, with a limit on every file"
```

`CHANGELOG.md`, under `### Fixed`:

```markdown
- **Videos are read in seconds rather than hours, and the frames are real.** Findra read video
  frames through a path that took about 33 seconds per frame for DivX and XviD films and returned
  a black picture every time, so one film could hold up the whole queue for a day and then store
  90 black frames. Frames now come from Windows' own decoders directly: the same films read in
  under three seconds with real pictures in them. No video can spend more than five minutes on
  frames, and one that produces nothing is recorded as such instead of filling the index with
  black.
```

---

### Task 4: The card's preview reads the same way

**Files:**
- Modify: `src/Findra/Content/PreviewDecoder.cs`
- Modify: `tests/Findra.Tests/Content/VideoFramesTests.cs`

**Interfaces:**
- Consumes: `VideoRead.Open`, `IVideoSource` (Task 3).
- Produces: no new API. `PreviewDecoder.Decode` keeps its signature.

- [ ] **Step 1: Write the failing test**

Add to `tests/Findra.Tests/Content/VideoFramesTests.cs`:

```csharp
    [Fact]
    public void TheCardsPreviewOfAMatchedMomentIsTheFrameFromThatMoment()
    {
        string path = Path.Combine(Path.GetTempPath(), "findra-preview-" + Guid.NewGuid().ToString("N") + ".mp4");
        TestClip.Write(path, (40, 40, 220), (40, 200, 40), (220, 60, 40));
        try
        {
            using SKImage? img = PreviewDecoder.Decode(path, ResultKind.Video, 200, moment: 1.5);
            Assert.NotNull(img);
            using SKBitmap bmp = SKBitmap.FromImage(img!);
            SKColor c = bmp.GetPixel(bmp.Width / 2, bmp.Height / 2);
            Assert.InRange(c.Green, 170, 230);
            Assert.InRange(c.Red, 10, 70);
        }
        finally { File.Delete(path); }
    }
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/Findra.Tests --filter TheCardsPreviewOfAMatchedMomentIsTheFrameFromThatMoment`
Expected: FAIL.

- [ ] **Step 3: Implement**

In `src/Findra/Content/PreviewDecoder.cs`, replace the video-moment branch:

```csharp
        // a matched moment shows THAT frame, not the file's poster
        if (kind == ResultKind.Video && moment >= 0)
        {
            try
            {
                (IVideoSource? source, string? skip) = VideoRead.Open(path);
                using (source)
                    if (source is not null)
                    {
                        VideoFrames.FrameResult f = source.Frame(moment, maxDim);
                        if (f.Picture is { } picture) { using (picture) return SKImage.FromBitmap(picture); }
                    }
            }
            catch (Exception ex) { Log.Once("card|frame|" + ex.GetType().Name, "WARN", "card", $"frame at {moment:0}s failed :: {ex.Message}"); }
        }
```

Update the type summary's sentence about video so it says frames come from `VideoFrames` and the
shell thumbnail remains the fallback.

- [ ] **Step 4: Run it and watch it pass**

Run: `dotnet test tests/Findra.Tests --filter VideoFramesTests`
Expected: PASS.

- [ ] **Step 5: Build clean and commit**

```bash
git add src/Findra/Content/PreviewDecoder.cs tests/Findra.Tests/Content/VideoFramesTests.cs CHANGELOG.md
git commit -m "Show the matched moment of a video on the card through the same decoder"
```

`CHANGELOG.md`, under `### Fixed`:

```markdown
- **The picture on the card for a video result is the moment that matched.** It came from the same
  slow path as indexing, so for many films it was a black rectangle after a long wait.
```

---

### Task 5: The child says it is working

**Files:**
- Modify: `src/Findra/Content/Indexer.cs`
- Modify: `src/Findra/Content/Decoders.cs` (beats in the other decoders)
- Create: `tests/Findra.Tests/Content/BeatTests.cs`

**Interfaces:**
- Consumes: `Decoders(..., Action? beat)` (Task 3).
- Produces: `Indexer` writes `indexer:beat` during a file. No new public API beyond
  `Beat.Throttled(Func<long> nowUnix, Action<long> write, int everySeconds) -> Action`.

- [ ] **Step 1: Write the failing test**

Create `tests/Findra.Tests/Content/BeatTests.cs`:

```csharp
using Findra;
using Xunit;

public class BeatTests
{
    [Fact]
    public void AFloodOfProgressBecomesOneWritePerInterval()
    {
        long now = 1000;
        var written = new List<long>();
        Action beat = Beat.Throttled(() => now, written.Add, everySeconds: 2);

        for (int i = 0; i < 100; i++) beat();          // all in the same second
        Assert.Single(written);

        now += 1;
        beat();
        Assert.Single(written);                        // not yet

        now += 2;
        beat();
        Assert.Equal(2, written.Count);
        Assert.Equal(1003, written[1]);
    }
}
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/Findra.Tests --filter BeatTests`
Expected: FAIL, `Beat` does not exist.

- [ ] **Step 3: Implement the throttle and wire it in**

Create `src/Findra/Content/Beat.cs`:

```csharp
using System;

namespace Findra;

/// <summary>
/// Saying "still working" often enough to be believed and seldom enough to be free.
///
/// <para>The indexer used to write its heartbeat only between files, so a file that took an hour
/// looked exactly like a child that had died an hour ago - and the difference between those two is
/// the whole of whether anything should be done about it. Every decoder now reports as it goes,
/// and this is what keeps that from being a database write per video frame.</para>
/// </summary>
public static class Beat
{
    public static Action Throttled(Func<long> nowUnix, Action<long> write, int everySeconds = 2)
    {
        ArgumentNullException.ThrowIfNull(nowUnix);
        ArgumentNullException.ThrowIfNull(write);
        long last = long.MinValue;
        return () =>
        {
            long now = nowUnix();
            if (now - last < everySeconds) return;
            last = now;
            write(now);
        };
    }
}
```

In `src/Findra/Content/Indexer.cs`, build one and hand it to the decoders in `Run`:

```csharp
            using ContentDb db = ContentDb.OpenOrRebuild(dbPath);
            Action beat = Beat.Throttled(
                () => DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                seconds =>
                {
                    // Only the beat row. Everything else in Status describes a file that has been
                    // finished, and this is written in the middle of one.
                    try { db.Set("indexer:beat", seconds.ToString(CultureInfo.InvariantCulture)); }
                    catch (Exception ex) { Log.Once("index|beat", "WARN", "index", "a progress beat could not be written :: " + ex.Message); }
                });
            using IDecoders decoders = Decoders.ForThisMachine(() => TranscribeMinutes(db), beat: beat);
```

In `src/Findra/Content/Decoders.cs`, call `Beat()` at each step of real work:

- `Document`: after each `e5.EncodePassages(batch)` call, and after `DocText.Extract(path)`.
- `Photo`: after `LoadBitmap` and after `ImageText.Read(path)`.
- `Audio` and the video sound track: after `Media.Decode(...)` returns.
- `Transcribe`: pass `Beat` into `Speech.Merge`'s callback so each embedded line beats, and beat
  once after `Media.Transcribe` returns.
- Around each model open (`Vision()`, `E5()`, `Media.OpenWhisper`): once before, once after.

- [ ] **Step 4: Run it and watch it pass**

Run: `dotnet test tests/Findra.Tests --filter BeatTests`
Expected: PASS.

- [ ] **Step 5: Build clean and commit**

```bash
git add src/Findra/Content CHANGELOG.md
git commit -m "The indexer reports progress while it reads a file, not only between files"
```

`CHANGELOG.md`, under `### Changed`:

```markdown
- **The indexer says it is working while it reads one file.** A long recording or a large document
  used to leave every surface saying nothing was happening until the file was finished.
```

---

### Task 6: A child that stops making progress is restarted

**Files:**
- Create: `src/Findra/Content/IndexerWatch.cs`
- Modify: `src/Findra/Content/IndexerHost.cs`
- Modify: `src/Findra/App/App.axaml.cs` (`PumpIndexer`)
- Modify: `src/Findra/Content/Indexer.cs` (write-off wording)
- Modify: `src/Findra/Diagnostics/SearchProbe.cs`
- Create: `tests/Findra.Tests/Content/IndexerWatchTests.cs`

**Interfaces:**
- Consumes: the `indexer:beat` row written in Task 5.
- Produces:
  - `IndexerWatch.StallSeconds = 180`
  - `IndexerWatch.ShouldRestart(bool hostRunning, bool reading, long pending, string? beat, long nowUnix) -> bool`
  - `IndexerHost.Kill(string reason)` and `IndexerHost.Kill(string reason, Action<string> log, Func<bool> kill)`

- [ ] **Step 1: Write the failing test**

Create `tests/Findra.Tests/Content/IndexerWatchTests.cs`:

```csharp
using Findra;
using Xunit;

public class IndexerWatchTests
{
    private const long Now = 1_000_000;
    private static string Beat(long secondsAgo) => (Now - secondsAgo).ToString(System.Globalization.CultureInfo.InvariantCulture);

    [Fact]
    public void AChildThatBeatRecentlyIsLeftAlone()
        => Assert.False(IndexerWatch.ShouldRestart(true, true, 10, Beat(5), Now));

    [Fact]
    public void AChildThatHasNotBeatenForThreeMinutesIsRestarted()
        => Assert.True(IndexerWatch.ShouldRestart(true, true, 10, Beat(IndexerWatch.StallSeconds + 1), Now));

    [Fact]
    public void NothingIsRestartedWhenThereIsNoChild()
        => Assert.False(IndexerWatch.ShouldRestart(false, true, 10, Beat(9999), Now));

    [Fact]
    public void NothingIsRestartedWhenReadingIsOff()
        => Assert.False(IndexerWatch.ShouldRestart(true, false, 10, Beat(9999), Now));

    [Fact]
    public void NothingIsRestartedWhenTheQueueIsEmpty()
        => Assert.False(IndexerWatch.ShouldRestart(true, true, 0, Beat(9999), Now));

    [Fact]
    public void AMissingOrUnreadableBeatIsNotEvidenceOfAStall()
    {
        Assert.False(IndexerWatch.ShouldRestart(true, true, 10, null, Now));
        Assert.False(IndexerWatch.ShouldRestart(true, true, 10, "not a number", Now));
    }

    [Fact]
    public void ABeatFromTheFutureIsAClockThatMovedRatherThanAStall()
        => Assert.False(IndexerWatch.ShouldRestart(true, true, 10, (Now + 500).ToString(System.Globalization.CultureInfo.InvariantCulture), Now));
}
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Findra.Tests --filter IndexerWatchTests`
Expected: FAIL, `IndexerWatch` does not exist.

- [ ] **Step 3: Write the rule**

Create `src/Findra/Content/IndexerWatch.cs`:

```csharp
using System;
using System.Globalization;

namespace Findra;

/// <summary>
/// When a child that is alive has stopped being useful.
///
/// <para><b>The attempts counter cannot see this.</b> It is spent before a file is opened so that
/// a decoder which takes the process down is written off - but a HANG is not a crash. The attempt
/// never ends, the child never exits, nothing restarts it, and the queue stops at that file for as
/// long as the machine is on. The only thing that used to spend an attempt was somebody restarting
/// Findra.</para>
///
/// <para>Pure, and asked by the interface's pump, which already comes round every 400 ms.</para>
/// </summary>
public static class IndexerWatch
{
    /// <summary>How long a live child may go without reporting progress before it is restarted.
    /// Every decoder beats as it works, and the queue's own rests are seconds, so three minutes is
    /// far past anything healthy - and a false restart costs one attempt out of three, while a
    /// missed stall costs the whole queue.</summary>
    public const int StallSeconds = 180;

    public static bool ShouldRestart(bool hostRunning, bool reading, long pending, string? beat, long nowUnix)
    {
        if (!hostRunning || !reading || pending <= 0) return false;
        if (!long.TryParse(beat, NumberStyles.Integer, CultureInfo.InvariantCulture, out long at)) return false;
        return nowUnix - at > StallSeconds;
    }
}
```

- [ ] **Step 4: Run them and watch them pass**

Run: `dotnet test tests/Findra.Tests --filter IndexerWatchTests`
Expected: PASS.

- [ ] **Step 5: Give the host a kill, with a seam**

In `src/Findra/Content/IndexerHost.cs`:

```csharp
    /// <summary>Stop a child that has stopped making progress, so that its attempt is spent and
    /// the next one starts. The restart is the ordinary one: a stalled file is not a process storm,
    /// and escalating the crash backoff to five minutes would make a file that hangs every time
    /// take the rest of the afternoon to be written off.</summary>
    public void Kill(string reason) => Kill(reason, m => Log.Warn("index", m), () =>
    {
        lock (_gate)
        {
            if (_proc is not { HasExited: false }) return false;
            _proc.Kill();
            _proc.Dispose();
            _proc = null;
            _restarts = 0;
            _lastStart = DateTime.MinValue;
            return true;
        }
    });

    /// <summary>The effects as delegates, so a test can assert what was killed and what was said
    /// without a process.</summary>
    public static void Kill(string reason, Action<string> log, Func<bool> kill)
    {
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(kill);
        if (kill()) log(reason);
    }
```

Add to `tests/Findra.Tests/Content/IndexerWatchTests.cs`:

```csharp
    [Fact]
    public void KillingSaysWhyAndOnlySaysItWhenSomethingWasKilled()
    {
        var said = new List<string>();
        IndexerHost.Kill("no progress on big.avi for 3 min", said.Add, () => true);
        Assert.Single(said);

        said.Clear();
        IndexerHost.Kill("nothing to kill", said.Add, () => false);
        Assert.Empty(said);
    }
```

- [ ] **Step 6: Ask the question in the pump**

In `src/Findra/App/App.axaml.cs`, inside `PumpIndexer`'s `try`, after the `EnsureRunning` line:

```csharp
            if (reading && db.PendingCount() > 0) host.EnsureRunning();

            // A child that is alive and has stopped reporting progress is a hung decoder. The
            // attempt was counted before the file was opened, so killing it now is what lets that
            // file be written off after three tries instead of holding the queue for ever.
            if (IndexerWatch.ShouldRestart(host.Running, reading, db.PendingCount(),
                                           db.Get("indexer:beat"), DateTimeOffset.UtcNow.ToUnixTimeSeconds()))
            {
                string current = db.Get("indexer:current") ?? "";
                host.Kill($"the indexer made no progress on {(current.Length > 0 ? current : "the file it was reading")} " +
                          $"for {IndexerWatch.StallSeconds.ToString(CultureInfo.InvariantCulture)} s - restarting it");
            }
```

- [ ] **Step 6b: Beat inside document extraction, before the watchdog can kill for its absence**

Task 5 beats once *after* `DocText.Extract(path)` returns. `IndexStatus` already records that a
180 MB document takes minutes inside that one call, and `Decoders.MaxDocBytes` allows 200 MB - so a
watchdog that kills after three minutes of silence would kill a healthy child reading a large
document, spend its attempt, and write the file off after three tries. That is the change meant to
free the queue destroying files instead.

Thread the beat into the extraction itself, in `src/Findra/Content/DocText.cs`: `Extract` takes an
optional `Action? beat = null` and calls it once per page or per section - wherever its loop
already is - and `Decoders.Document` passes `Beat`. Where a format has no loop to hang it on, say
so in the code rather than inventing one.

Add a test that a callback passed to `Extract` is invoked more than once for a multi-page document,
using a document the test writes itself in whatever format `DocText` can already read without a
model.

- [ ] **Step 7: Say it in the write-off and in the probe**

In `src/Findra/Content/Indexer.cs`, `WriteOffIfItKeepsComingBack`, change the recorded reason and
the log line so both cover a hang as well as a crash:

```csharp
        Log.Warn("index", $"{Path.GetFileName(item.Path)} has been given " +
                          $"{ContentDb.MaxAttempts.ToString(CultureInfo.InvariantCulture)} attempts and ended or " +
                          "stalled in each of them; it is written off so the rest of the queue can move");
```

and the Upsert's error string becomes
`"this file stopped or ended every attempt to read it"`.

Search the tests for the old sentence and update whatever asserts it:
`grep -rn "ended every attempt" tests src`

In `src/Findra/Diagnostics/SearchProbe.cs`, `Indexer()`, add the progress line after the running
line:

```csharp
            string running = Label + IndexStatus.Running(pid, db.Get("indexer:state"),
                                                         db.Get("indexer:current"), db.Get("indexer:rate"));
            if (long.TryParse(db.Get("indexer:beat"), NumberStyles.Integer, CultureInfo.InvariantCulture, out long beat))
            {
                long ago = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - beat;
                running += Environment.NewLine +
                           $"                           last progress {ago.ToString("N0", CultureInfo.InvariantCulture)} s ago" +
                           (ago > IndexerWatch.StallSeconds ? "  (STALLED - the interface restarts it)" : "");
            }
            return running;
```

- [ ] **Step 8: Run everything, build clean, commit**

Run: `dotnet test` then `dotnet build -warnaserror -t:Rebuild`

```bash
git add src/Findra tests/Findra.Tests CHANGELOG.md
git commit -m "Restart an indexer that has stopped making progress"
```

`CHANGELOG.md`, under `### Fixed`:

```markdown
- **One file can no longer hold up everything waiting behind it.** A decoder that hung rather than
  crashed was never noticed: the indexer stayed alive, the file was never given up on, and nothing
  else in the queue was read until Findra was restarted. A file that stops making progress for
  three minutes now ends that attempt, and after three attempts the file is set aside with a reason
  and the queue moves on.
```

---

### Task 7: A missing codec is a fact, and it comes back

**Files:**
- Modify: `src/Findra/Content/ContentDb.cs` (prefix re-queue, codec counts)
- Create: `src/Findra/Content/VideoDecoders.cs`
- Modify: `src/Findra/App/App.axaml.cs` (fingerprint check in the pump)
- Modify: `src/Findra/Diagnostics/SearchIndex.cs`
- Create: `tests/Findra.Tests/Content/VideoDecodersTests.cs`
- Modify: `tests/Findra.Tests/Content/ContentDbTests.cs` (or the nearest existing db test file)

**Interfaces:**
- Consumes: `Decoders.NoVideoCodec` (Task 3).
- Produces:
  - `ContentDb.RequeueKinds(..., IReadOnlyList<string>? onlyBecauseStartingWith = null)`
  - `ContentDb.BlockedVideoCodecs() -> IReadOnlyList<(string Codec, long Count)>`
  - `VideoDecoders.Fingerprint() -> string`
  - `VideoDecoders.CodecFromReason(string recorded) -> string?`

- [ ] **Step 1: Write the failing tests**

Create `tests/Findra.Tests/Content/VideoDecodersTests.cs`:

```csharp
using Findra;
using Xunit;

public class VideoDecodersTests
{
    [Fact]
    public void TheCodecIsReadBackOutOfTheRecordedReason()
    {
        Assert.Equal("HEVC", VideoDecoders.CodecFromReason(Decoders.NoVideoCodec + " (HEVC)"));
        Assert.Null(VideoDecoders.CodecFromReason(Decoders.NoVideoCodec));
        Assert.Null(VideoDecoders.CodecFromReason("no text"));
    }

    [Fact]
    public void TheFingerprintIsTheSameTwiceRunningAndSaysSomething()
    {
        string a = VideoDecoders.Fingerprint();
        string b = VideoDecoders.Fingerprint();
        Assert.Equal(a, b);
        Assert.NotEqual("", a);
    }
}
```

In the db tests, add:

```csharp
    [Fact]
    public void VideosBlockedOnACodecAreRequeuedTogetherWhateverCodecTheyName()
    {
        using ContentDb db = Open();
        using (var tx = db.Begin())
        {
            db.Upsert("C", 1, @"C:\a.mp4", ResultKind.Video, 1, 10, ContentDb.StateSkipped,
                      Decoders.NoVideoCodec + " (HEVC)", [], tx);
            db.Upsert("C", 2, @"C:\b.mov", ResultKind.Video, 1, 10, ContentDb.StateSkipped,
                      Decoders.NoVideoCodec + " (cvid)", [], tx);
            db.Upsert("C", 3, @"C:\c.mp4", ResultKind.Video, 1, 10, ContentDb.StateSkipped,
                      Decoders.NoFrames, [], tx);
            tx.Commit();
        }

        int n = db.RequeueKinds([(int)ResultKind.Video], Indexer.Recheck,
                                onlyBecauseStartingWith: [Decoders.NoVideoCodec]);
        Assert.Equal(2, n);

        var counts = db.BlockedVideoCodecs();
        Assert.Equal(2, counts.Count);
        Assert.Contains(counts, c => c.Codec == "HEVC" && c.Count == 1);
    }
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Findra.Tests --filter "VideoDecodersTests|VideosBlockedOnACodec"`
Expected: FAIL.

- [ ] **Step 3: Add the prefix filter and the counts**

In `ContentDb.RequeueKinds`, add the parameter and a third filter arm, before the `onlyBecause`
arm:

```csharp
    public int RequeueKinds(int[] kinds, string reason,
                            IReadOnlyList<string>? notBecause = null,
                            IReadOnlyList<string>? onlyBecause = null,
                            IReadOnlyList<string>? onlyBecauseStartingWith = null)
```

```csharp
            if (onlyBecauseStartingWith is { Count: > 0 })
            {
                // A prefix rather than the whole sentence: the recorded reason names the codec in
                // brackets after it, and a list of every codec anybody might have is a list that
                // will be wrong on somebody's machine.
                var named = onlyBecauseStartingWith.Select((_, i) => $"error LIKE $s{i.ToString(CultureInfo.InvariantCulture)} || '%'");
                filter = " AND (" + string.Join(" OR ", named) + ")";
                for (int i = 0; i < onlyBecauseStartingWith.Count; i++)
                    cmd.Parameters.AddWithValue($"$s{i.ToString(CultureInfo.InvariantCulture)}", onlyBecauseStartingWith[i]);
            }
            else if (onlyBecause is { Count: > 0 })
```

Add the counts, beside `CountSkippedFor`:

```csharp
    /// <summary>Videos passed over because Windows has no decoder for them, by codec. What
    /// Settings reports and what <c>--searchindex</c> groups: "212 videos need HEVC" is something
    /// a person can act on, where "212 videos were skipped" is not.</summary>
    public IReadOnlyList<(string Codec, long Count)> BlockedVideoCodecs()
    {
        var list = new List<(string, long)>();
        using var cmd = _c.CreateCommand();
        cmd.CommandText = "SELECT error, COUNT(*) FROM items WHERE kind=$k AND state=$s AND error LIKE $p || '%' GROUP BY error";
        cmd.Parameters.AddWithValue("$k", (int)ResultKind.Video);
        cmd.Parameters.AddWithValue("$s", StateSkipped);
        cmd.Parameters.AddWithValue("$p", Decoders.NoVideoCodec);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            string codec = VideoDecoders.CodecFromReason(r.GetString(0)) ?? "unknown";
            list.Add((codec, r.GetInt64(1)));
        }
        return list;
    }
```

- [ ] **Step 4: Write `VideoDecoders`**

Create `src/Findra/Content/VideoDecoders.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace Findra;

/// <summary>
/// Which video decoders this machine has, and when that changes.
///
/// <para>Findra cannot read a codec Windows has no decoder for, and the common case is not exotic:
/// HEVC is what recent phones record and it is not in Windows by default. Those files are recorded
/// as passed over, with the codec named - and the moment somebody installs the codec, exactly
/// those files are worth reading again. Nothing else would ever notice: the journal reports
/// changed files, and a folder of holiday videos does not change.</para>
/// </summary>
[SupportedOSPlatform("windows10.0.19041.0")]
public static class VideoDecoders
{
    /// <summary>The meta row holding the last fingerprint seen.</summary>
    public const string Key = "index:videodecoders";

    private static readonly Guid VideoDecoderCategory = new("d6c02d4b-6833-45b4-971a-05a4b04bab91");
    private const int EnumFlagAll = 0x0000003F;

    [DllImport("mfplat.dll")]
    private static extern int MFTEnumEx(Guid category, int flags, IntPtr inputType, IntPtr outputType,
                                        out IntPtr activates, out int count);
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr p);

    private static readonly Guid TransformClsid = new("6821c42b-65a4-4e82-99bc-9a88205ecd0c");

    /// <summary>A short, stable description of every video decoder registered right now. Compared
    /// with the last one seen; when it differs, the videos blocked on a codec are queued again.
    /// Empty when the platform will not answer, which is read as "nothing changed" rather than as a
    /// reason to re-read a disk.</summary>
    public static string Fingerprint()
    {
        IntPtr activates = IntPtr.Zero;
        try
        {
            MediaFoundation.Startup();
            if (MFTEnumEx(VideoDecoderCategory, EnumFlagAll, IntPtr.Zero, IntPtr.Zero, out activates, out int count) < 0)
                return "";

            var ids = new List<string>(count);
            for (int i = 0; i < count; i++)
            {
                IntPtr activate = Marshal.ReadIntPtr(activates, i * IntPtr.Size);
                try
                {
                    if (MediaFoundation.Call<MediaFoundation.GetGuidFn>(activate, MediaFoundation.GetGUID)(
                            activate, TransformClsid, out Guid clsid) >= 0)
                        ids.Add(clsid.ToString());
                }
                finally { MediaFoundation.Release(activate); }
            }
            ids.Sort(StringComparer.Ordinal);
            byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(",", ids)));
            return count.ToString(CultureInfo.InvariantCulture) + ":" + Convert.ToHexString(hash)[..16];
        }
        catch (Exception ex)
        {
            Log.Once("index|mftenum", "WARN", "index", "the video decoders could not be listed :: " + ex.Message);
            return "";
        }
        finally { if (activates != IntPtr.Zero) CoTaskMemFree(activates); }
    }

    /// <summary>The codec out of a recorded reason, or null when that reason is not about a codec.
    /// </summary>
    public static string? CodecFromReason(string recorded)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        if (!recorded.StartsWith(Decoders.NoVideoCodec, StringComparison.Ordinal)) return null;
        int open = recorded.IndexOf('(', Decoders.NoVideoCodec.Length);
        int close = recorded.LastIndexOf(')');
        return open > 0 && close > open + 1 ? recorded[(open + 1)..close] : null;
    }
}
```

- [ ] **Step 5: Check it in the pump**

In `PumpIndexer`, after the watchdog block:

```csharp
            // Installing a codec is the one thing that makes a blocked video readable, and nothing
            // else would ever queue it again: the journal reports files that change, and these do
            // not. Asked here rather than at startup because the Store installs it while Findra is
            // running.
            if (_decoderCheck.ElapsedMilliseconds > 5 * 60 * 1000)
            {
                _decoderCheck.Restart();
                string now = VideoDecoders.Fingerprint();
                string was = db.Get(VideoDecoders.Key) ?? "";
                if (now.Length > 0 && was.Length > 0 && now != was)
                {
                    int n = db.RequeueKinds([(int)ResultKind.Video], Indexer.Recheck,
                                            onlyBecauseStartingWith: [Decoders.NoVideoCodec, Decoders.NoContainerReader]);
                    Log.Info("index", $"the video decoders on this machine changed: {n.ToString("N0", CultureInfo.InvariantCulture)} " +
                                      "video(s) that needed a codec are queued to be read");
                }
                if (now.Length > 0 && now != was) db.Set(VideoDecoders.Key, now);
            }
```

with the field beside `_indexerPaused`:

```csharp
    // Every five minutes rather than every pass: the pump comes round every 400 ms and enumerating
    // the platform's transforms is not free.
    private readonly Stopwatch _decoderCheck = Stopwatch.StartNew();
```

- [ ] **Step 6: Group them in `--searchindex`**

`--searchindex` renders a pure `IndexSnapshot` and never touches a database at the print site -
that separation is what makes its report testable with no index on disk - so the counts travel on
the snapshot rather than being read while printing.

In `src/Findra/Diagnostics/SearchIndex.cs`, add a field to `IndexSnapshot`, last, after
`TooLongRecordings`:

```csharp
    long TooLongRecordings,
    // Videos Windows has no decoder for, by codec. Grouped rather than totalled for the same
    // reason the capability counts are: "212 videos were skipped" tells nobody what to do, and
    // "212 videos need HEVC" does.
    IReadOnlyList<(string Codec, long Count)> BlockedVideos);
```

Populate it where the snapshot is built, beside the capability counts that already read the
database (`caps.Add(...)`, near the `new IndexSnapshot(...)` call), by passing
`db.BlockedVideoCodecs()` as the new argument.

Render it in `SearchIndexReport.Render`, beside the line reporting recordings passed over for
length, using the `Line` helper and `N` formatter already in scope there:

```csharp
        if (s.BlockedVideos.Count > 0)
        {
            Line();
            foreach ((string codec, long count) in s.BlockedVideos.OrderByDescending(b => b.Count))
                Line($"              {N(count)} video(s) need a codec Windows has not got: {codec}");
            Line("              Installing it makes exactly those files readable; nothing else is re-read.");
        }
```

Every existing construction of `IndexSnapshot` in the tests needs the new argument; `[]` is right
for the ones that are not about this. `ExplainFile` needs no change: the recorded reason already
names the codec, and `Verdict`'s skipped arm prints it.

- [ ] **Step 7: Run everything, build clean, commit**

Run: `dotnet test` then `dotnet build -warnaserror -t:Rebuild`

```bash
git add src/Findra tests/Findra.Tests CHANGELOG.md
git commit -m "Name the codec a video needs, and read it when the codec arrives"
```

`CHANGELOG.md`, under `### Added`:

```markdown
- **Videos Windows cannot decode say so, by name.** A video whose codec Windows has no decoder for
  is recorded as needing that codec rather than as unreadable, `findra --searchindex` groups them,
  and installing the codec queues exactly those files to be read. Nothing else is re-read.
```

---

### Task 8: Settings says how many videos need a codec

**Files:**
- Modify: `src/Findra/Settings/SettingsModel.cs`
- Modify: `src/Findra/Settings/SettingsActions.cs`
- Modify: `src/Findra/Settings/SettingsWindow.cs`
- Modify: `src/Findra/App/App.axaml.cs`
- Create: `src/Findra/Settings/VideoCodecStore.cs`
- Modify: `src/Findra/Diagnostics/SearchShot.cs`
- Modify: `tests/Findra.Tests/Settings/SettingsModelTests.cs`

**Interfaces:**
- Consumes: `ContentDb.BlockedVideoCodecs()` (Task 7).
- Produces:
  - `VideoCodecStore.ProductFor(string codec) -> string?`, `VideoCodecStore.PageFor(string productId) -> string`
  - `SettingsState.BlockedVideos { get; init; }` (long) and `SettingsState.BlockedCodec { get; init; }` (string?)
  - `ControlId.VideoCodec`, `SettingsAction.OpenCodecStore`, `ISettingsHost.OpenCodecStore(string productId)`

- [ ] **Step 1: Write the failing tests**

Add to `tests/Findra.Tests/Settings/SettingsModelTests.cs`:

```csharp
    private static SettingsState Content(long blocked, string? codec, bool photos = true) =>
        new(Config.Default with { IndexContent = true })
        {
            Section = Section.Content,
            Installed = photos ? new CapabilitySet(new HashSet<Capability> { Capability.Photos }) : CapabilitySet.None,
            BlockedVideos = blocked,
            BlockedCodec = codec,
        };

    [Fact]
    public void WithNothingBlockedThePhotoRowSaysOnlyThatItIsInstalled()
    {
        Control row = SettingsModel.Controls(Content(0, null)).Single(c => c.Tag == (int)Capability.Photos);
        Assert.Equal(ControlKind.Text, row.Kind);
        Assert.Equal("installed", row.Value);
    }

    [Fact]
    public void VideosNeedingACodecYouCanGetAreOfferedAsAButton()
    {
        Control row = SettingsModel.Controls(Content(212, "HEVC")).Single(c => c.Id == ControlId.VideoCodec);
        Assert.Equal(ControlKind.Button, row.Kind);
        Assert.Contains("212", row.Value, StringComparison.Ordinal);
        Assert.Contains("HEVC", row.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void VideosNeedingACodecNobodySellsAreReportedAndNotOffered()
    {
        Control row = SettingsModel.Controls(Content(6, "cvid")).Single(c => c.Id == ControlId.VideoCodec);
        Assert.Equal(ControlKind.Text, row.Kind);
        Assert.Contains("6", row.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void PressingTheCodecRowOpensThatCodecsPage()
    {
        SettingsState s = Content(212, "HEVC");
        int row = SettingsModel.Controls(s).ToList().FindIndex(c => c.Id == ControlId.VideoCodec);
        SettingsOutcome o = SettingsModel.Apply(s, new PanelHit(PanelTarget.Control, row, -1));
        Assert.Equal(SettingsAction.OpenCodecStore, o.Action);
        Assert.Equal(VideoCodecStore.ProductFor("HEVC"), o.Argument);
    }
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Findra.Tests --filter SettingsModelTests`
Expected: FAIL.

- [ ] **Step 3: Implement**

Create `src/Findra/Settings/VideoCodecStore.cs`:

```csharp
using System;

namespace Findra;

/// <summary>
/// The codecs Microsoft sells a decoder for, and where to get it.
///
/// <para>Deliberately short. HEVC is the case that matters - recent phones record in it and
/// Windows does not decode it out of the box - and it is the only one with a page worth sending
/// somebody to. The rest are old formats nobody publishes a decoder for any more, and offering a
/// button that leads nowhere is worse than reporting the fact plainly.</para>
///
/// <para><b>It is not free, and nothing here may say it is.</b> The extension published for device
/// manufacturers to pre-install only installs on machines that shipped with it.</para>
/// </summary>
public static class VideoCodecStore
{
    /// <summary>The Store listing for a codec, or null when there is nothing to offer.</summary>
    public static string? ProductFor(string codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return codec.ToUpperInvariant() switch
        {
            "HEVC" or "HEV1" or "HVC1" => "9NMZLZ57R3T7",
            _ => null,
        };
    }

    /// <summary>The Store's own page for a listing, for a machine where the Store app will not
    /// open.</summary>
    public static string PageFor(string productId) =>
        "https://apps.microsoft.com/detail/" + productId.ToLowerInvariant();

    /// <summary>What the Store app answers to.</summary>
    public static string LinkFor(string productId) => "ms-windows-store://pdp/?ProductId=" + productId;
}
```

In `SettingsModel.cs`: add `VideoCodec` to `ControlId`, `OpenCodecStore` to `SettingsAction`, the
two `SettingsState` properties, and the row. In `Content(SettingsState s)`, replace the capability
loop's installed arm:

```csharp
        foreach (Capability c in Capabilities.All)
        {
            if (c == Capability.Hebrew && !s.HebrewOffered) continue;
            if (!s.Installed.Has(c))
            {
                rows.Add(Control.Plain(ControlId.Capability, ControlKind.Button, Capabilities.Title(c),
                                       s.Waiting(ControlId.Capability) ? "Downloading..." :
                                       Sizes.Human(Capabilities.MarginalBytes(c, s.Installed)), tag: (int)c));
                continue;
            }

            // The blocked count belongs on THIS row rather than on one of its own: the pane is a
            // fixed rectangle and is already full, and a video Findra cannot read is a fact about
            // what this capability can do here.
            if (c == Capability.Photos && s.BlockedVideos > 0)
            {
                string n = s.BlockedVideos.ToString("N0", Fixed);
                string? product = s.BlockedCodec is { } codec ? VideoCodecStore.ProductFor(codec) : null;
                rows.Add(product is not null
                    ? Control.Plain(ControlId.VideoCodec, ControlKind.Button, Capabilities.Title(c),
                                    $"{n} need {s.BlockedCodec}", tag: (int)c)
                    : Control.Plain(ControlId.VideoCodec, ControlKind.Text, Capabilities.Title(c),
                                    $"installed, {n} unreadable", tag: (int)c));
                continue;
            }

            rows.Add(Control.Plain(ControlId.Capability, ControlKind.Text, Capabilities.Title(c), "installed", tag: (int)c));
        }
```

In `Apply`, beside the other arms:

```csharp
            ControlId.VideoCodec when s.BlockedCodec is { } codec && VideoCodecStore.ProductFor(codec) is { } product =>
                SettingsOutcome.Ask(s, SettingsAction.OpenCodecStore, product),
```

In `SettingsActions.cs`, add to the interface and the dispatch:

```csharp
    /// <summary>Open the Store page for a codec Windows has not got. Findra installs nothing
    /// itself - the same rule updates follow - so this opens a page and gets out of the way.
    /// </summary>
    void OpenCodecStore(string productId);
```

```csharp
            case SettingsAction.OpenCodecStore: host.OpenCodecStore(argument); return;
```

In `App.axaml.cs`, implement it on `Shell`:

```csharp
    void ISettingsHost.OpenCodecStore(string productId)
    {
        // The Store app first, because that is where the install happens; the web listing when it
        // will not open, which is the machine where the Store has been removed.
        try { Process.Start(new ProcessStartInfo(VideoCodecStore.LinkFor(productId)) { UseShellExecute = true }); }
        catch (Exception first)
        {
            Log.Warn("settings", "the Store would not open for a codec: " + first.Message);
            try { Process.Start(new ProcessStartInfo(VideoCodecStore.PageFor(productId)) { UseShellExecute = true }); }
            catch (Exception second) { Log.Warn("settings", "nor would the page: " + second.Message); }
        }
    }
```

In `SettingsWindow.cs`, extend the push so the open window keeps up:

```csharp
    public void UseIndexState(bool everIndexed, bool indexerAlive, long pending, long indexed,
                              long blockedVideos, string? blockedCodec) =>
        _canvas.Refresh(s => s.EverIndexed == everIndexed && s.IndexerAlive == indexerAlive
                             && s.Pending == pending && s.Indexed == indexed
                             && s.BlockedVideos == blockedVideos && s.BlockedCodec == blockedCodec
            ? s
            : s with { EverIndexed = everIndexed, IndexerAlive = indexerAlive, Pending = pending, Indexed = indexed,
                       BlockedVideos = blockedVideos, BlockedCodec = blockedCodec });
```

In `App.axaml.cs`, compute the two values where the counts are already pushed, and only while the
window is open - the query is a grouped scan and nothing else needs it:

```csharp
        long blocked = 0; string? blockedCodec = null;
        if (SettingsWindow.Open is not null)
        {
            IReadOnlyList<(string Codec, long Count)> byCodec = db.BlockedVideoCodecs();
            foreach ((string codec, long count) in byCodec) blocked += count;
            // The codec with a decoder somebody can install decides the row; otherwise the
            // commonest one, so the row can still report the number.
            blockedCodec = byCodec.OrderByDescending(b => VideoCodecStore.ProductFor(b.Codec) is not null)
                                  .ThenByDescending(b => b.Count)
                                  .Select(b => b.Codec)
                                  .FirstOrDefault();
        }
```

and pass both into the `UseIndexState` call and the `SettingsState` built when the window opens.

In `SearchShot.cs`, the settings state gains an installed Photos capability and a blocked count, so
the new row is rendered by a shot rather than shipped unlooked at:

```csharp
            Installed = new CapabilitySet(new HashSet<Capability> { Capability.Meaning, Capability.Photos }),
            BlockedVideos = 212, BlockedCodec = "HEVC",
```

- [ ] **Step 4: Run the settings tests and watch them pass**

Run: `dotnet test tests/Findra.Tests --filter SettingsModelTests`
Expected: PASS, including the existing sweeps
`EveryDrawnControlDoesSomethingWhenItIsClicked`, `EverySectionFitsTheFixedPaneWithItsNotesInIt`,
`EveryRowLabelFitsItsOwnColumn` and `EveryOptionLabelFitsThePillItIsDrawnIn`.
If the pane no longer fits, shorten the row's value text - never widen the column or the pane.

- [ ] **Step 5: Redraw the shots the README and the site show**

Run:
```bash
dotnet publish -c Release --self-contained -r win-x64 -o publish/win-x64 src/Findra/Findra.csproj
pwsh -File build/Make-Shots.ps1 -Exe publish/win-x64/findra.exe
dotnet test tests/Findra.Tests --filter SiteShotTests
```
Expected: `docs/shots` and `website/public/shots` agree, and the settings picture shows the new row.

- [ ] **Step 6: Build clean and commit**

```bash
git add src/Findra tests/Findra.Tests docs/shots website/public/shots CHANGELOG.md
git commit -m "Settings reports videos that need a codec Windows has not got"
```

`CHANGELOG.md`, under `### Added`:

```markdown
- **Settings says when videos need a codec.** Under Content, the Photos and video row reports how
  many videos Windows has no decoder for, and where the codec can be had when there is one to get.
  Findra installs nothing itself. Those videos are read as soon as the codec is there.
```

---

### Task 9: Re-read what the old decoder stored

**Files:**
- Modify: `src/Findra/Content/ContentDb.cs` (schema 6, `ReplaceSegments`, `ResetAttempts`, `RequeueFailed`)
- Modify: `src/Findra/Content/Indexer.cs` (`Reframe`)
- Modify: `src/Findra/Content/Decoders.cs` (`DecodeFrames`)
- Modify: `tests/Findra.Tests/Content/IndexerTests.cs`
- Modify: any test fake implementing `IDecoders`

**Interfaces:**
- Consumes: everything above.
- Produces:
  - `Indexer.Reframe = "reframe"`
  - `IDecoders.DecodeFrames(string path) -> KindResult`
  - `ContentDb.ReplaceSegments(string vol, ulong frn, int segKind, IReadOnlyList<Segment> segments, SqliteTransaction tx) -> List<long>`
  - `ContentDb.ResetAttempts(int[] kinds) -> int`
  - `ContentDb.RequeueFailed(int[] kinds, string reason) -> int`
  - `Migration` gains `QueueReason`, `ResetAttempts`, `IncludeFailed`

- [ ] **Step 1: Write the failing test**

Add to `tests/Findra.Tests/Content/IndexerTests.cs`:

```csharp
    [Fact]
    public void ReReadingAVideosFramesKeepsWhatWasHeardInIt()
    {
        using ContentDb db = Open();
        var frame = new ContentDb.Segment(ContentDb.SegFrame, 10, 10, 7, "");
        var speech = new ContentDb.Segment(ContentDb.SegSpeech, 0, 5, 9, "hello there");
        using (var tx = db.Begin())
        {
            db.Upsert("C", 42, @"C:\film.mkv", ResultKind.Video, 1, 100, ContentDb.StateIndexed, null,
                      [frame, speech], tx);
            tx.Commit();
        }

        var fresh = new ContentDb.Segment(ContentDb.SegFrame, 20, 20, 11, "");
        List<long> dead;
        using (var tx = db.Begin())
        {
            dead = db.ReplaceSegments("C", 42, ContentDb.SegFrame, [fresh], tx);
            tx.Commit();
        }

        Assert.Equal([7L], dead);                              // the old frame's vector, to be tombstoned
        ContentDb.ItemRow row = db.ItemByPath(@"C:\film.mkv")!.Value;
        var kinds = db.SegmentsOf(row.Id).Select(s => (s.SegKind, s.Vec)).ToList();
        Assert.Contains((ContentDb.SegSpeech, 9L), kinds);     // the transcript is untouched
        Assert.Contains((ContentDb.SegFrame, 11L), kinds);     // the frame is the new one
        Assert.DoesNotContain((ContentDb.SegFrame, 7L), kinds);
        Assert.Contains(db.Fts("hello", 10), h => h.Path == @"C:\film.mkv");   // and still findable
    }

    [Fact]
    public void AVideoWrittenOffByTheOldDecoderIsOfferedAgain()
    {
        using ContentDb db = Open();
        using (var tx = db.Begin())
        {
            db.Upsert("C", 1, @"C:\dead.avi", ResultKind.Video, 1, 10, ContentDb.StateFailed,
                      "this file stopped or ended every attempt to read it", [], tx);
            tx.Commit();
        }
        Assert.Equal(1, db.RequeueFailed([(int)ResultKind.Video], Indexer.Recheck));
        Assert.Equal(1, db.PendingCount());
    }

    [Fact]
    public void AQueuedVideosSpentAttemptsAreForgotten()
    {
        using ContentDb db = Open();
        db.Enqueue("C", 5, @"C:\slow.avi", ResultKind.Video, Indexer.Recheck);
        ContentDb.Pending item = db.TakeNext()!.Value;
        db.CountAttempt(item.Id);
        db.CountAttempt(item.Id);
        Assert.Equal(1, db.ResetAttempts([(int)ResultKind.Video]));
        Assert.Equal(0, db.TakeNext()!.Value.Attempts);
    }
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Findra.Tests --filter "ReReadingAVideosFrames|AVideoWrittenOff|AQueuedVideosSpent"`
Expected: FAIL.

- [ ] **Step 3: Implement the database half**

In `ContentDb.cs`, add beside `Upsert`:

```csharp
    /// <summary>Replace only the segments of ONE kind, leaving the rest of the item alone.
    ///
    /// <para>A video's frames were read by a decoder that has changed; what was HEARD in it has
    /// not. Re-reading the whole file would re-transcribe every video under the transcription
    /// limit - hours of work to arrive at the transcript already held. Returns the vector rows the
    /// replaced segments carried, to be tombstoned after the commit, for the reason
    /// <see cref="Upsert"/> gives.</para></summary>
    public List<long> ReplaceSegments(string vol, ulong frn, int segKind,
                                      IReadOnlyList<Segment> segments, SqliteTransaction tx)
    {
        ArgumentNullException.ThrowIfNull(segments);
        using var claim = Enter();
        long? itemId;
        using (var cmd = _c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id FROM items WHERE vol=$v AND frn=$f";
            cmd.Parameters.AddWithValue("$v", vol);
            cmd.Parameters.AddWithValue("$f", unchecked((long)frn));
            itemId = cmd.ExecuteScalar() as long?;
        }
        if (itemId is null) return [];

        var dead = new List<long>();
        using (var cmd = _c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "SELECT id, vec, text FROM segments WHERE item=$i AND kind=$k";
            cmd.Parameters.AddWithValue("$i", itemId.Value);
            cmd.Parameters.AddWithValue("$k", segKind);
            using var r = cmd.ExecuteReader();
            var rows = new List<(long Id, long Vec, string Text)>();
            while (r.Read()) rows.Add((r.GetInt64(0), r.GetInt64(1), r.GetString(2)));
            r.Close();
            foreach (var (id, vec, text) in rows)
            {
                if (vec >= 0) dead.Add(vec);
                if (text.Length > 0)
                {
                    using var f = _c.CreateCommand();
                    f.Transaction = tx;
                    f.CommandText = "INSERT INTO fts(fts, rowid, text) VALUES('delete', $r, $x)";
                    f.Parameters.AddWithValue("$r", id);
                    f.Parameters.AddWithValue("$x", text);
                    f.ExecuteNonQuery();
                }
            }
        }
        using (var cmd = _c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = "DELETE FROM segments WHERE item=$i AND kind=$k";
            cmd.Parameters.AddWithValue("$i", itemId.Value);
            cmd.Parameters.AddWithValue("$k", segKind);
            cmd.ExecuteNonQuery();
        }
        InsertSegments(itemId.Value, segments, tx);
        return dead;
    }
```

Factor the insert loop out of `Upsert` into `private void InsertSegments(long itemId,
IReadOnlyList<Segment> segments, SqliteTransaction tx)` and call it from both, so the full-text
rows are written the same way in each.

Add:

```csharp
    /// <summary>Forget attempts spent on queued files of these kinds. A file that spent attempts
    /// under a decoder that has been replaced must not be written off before the new one has seen
    /// it - which would make the files that motivated the change the ones it never reads.</summary>
    public int ResetAttempts(int[] kinds)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Length == 0) return 0;
        using var claim = Enter();
        using var cmd = _c.CreateCommand();
        cmd.CommandText = $"UPDATE pending SET attempts = 0 WHERE attempts > 0 AND kind IN ({string.Join(",", kinds)})";
        return cmd.ExecuteNonQuery();
    }

    /// <summary>Queue the FAILED rows of these kinds. <see cref="RequeueKinds"/> deliberately
    /// leaves them out - a file the decoder could not read has not changed because a capability
    /// arrived - but a file the decoder could not read is exactly what a NEW decoder is for.
    /// </summary>
    public int RequeueFailed(int[] kinds, string reason)
    {
        ArgumentNullException.ThrowIfNull(kinds);
        if (kinds.Length == 0) return 0;
        using var claim = Enter();
        int n = 0;
        using var tx = Begin();
        using (var cmd = _c.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT vol, frn, path, kind FROM items WHERE state={StateFailed.ToString(CultureInfo.InvariantCulture)} " +
                              $"AND kind IN ({string.Join(",", kinds)})";
            using var r = cmd.ExecuteReader();
            var rows = new List<(string, ulong, string, int)>();
            while (r.Read()) rows.Add((r.GetString(0), unchecked((ulong)r.GetInt64(1)), r.GetString(2), r.GetInt32(3)));
            r.Close();
            foreach (var (vol, frn, path, kind) in rows) { Enqueue(vol, frn, path, (ResultKind)kind, reason, tx); n++; }
        }
        tx.Commit();
        return n;
    }
```

Extend `Migration` and the runner:

```csharp
    public readonly record struct Migration(int To, int[] InvalidatedKinds, string Reason, bool ReWalk = false,
                                            string? QueueReason = null, bool ResetAttempts = false,
                                            bool IncludeFailed = false);
```

```csharp
            if (m.ReWalk) ClearAllUsnPositions();
            if (m.ResetAttempts) ResetAttempts(m.InvalidatedKinds);
            int n = RequeueKinds(m.InvalidatedKinds, m.QueueReason ?? Indexer.Recheck);
            if (m.IncludeFailed) n += RequeueFailed(m.InvalidatedKinds, Indexer.Recheck);
```

Add the step, and raise `SchemaVersion` to 6:

```csharp
        // Every video frame in an index older than this was taken by a decoder that returned a
        // black picture for a whole family of codecs, in about 33 seconds each. The frames have to
        // be taken again - and ONLY the frames: what was heard in a video has not changed, and
        // re-transcribing every recording under the limit would be hours of work for a transcript
        // already held. Reframe is what says so.
        //
        // Attempts are reset because a video that hung the old decoder has spent attempts it did
        // not deserve, and the failed rows are queued because "could not be read" was the old
        // decoder's verdict rather than the file's.
        //
        // No ReWalk: which files are eligible has not changed, only what is stored about them.
        new(To: 6, InvalidatedKinds: [(int)ResultKind.Video],
            Reason: "video frames were read by a decoder that stored black pictures",
            QueueReason: Indexer.Reframe, ResetAttempts: true, IncludeFailed: true),
```

- [ ] **Step 4: Implement the indexer half**

In `Indexer.cs`:

```csharp
    /// <summary>The queue reason that means "take this video's pictures again, and leave what was
    /// heard in it alone".</summary>
    public const string Reframe = "reframe";
```

The freshness check must not dequeue a reframe row untouched - the same defect a migration hit
before, where every file was queued and none re-read:

```csharp
            if (item.Reason != Recheck && item.Reason != Reframe
                && _db.StateOf(item.Vol, item.Frn) != ContentDb.StateSkipped
                && _db.IsCurrent(item.Vol, item.Frn, mtime))
```

And the frames-only path, after the `CanRead` gate and before the full `Decode`:

```csharp
            if (item.Reason == Reframe && item.Kind == ResultKind.Video
                && _db.StateOf(item.Vol, item.Frn) == ContentDb.StateIndexed)
            {
                KindResult frames = _decoders.DecodeFrames(item.Path);
                _decoders.Flush();
                List<long> old;
                using (var tx = _db.Begin())
                {
                    old = _db.ReplaceSegments(item.Vol, item.Frn, ContentDb.SegFrame, frames.Segments, tx);
                    _db.Dequeue(item.Id, tx);
                    tx.Commit();
                }
                _decoders.Release(old);
                _done++;
                return "reframed";
            }
```

In `Decoders.cs`, add to `IDecoders` and implement:

```csharp
    /// <summary>The pictures alone, for a video already in the index whose transcript is still
    /// good. Never a whole re-read: that would transcribe every recording again.</summary>
    KindResult DecodeFrames(string path);
```

```csharp
    public KindResult DecodeFrames(string path)
    {
        if (!Installed.Has(Capability.Photos)) return new KindResult([], NoModel);
        (IVideoSource? source, string? skip) = _videos(path);
        using (source)
        {
            if (source is null) return new KindResult([], skip ?? NoFrames);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            VideoTake take = VideoRead.Take(source, VideoRead.Plan(source.Seconds), Beat, () => clock.Elapsed);
            return new KindResult(Embed(take), take.Skip);
        }
    }
```

Update every test fake implementing `IDecoders` with the new member
(`grep -rn "IDecoders" tests`).

- [ ] **Step 5: Run the tests and watch them pass**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 6: Build clean and commit**

```bash
git add src/Findra tests/Findra.Tests CHANGELOG.md
git commit -m "Take the pictures again for videos read by the old decoder"
```

`CHANGELOG.md`, under `### Fixed`:

```markdown
- **Videos already in the index have their pictures taken again.** Anything indexed before this
  release may hold black frames, so Findra re-reads the pictures of every video it has - and only
  the pictures: what was heard in them is kept, so nothing is transcribed twice. Videos that were
  set aside as unreadable are tried again, and videos waiting in the queue no longer carry the
  attempts they spent on the old decoder.
```

---

### Task 9b: A reframe that throws must not destroy what it was protecting

**Files:**
- Modify: `src/Findra/Content/Indexer.cs`
- Modify: `src/Findra/Content/ContentDb.cs`
- Modify: `tests/Findra.Tests/Content/IndexerTests.cs`

**Interfaces:**
- Consumes: `ContentDb.RecordFrameOutcome`, `Indexer.Reframe` (Task 9).
- Produces: no new public API.

**Why:** the frames-only branch protects a video's transcript by replacing only its frame segments.
But if `DecodeFrames` throws, control leaves that branch and lands in `Handle`'s catch, which calls
`Upsert` with no segments at all: every segment goes, including the transcript, its vectors are
tombstoned, and the row is left `StateFailed` - which `RequeueKinds` excludes by design, so nothing
ever brings it back. That is the exact loss the branch exists to prevent, reached by the one path
nobody looked at, in new code reading files somebody else wrote.

- [ ] **Step 1: Write the failing test**

Add to `tests/Findra.Tests/Content/IndexerTests.cs`, in the style of the reframe tests already
there:

```csharp
    [Fact]
    public void AReframeThatThrowsKeepsTheTranscriptItWasProtecting()
    {
        using ContentDb db = Open();
        var frame = new ContentDb.Segment(ContentDb.SegFrame, 10, 10, 7, "");
        var speech = new ContentDb.Segment(ContentDb.SegSpeech, 0, 5, 9, "hello there");
        using (var tx = db.Begin())
        {
            db.Upsert("C", 42, @"C:\film.mkv", ResultKind.Video, 1, 100, ContentDb.StateIndexed, null,
                      [frame, speech], tx);
            tx.Commit();
        }
        db.Enqueue("C", 42, @"C:\film.mkv", ResultKind.Video, Indexer.Reframe);

        // A decoder that throws rather than returning a reason: a malformed file, or an error the
        // reader does not map to a skip.
        Indexer.DrainOnce(db, _ => { }, new ThrowingFrames());

        ContentDb.ItemRow row = db.ItemByPath(@"C:\film.mkv")!.Value;
        Assert.NotEqual(ContentDb.StateFailed, row.State);
        Assert.Contains(db.SegmentsOf(row.Id), s => s.SegKind == ContentDb.SegSpeech && s.Vec == 9);
        Assert.Contains(db.Fts("hello", 10), h => h.Path == @"C:\film.mkv");
        Assert.NotEqual("", row.Error);            // it says what happened
        Assert.Equal(0, db.PendingCount());        // and the queue moved on
    }
```

`ThrowingFrames` is a fake `IDecoders` whose `DecodeFrames` throws and whose other members behave
as the existing fakes do. Put it beside them.

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/Findra.Tests --filter AReframeThatThrowsKeepsTheTranscriptItWasProtecting`
Expected: FAIL - the transcript is gone and the row is `StateFailed`.

- [ ] **Step 3: Give the branch its own catch**

In `src/Findra/Content/Indexer.cs`, wrap the frames-only branch so a throw is recorded the way a
skip is, without discarding what the branch was protecting:

```csharp
                catch (Exception ex)
                {
                    // The transcript is not this failure's to destroy. Handle's own catch writes an
                    // empty segment set, which is right for a file being read from scratch and
                    // wrong here: it would take the speech rows with it and leave the item Failed,
                    // where nothing re-queues it.
                    Log.Once($"index|reframe|{ex.GetType().Name}", "WARN", "index",
                        $"the pictures of {Path.GetFileName(item.Path)} could not be read again :: " +
                        $"{ex.GetType().Name}: {ex.Message}");
                    using (var tx = _db.Begin())
                    {
                        _db.RecordFrameOutcome(item.Vol, item.Frn,
                            $"the pictures could not be read again :: {ex.GetType().Name}", tx);
                        _db.Dequeue(item.Id, tx);
                        tx.Commit();
                    }
                    _failed++;
                    return "FAILED";
                }
```

`RecordFrameOutcome` already chooses the state from what still answers for the file, so a video
with a transcript stays indexed carrying the reason as a note, and a bare one drops to skipped.

- [ ] **Step 4: Run it and watch it pass**

Run: `dotnet test tests/Findra.Tests --filter Reframe`
Expected: PASS, including the reframe tests from the previous task.

- [ ] **Step 5: The two recorded minors, in the same method**

- `RecordFrameOutcome` writes `state` and `error` but never refreshes `indexed_at`, so a video the
  migration drops to skipped sorts in `--searchindex`'s recent skips as though it were skipped long
  ago. Set it, as `Upsert` does.
- On a machine with Speech installed and Photos absent, `CanRead(Video)` is true and `DecodeFrames`
  returns `NoModel`, so the step stamps "no decoder for this kind yet" onto every indexed video row
  - a note the ordinary path deliberately never writes there, because a capability being absent is
  not a fact about the file. Record nothing when the reason is `Decoders.NoModel` and something
  still answers for the file, which makes this branch agree with `Decoders.Video` exactly.

Add a test for the second: with a fake returning `([], Decoders.NoModel)` over an item that keeps a
transcript, the row's `error` is unchanged.

- [ ] **Step 6: Run everything, build clean, commit**

Run: `dotnet test` then `dotnet build -warnaserror -t:Rebuild`

```bash
git add src/Findra tests/Findra.Tests CHANGELOG.md
git commit -m "A failed re-read of a video's pictures keeps its transcript"
```

`CHANGELOG.md`, under `### Fixed`:

```markdown
- **Re-reading a video's pictures can no longer cost it its transcript.** If reading the pictures
  of an already-indexed video failed outright, everything stored for that file was discarded -
  including what was heard in it, which had not changed and was not being re-read. What was heard
  now survives, and the file says why its pictures are missing.
```

---

### Task 10: Write down the rules

**Files:**
- Modify: `CLAUDE.md`
- Modify: `docs/e2e-run-sheet.md` and `docs/end-to-end-checklist.md`

**Interfaces:**
- Consumes: everything above. Produces no code.

- [ ] **Step 1: Add a section to `CLAUDE.md`**

After `## The progress pill`, add `## Video frames`, saying in the file's own voice:

- Frames come from `VideoFrames` through the source reader, and `Windows.Media.Editing` is never
  used for them: it took about 33 seconds a frame for MPEG-4 Part 2 and returned black, measured on
  this machine at the size the indexer asks for.
- The four corrections, each with the file that proved it: crop to the visible area, rotate by what
  the file says, square the pixels, skip an all-zero sample after a seek.
- Empty means every pixel zero and nothing looser; a dark frame is a real frame.
- Every file has a limit: three empty frames in a row end it, five minutes is the most any video
  spends on frames, and a child that reports no progress for three minutes is restarted by the
  interface. Attempts catch crashes; only this catches hangs.
- A codec Windows has not got is a skip naming the codec, and the videos come back when the
  machine's decoders change. The Store listing is paid; nothing may call it free.
- Measured on one machine only: x64, AMD processor, NVIDIA card, software decoding, with the codec
  extensions installed. No arm64 machine has run any of it, and `ReadSample` has an acknowledged
  hang on arm64 - which is what the watchdog bounds.

- [ ] **Step 2: Add the end-to-end items**

To both documents, as new numbered items in the same style:

- Play a video library through a first pass and confirm the pill moves past the first film.
- Confirm a phone video's picture on the card is upright.
- On a machine without the HEVC extension, confirm Settings offers the codec and that installing it
  queues exactly those videos.
- Confirm an index from before this release re-reads video frames and does not re-transcribe.

- [ ] **Step 3: Commit**

```bash
git add CLAUDE.md docs CHANGELOG.md
git commit -m "Write down how video frames are read and what bounds a hung decoder"
```

`CHANGELOG.md`: no user-facing entry is needed for a documentation-only commit; add the line
`- Documentation: how video frames are read, and what is checked end to end.` under
`### Changed` so the changelog rule still holds.

---

### Task 11: The diagnostics transcribe on the chip the indexer uses

**Files:**
- Modify: `src/Findra/Models/VulkanAdapter.cs`
- Modify: `src/Findra/Diagnostics/SearchModels.cs`, `SearchIndex.cs`, `SelfTest.cs`, `SearchBench.cs`
- Modify: `tests/Findra.Tests/Models/VulkanAdapterTests.cs`

**Interfaces:**
- Consumes: `VulkanAdapter.Visible()`, `VulkanAdapter.VisibleDevices`.
- Produces: `VulkanAdapter.ReExecWithDiscrete(IReadOnlyList<string> args) -> int?` and its seam overload
  `ReExecWithDiscrete(IReadOnlyList<string> args, Func<string, string?> readEnv, Func<string?> visible, Func<IReadOnlyList<string>, string, int> run) -> int?`

**Why:** `GGML_VK_VISIBLE_DEVICES` is set in exactly one place - `IndexerHost` puts it on the indexer
child before the child exists. Every other process that opens whisper has nobody to set it, so the
speech runtime takes its default Vulkan device, which on a machine with integrated graphics beside
a card is the integrated one. That is not merely slower than the card, it is slower than the
processor.

Four entry points open whisper in their own process: `--searchmodels`, `--searchindex` when given
paths to drain, `--searchtest`, and the machine report `--searchbench` prints. **`--searchmodels`
is the worst of them**, because it is the command this project treats as the authority on which
chip does the work - and `Media.ProveItTranscribes` runs a real transcription on whatever device it
landed on. On the wrong chip that probe can produce exactly the garbled output it exists to detect,
reject the accelerated rung, and report that accelerated speech does not work on a machine where
the indexer is using it.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Findra.Tests/Models/VulkanAdapterTests.cs`:

```csharp
    [Fact]
    public void AProcessThatAlreadyHasTheVariableIsNotRestarted()
    {
        bool ran = false;
        int? code = VulkanAdapter.ReExecWithDiscrete(
            ["--searchmodels"], _ => "0", () => "1", (_, _) => { ran = true; return 0; });
        Assert.Null(code);
        Assert.False(ran);
    }

    [Fact]
    public void AMachineWithNothingDiscreteIsLeftExactlyAsItWas()
    {
        bool ran = false;
        int? code = VulkanAdapter.ReExecWithDiscrete(
            ["--searchmodels"], _ => null, () => null, (_, _) => { ran = true; return 0; });
        Assert.Null(code);
        Assert.False(ran);
    }

    [Fact]
    public void OtherwiseTheProcessIsRestartedOnceWithTheCardMadeVisibleAndItsExitCodeCarried()
    {
        var seen = new List<(IReadOnlyList<string> Args, string Visible)>();
        int? code = VulkanAdapter.ReExecWithDiscrete(
            ["--searchindex", @"D:\clip.mp4"], _ => null, () => "1",
            (args, visible) => { seen.Add((args, visible)); return 3; });

        Assert.Equal(3, code);
        (IReadOnlyList<string> args, string visible) = Assert.Single(seen);
        Assert.Equal("1", visible);
        Assert.Equal(["--searchindex", @"D:\clip.mp4"], args);
    }
```

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test tests/Findra.Tests --filter VulkanAdapterTests`
Expected: FAIL, `ReExecWithDiscrete` does not exist.

- [ ] **Step 3: Implement it**

Add to `src/Findra/Models/VulkanAdapter.cs`:

```csharp
    /// <summary>
    /// Start this process again with the speech runtime pointed at the card, and hand back the exit
    /// code it finished with. Null means nothing was done and the caller carries on.
    ///
    /// <para><b>It has to be a new process.</b> The runtime reads
    /// <see cref="VisibleDevices"/> through the C runtime's copy of the environment, which
    /// <c>Environment.SetEnvironmentVariable</c> does not reach - so a version of this that set the
    /// variable in place would read its own value back, log success, and change nothing. The
    /// indexer child gets the variable because its parent sets it before the child exists; a
    /// diagnostic somebody typed has no such parent, and this is how it gets one.</para>
    ///
    /// <para>Nothing happens when the variable is already set - which is what stops this restarting
    /// itself for ever - or when there is no card to choose, which is an ordinary machine rather
    /// than a fault.</para>
    /// </summary>
    public static int? ReExecWithDiscrete(IReadOnlyList<string> args)
        => ReExecWithDiscrete(args, Environment.GetEnvironmentVariable, Visible, Run);

    /// <summary>The effects as delegates, so the decision is testable without starting a process.
    /// </summary>
    public static int? ReExecWithDiscrete(IReadOnlyList<string> args, Func<string, string?> readEnv,
                                          Func<string?> visible, Func<IReadOnlyList<string>, string, int> run)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(readEnv);
        ArgumentNullException.ThrowIfNull(visible);
        ArgumentNullException.ThrowIfNull(run);

        if (!string.IsNullOrEmpty(readEnv(VisibleDevices))) return null;
        if (visible() is not { } device) return null;
        return run(args, device);
    }

    private static int Run(IReadOnlyList<string> args, string device)
    {
        string exe = Environment.ProcessPath ?? "";
        if (exe.Length == 0) return 0;          // nothing to restart; the caller carries on

        var start = new System.Diagnostics.ProcessStartInfo(exe)
        {
            // Inherited handles, so the restarted process writes to the console the caller is
            // already attached to. A diagnostic whose output went nowhere would be worse than a
            // slow one.
            UseShellExecute = false,
        };
        foreach (string a in args) start.ArgumentList.Add(a);
        start.Environment[VisibleDevices] = device;

        using var proc = System.Diagnostics.Process.Start(start);
        if (proc is null) return 0;
        proc.WaitForExit();
        return proc.ExitCode;
    }
```

- [ ] **Step 4: Run them and watch them pass**

Run: `dotnet test tests/Findra.Tests --filter VulkanAdapterTests`
Expected: PASS.

- [ ] **Step 5: Call it from the four modes that open whisper**

At the top of each mode's entry, before anything opens a model, and passing that mode's own
arguments:

```csharp
        if (VulkanAdapter.ReExecWithDiscrete(args) is { } code) return code;
```

`--searchmodels` (`SearchModels.cs`), `--searchindex` (`SearchIndex.cs`), `--searchtest`
(`SelfTest.cs`) and `--searchbench` (`SearchBench.cs`, whose machine report opens whisper even
though its own decoders take no capabilities).

**Not `--index`, not `--names`, and not the interface.** The indexer child already has the variable
from its parent, the names helper never transcribes, and the interface is not a diagnostic.

- [ ] **Step 6: Run everything, build clean, commit**

Run: `dotnet test` then `dotnet build -warnaserror -t:Rebuild` then
`pwsh -File build/Check-Diagnostics.ps1 -Exe publish/win-x64/findra.exe` if a publish exists, since
every mode it runs is piped and this change starts a second process.

```bash
git add src/Findra tests/Findra.Tests CHANGELOG.md
git commit -m "Diagnostics transcribe on the same chip the indexer uses"
```

`CHANGELOG.md`, under `### Fixed`:

```markdown
- **The diagnostics measured speech on the wrong chip.** `--searchmodels`, `--searchindex`,
  `--searchtest` and `--searchbench` transcribed on whichever graphics device Windows listed first,
  which on a machine with integrated graphics beside a card is the integrated one - so they were far
  slower than the indexer they exist to explain, and `--searchmodels` could report that accelerated
  speech does not work on a machine where it does.
```

---

### Task 12: Speech transcripts embed in batches

**Files:**
- Modify: `src/Findra/Content/Speech.cs`
- Modify: `src/Findra/Content/Decoders.cs`
- Modify: `tests/Findra.Tests/Content/MediaTests.cs`

**Interfaces:**
- Consumes: `E5Encoder.Batch`, `E5Encoder.EncodePassages`, `E5Encoder.Passage`.
- Produces: `Speech.Window(double T0, double T1, string Text)` and
  `Speech.Windows(IReadOnlyList<Media.Line> lines, double maxSeconds = 20, int maxChars = 600) -> List<Window>`.
  `Speech.Merge` stays, as a thin wrapper over `Windows`, so every existing windowing test keeps
  testing the windowing rule.

**Why:** a transcript's windows are embedded one at a time - `Merge` calls its `embed` callback per
window, which is a batch of one - while `Document` embeds `E5Encoder.Batch` at a time and says in
its own comment why. Batching on the accelerator is the largest single lever in a content pass,
measured at 134 segments a second against 408. An hour of speech is about 180 windows.

- [ ] **Step 1: Write the failing test**

Add to `tests/Findra.Tests/Content/MediaTests.cs`:

```csharp
    [Fact]
    public void CuttingTheWindowsAndEmbeddingThemAreSeparateSteps()
    {
        List<Media.Line> lines =
        [
            new(0, 3, "the first thing said", "en"),
            new(3, 7, "and the second", "en"),
            new(30, 34, "much later", "en"),
        ];

        // The windows a caller can embed in batches, and the segments the old path produced, have
        // to agree: same texts, same times, same order.
        List<Speech.Window> windows = Speech.Windows(lines);
        List<ContentDb.Segment> merged = Speech.Merge(lines, _ => -1);

        Assert.Equal(merged.Count, windows.Count);
        for (int i = 0; i < windows.Count; i++)
        {
            Assert.Equal(merged[i].Text, windows[i].Text);
            Assert.Equal(merged[i].T0, windows[i].T0);
            Assert.Equal(merged[i].T1, windows[i].T1);
        }
    }
```

- [ ] **Step 2: Run it and watch it fail**

Run: `dotnet test tests/Findra.Tests --filter CuttingTheWindowsAndEmbeddingThemAreSeparateSteps`
Expected: FAIL, `Speech.Windows` does not exist.

- [ ] **Step 3: Split the windowing out**

In `src/Findra/Content/Speech.cs`, add the window record and `Windows`, moving the existing loop
into it unchanged, and leave `Merge` calling it:

```csharp
    /// <summary>One window of a transcript, before anything has embedded it. Cutting and embedding
    /// are separate so the embedding can be done a batch at a time - on the accelerator that is the
    /// difference between 134 segments a second and 408 - while the windowing rule, which is the
    /// part with an off-by-one in it, stays testable with no model on disk.</summary>
    public readonly record struct Window(double T0, double T1, string Text);

    public static List<Window> Windows(IReadOnlyList<Media.Line> lines, double maxSeconds = 20, int maxChars = 600)
    {
        // the body of the old Merge, appending a Window instead of calling embed
    }

    public static List<ContentDb.Segment> Merge(IReadOnlyList<Media.Line> lines, Func<string, long> embed,
                                                double maxSeconds = 20, int maxChars = 600)
    {
        ArgumentNullException.ThrowIfNull(embed);
        var segs = new List<ContentDb.Segment>();
        foreach (Window w in Windows(lines, maxSeconds, maxChars))
            segs.Add(new ContentDb.Segment(ContentDb.SegSpeech, w.T0, w.T1, embed(w.Text), w.Text));
        return segs;
    }
```

- [ ] **Step 4: Embed them in batches**

In `src/Findra/Content/Decoders.cs`, `Transcribe` stops calling `Speech.Merge` and does what
`Document` does:

```csharp
        E5Encoder e5 = E5();
        List<Speech.Window> windows = Speech.Windows(lines);
        var segs = new List<ContentDb.Segment>(windows.Count);
        for (int i = 0; i < windows.Count; i += E5Encoder.Batch)
        {
            int n = Math.Min(E5Encoder.Batch, windows.Count - i);
            var batch = new List<string>(n);
            for (int k = 0; k < n; k++) batch.Add(E5Encoder.Passage(path, windows[i + k].Text));
            float[][] vs = e5.EncodePassages(batch);
            for (int k = 0; k < n; k++)
            {
                Speech.Window w = windows[i + k];
                segs.Add(new ContentDb.Segment(ContentDb.SegSpeech, w.T0, w.T1,
                                               Append(vs[k], ContentDb.SegSpeech), w.Text));
            }
            Beat();
        }
        return segs;
```

The order of the segments is the transcript's order, exactly as before.

- [ ] **Step 5: Run everything, build clean, commit**

Run: `dotnet test` then `dotnet build -warnaserror -t:Rebuild`

```bash
git add src/Findra tests/Findra.Tests CHANGELOG.md
git commit -m "Embed a transcript's windows a batch at a time"
```

`CHANGELOG.md`, under `### Changed`:

```markdown
- **Transcripts are embedded a batch at a time.** Each window of speech was embedded on its own,
  which on the accelerator is several times slower than embedding them together - the way words in
  documents have always been done.
```

---

## Self-Review

**Spec coverage:**

| Spec section | Task |
|---|---|
| 1. Reading frames: open, codec, duration | 1 |
| 1. Crop, rotation, pixel aspect, empty sample | 2 |
| 1. Empty/unreadable, give-up, budget, `no frames` | 3 |
| 1. Failures at the open | 1, 3 |
| 1. Tests, generated clip | 2 |
| 2. Beats at real work | 5 |
| 2. `IndexerWatch`, kill, restart, write-off wording, probe | 6 |
| 3. Reason naming the codec, prefix re-queue, fingerprint | 7 |
| 3. Settings row, Store listing, shot | 8 |
| 3. `--searchindex` grouping | 7 |
| 4. Schema 6, reframe, attempts, failed rows | 9 |
| Caveats written down | 10 |

**Type consistency checked:** `VideoFrames.FrameResult` is used by `IVideoSource.Frame`,
`VideoRead.Take` and `PreviewDecoder`; `FrameShape` is produced by `VideoFrames.ShapeOf` and
consumed by `VideoGeometry.Apply`; `Decoders.NoVideoCodec` is written by `VideoRead.Open`, read by
`ContentDb.BlockedVideoCodecs` and `VideoDecoders.CodecFromReason`, and matched as a prefix by
`RequeueKinds`; `Indexer.Reframe` is written by migration 6 and read by `Indexer.Handle`.

**Known risk, stated rather than hidden:** the vtable slot numbers in Task 1 are counted from the
interface declaration order. Task 1 Step 5 is what proves them on a real file, and Task 2's clip
test proves the rest of the chain. If either fails in a way that looks like memory corruption, the
slot table is the first thing to check.
