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

    /// <summary>Drop one COM reference. Every caller in this file takes exactly one, so this is
    /// the one place that reference is given back - a leak or a double-release is a search
    /// somebody would have to do file by file otherwise.</summary>
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
