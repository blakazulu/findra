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
