using Microsoft.Win32;

namespace Findra.Startup;

/// <summary>
/// The "start when I sign in" entry, in the user's own Run key.
///
/// <para>Deliberately NOT written by the installer. An installer runs elevated, and an elevated
/// process's HKCU is the hive of whoever answered the prompt - which on a machine where an admin
/// installs for somebody else is the wrong person entirely. Findra writes it itself, from its own
/// session, where HKCU means what it says.</para>
///
/// <para>Separate from the scheduled task: the task starts the elevated NAME HELPER, and without
/// it there are no file names to search; this starts the interface, and Findra works fine without
/// it - you just have to launch it. At sign-in the helper stays only when this entry starts Findra
/// too (<see cref="HelperStart"/>).</para>
/// </summary>
public static class Autostart
{
    public const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string ValueName = "Findra";

    /// <summary>
    /// Where the one entry is kept. The real one is the signed-in user's Run key; a test passes
    /// its own, because a test that exercised the real key would rewrite whether Findra starts at
    /// sign-in on the machine running it - and would leave it wrong when it failed halfway.
    ///
    /// <para>Public rather than internal only so the round trip can be tested: this assembly
    /// grants no <c>InternalsVisibleTo</c>, and every other seam the tests reach is public too.
    /// It is a seam, not an API.</para>
    /// </summary>
    public interface IStore
    {
        string? Read();
        void Write(string value);
        void Remove();
    }

    /// <summary>The TaskId of the <c>windows.startupTask</c> in packaging/store/Package.appxmanifest.</summary>
    public const string PackageTaskId = "FindraStartup";

    /// <summary>The only implementation anything but a test uses: the Run key, or inside a Store
    /// package - where a Run-key write is redirected into the package and reaches nobody - the
    /// package's own sign-in entry, which Settings > Apps > Startup lists.</summary>
    public static IStore RunKey { get; } = Packaged.IsPackaged ? new PackageStartupTask() : new CurrentUserRunKey();

    /// <summary>Quoted, always. An unquoted path with a space in it makes Windows run the first
    /// word and pass the rest as arguments, at every sign-in, with no error anywhere.</summary>
    public static string CommandFor(string exePath)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        return "\"" + exePath.Trim().Trim('"') + "\"";
    }

    public static bool IsSet() => IsSet(RunKey);

    public static bool IsSet(IStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            return store.Read() is { Length: > 0 };
        }
        catch (Exception ex) { Log.Warn("startup", "could not read the autostart entry: " + ex.Message); return false; }
    }

    /// <summary>Where Startup apps, in Task Manager or Settings, marks an entry off.</summary>
    public const string ApprovedKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

    /// <summary>
    /// Whether Windows will start Findra at the next sign-in: the entry is there, and nobody
    /// switched it off under Startup apps. Switching it off leaves the entry where it is and
    /// writes a mark beside it whose first byte is odd (01 or 03, then the time it was switched
    /// off); 02 or no mark at all means on.
    /// </summary>
    public static bool StartsAtSignIn() =>
        // A package's entry already says whether Startup apps switched it off; there is no mark.
        StartsAtSignIn(RunKey, Packaged.IsPackaged ? () => null : ReadApproval);

    public static bool StartsAtSignIn(IStore store, Func<byte[]?> approval)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(approval);
        if (!IsSet(store)) return false;
        try
        {
            return approval() is not { Length: > 0 } mark || (mark[0] & 1) == 0;
        }
        catch (Exception ex)
        {
            // Believe the entry. Wrong this way, a helper stays that need not have; wrong the
            // other, somebody who asked for Findra at sign-in waits for names.
            Log.Warn("startup", "could not read whether Startup apps switched Findra off: " + ex.Message);
            return true;
        }
    }

    private static byte[]? ReadApproval()
    {
        using RegistryKey? key = Registry.CurrentUser.OpenSubKey(ApprovedKeyPath);
        return key?.GetValue(ValueName) as byte[];
    }

    public static void Set(string exePath) => Set(exePath, RunKey);

    public static void Set(string exePath, IStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            store.Write(CommandFor(exePath));
            Log.Info("startup", "Findra will start at sign-in");
        }
        catch (Exception ex) { Log.Warn("startup", "could not write the autostart entry: " + ex.Message); }
    }

    public static void Clear() => Clear(RunKey);

    public static void Clear(IStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        try
        {
            // Asked before removed, so an ordinary uninstall on a machine that never turned it on
            // says nothing rather than reporting a removal that did not happen.
            if (store.Read() is null) return;
            store.Remove();
            Log.Info("startup", "the autostart entry was removed");
        }
        catch (Exception ex) { Log.Warn("startup", "could not remove the autostart entry: " + ex.Message); }
    }

    /// <summary>
    /// The <c>windows.startupTask</c> the package manifest declares. Its state is the whole answer:
    /// Read names it only when Windows will start Findra, so an entry switched off under Startup
    /// apps reads as off, as the Run key's mark does. Turning it back on from inside Findra after
    /// the person switched it off there is refused by Windows, and the switch then reads off
    /// again - the log says where to turn it on.
    /// </summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows10.0.19041.0")]
    private sealed class PackageStartupTask : IStore
    {
        private static Windows.ApplicationModel.StartupTask Task() =>
            Windows.ApplicationModel.StartupTask.GetAsync(PackageTaskId).AsTask().GetAwaiter().GetResult();

        public string? Read() =>
            Task().State is Windows.ApplicationModel.StartupTaskState.Enabled
                         or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy
                ? PackageTaskId : null;

        public void Write(string value)
        {
            Windows.ApplicationModel.StartupTaskState state = Task().RequestEnableAsync().AsTask().GetAwaiter().GetResult();
            if (state is not (Windows.ApplicationModel.StartupTaskState.Enabled or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy))
                throw new InvalidOperationException(
                    $"Windows kept the sign-in entry {state}; it can be turned on in Settings > Apps > Startup");
        }

        public void Remove() => Task().Disable();
    }

    private sealed class CurrentUserRunKey : IStore
    {
        public string? Read()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) as string;
        }

        public void Write(string value)
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(ValueName, value);
        }

        public void Remove()
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
