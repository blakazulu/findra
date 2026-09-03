namespace Findra;

/// <summary>
/// The eight things a settings click can ask the machine for. An interface rather than a switch
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
    void RecentreCapsule();
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
            case SettingsAction.RecentreCapsule: host.RecentreCapsule(); return;

            case SettingsAction.InstallCapability:
                // The argument crossed a string boundary. A parse that falls back to the first
                // enum value starts a 629 MB download nobody asked for.
                if (Enum.TryParse(argument, ignoreCase: false, out Capability c)) host.InstallCapability(c);
                else Log.Warn("settings", $"'{argument}' names no capability; nothing was installed");
                return;

            default:
                throw new ArgumentOutOfRangeException(nameof(action), action, "no settings action arm for this value");
        }
    }
}
