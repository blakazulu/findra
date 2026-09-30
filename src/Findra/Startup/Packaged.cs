using System.Runtime.InteropServices;

namespace Findra.Startup;

/// <summary>
/// Whether this process runs inside a Microsoft Store package. A packaged copy lives in a folder
/// that moves with every update, is updated by the Store rather than by Findra, and has its
/// sign-in entry in the package rather than the Run key - so three things ask this one question.
/// </summary>
public static class Packaged
{
    private const int AppModelErrorNoPackage = 15700;

    private static readonly Lazy<bool> Answer = new(Ask);

    /// <summary>Asked once per process: a process cannot gain or lose its package.</summary>
    public static bool IsPackaged => Answer.Value;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetCurrentPackageFullName(ref uint packageFullNameLength, char[]? packageFullName);

    private static bool Ask()
    {
        try
        {
            uint length = 0;
            // With no buffer the call reports either "no package" or "buffer too small"; the
            // second is the answer, and the name itself is never needed.
            return GetCurrentPackageFullName(ref length, null) != AppModelErrorNoPackage;
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            // Older than Windows 8: there are no packages to be inside.
            return false;
        }
    }
}

/// <summary>Where Findra is in the Microsoft Store.</summary>
public static class StoreListing
{
    /// <summary>The product ID Partner Center gave Findra; it never changes.</summary>
    public const string ProductId = "9P78Z9KT48PR";

    /// <summary>Opens the Store app on Findra's page, where a waiting update has its button.</summary>
    public const string PageUri = "ms-windows-store://pdp/?ProductId=" + ProductId;
}
