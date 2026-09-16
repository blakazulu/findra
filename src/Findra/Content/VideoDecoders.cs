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
