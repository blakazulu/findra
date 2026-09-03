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
/// <para>Separate from the scheduled task: the task starts the elevated NAME HELPER at logon, and
/// without it there are no file names to search; this starts the interface, and Findra works fine
/// without it - you just have to launch it.</para>
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

    /// <summary>The only implementation anything but a test uses.</summary>
    public static IStore RunKey { get; } = new CurrentUserRunKey();

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
