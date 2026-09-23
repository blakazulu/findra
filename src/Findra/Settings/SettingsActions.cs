namespace Findra;

/// <summary>
/// The ten things a settings click can ask the machine for. An interface rather than a switch
/// inside the window, so the last link from a click to the operating system has a test.
/// </summary>
public interface ISettingsHost
{
    void OpenPalettesFile(string path);
    void BeginChordCapture();
    void SetAutostart(bool on);
    void RegisterHelper();
    void PickFolder();
    void InstallCapability(Capability capability);
    void CheckNow();

    /// <summary>Start the upgrade. Never by replacing anything itself: winget in a visible
    /// window for a winget copy, the releases page for anything else.</summary>
    void UpdateNow();

    void RecentreCapsule();
    void StartIndexing();

    /// <summary>Open the folder the log files are in, not today's file. Yesterday's is what
    /// somebody reporting "it stopped working last night" actually needs, and a folder reaches
    /// both.</summary>
    void OpenLogs();

    /// <summary>Open the Store page for a codec Windows has not got. Findra installs nothing
    /// itself - the same rule updates follow - so this opens a page and gets out of the way.
    /// </summary>
    void OpenCodecStore(string productId);

    /// <summary>Remove an add-on's files (keeping any another add-on still needs) and, when
    /// <paramref name="forget"/>, re-read what it found so its findings are dropped.</summary>
    void RemoveAddOn(Capability addOn, bool forget);
}

public static class SettingsActions
{
    /// <summary>
    /// Route one action to the host. The default arm THROWS rather than returning quietly: an
    /// action added to the enum and forgotten here is a control that is drawn and does nothing,
    /// which is the defect this interface exists to prevent, and a silent default is what lets it
    /// hide.
    /// </summary>
    public static void Dispatch(SettingsAction action, string argument, ISettingsHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        switch (action)
        {
            case SettingsAction.None: return;
            case SettingsAction.OpenPalettesFile: host.OpenPalettesFile(argument); return;
            case SettingsAction.CaptureChord: host.BeginChordCapture(); return;
            case SettingsAction.SetAutostart: host.SetAutostart(true); return;
            case SettingsAction.ClearAutostart: host.SetAutostart(false); return;
            case SettingsAction.RegisterHelper: host.RegisterHelper(); return;
            case SettingsAction.PickFolder: host.PickFolder(); return;
            case SettingsAction.CheckNow: host.CheckNow(); return;
            case SettingsAction.UpdateNow: host.UpdateNow(); return;
            case SettingsAction.RecentreCapsule: host.RecentreCapsule(); return;
            case SettingsAction.StartIndexing: host.StartIndexing(); return;
            case SettingsAction.OpenLogs: host.OpenLogs(); return;
            case SettingsAction.OpenCodecStore: host.OpenCodecStore(argument); return;

            case SettingsAction.InstallCapability:
                // The argument crossed a string boundary. A parse that falls back to the first
                // enum value starts a 629 MB download nobody asked for.
                if (Enum.TryParse(argument, ignoreCase: false, out Capability c)) host.InstallCapability(c);
                else Log.Warn("settings", $"'{argument}' names no capability; nothing was installed");
                return;

            case SettingsAction.RemoveAddOn:
            {
                // "Photos|keep". Anything that does not parse removes nothing: a fallback to the
                // first enum value would delete an add-on nobody asked about.
                string[] parts = argument.Split('|');
                if (parts.Length == 2 && Enum.TryParse(parts[0], ignoreCase: false, out Capability gone)
                    && Enum.IsDefined(gone) && parts[1] is "keep" or "forget")
                    host.RemoveAddOn(gone, forget: parts[1] == "forget");
                else Log.Warn("settings", $"'{argument}' names no add-on to remove; nothing was removed");
                return;
            }

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "no settings action arm for this value");
        }
    }
}
