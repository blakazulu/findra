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

    /// <summary>Quoted, always. An unquoted path with a space in it makes Windows run the first
    /// word and pass the rest as arguments, at every sign-in, with no error anywhere.</summary>
    public static string CommandFor(string exePath)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        return "\"" + exePath.Trim().Trim('"') + "\"";
    }

    public static bool IsSet()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath);
            return key?.GetValue(ValueName) is string s && s.Length > 0;
        }
        catch (Exception ex) { Log.Warn("startup", "could not read the autostart entry: " + ex.Message); return false; }
    }

    public static void Set(string exePath)
    {
        try
        {
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(KeyPath);
            key.SetValue(ValueName, CommandFor(exePath));
            Log.Info("startup", "Findra will start at sign-in");
        }
        catch (Exception ex) { Log.Warn("startup", "could not write the autostart entry: " + ex.Message); }
    }

    public static void Clear()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(KeyPath, writable: true);
            if (key?.GetValue(ValueName) is null) return;
            key.DeleteValue(ValueName, throwOnMissingValue: false);
            Log.Info("startup", "the autostart entry was removed");
        }
        catch (Exception ex) { Log.Warn("startup", "could not remove the autostart entry: " + ex.Message); }
    }
}
