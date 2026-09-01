using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Findra;

/// <summary>What a result does when acted on. Shared by the card and by the keyboard.</summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public static class CardActions
{
    public static void Open(SearchResult r)
    {
        try
        {
            // A matched moment opens at that time where the player takes a start position. mpv and
            // VLC do; Movies & TV does not and simply ignores the argument.
            if (r.MomentSeconds >= 0 && r.Kind == ResultKind.Video && TryOpenAt(r.Path, r.MomentSeconds)) return;
            Process.Start(new ProcessStartInfo(r.Path) { UseShellExecute = true });
            Log.Info("search", $"opened {FileKinds.Label(r.Kind).ToLowerInvariant()}: {r.Name}");
        }
        catch (Exception ex) { Log.Warn("search", $"open failed for {r.Name}: {ex.Message}"); }
    }

    private static bool TryOpenAt(string path, double seconds)
    {
        // Only when a known player owns the extension - shelling "file --start=" at an unknown
        // handler would hand it a bogus argument.
        string? handler = DefaultHandlerExe(Path.GetExtension(path));
        if (handler is null) return false;
        string exe = Path.GetFileName(handler).ToLowerInvariant();
        string args = exe switch
        {
            "mpv.exe" => $"--start={seconds:0} \"{path}\"",
            "vlc.exe" => $"--start-time={seconds:0} \"{path}\"",
            "mpc-hc64.exe" or "mpc-hc.exe" or "mpc-be64.exe" or "mpc-be.exe" => $"\"{path}\" /start {(long)(seconds * 1000)}",
            _ => ""
        };
        if (args.Length == 0) return false;
        Process.Start(new ProcessStartInfo(handler, args) { UseShellExecute = false });
        Log.Info("search", $"opened at {seconds:0}s in {exe}: {Path.GetFileName(path)}");
        return true;
    }

    public static void Reveal(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            Log.Info("search", $"revealed: {Path.GetFileName(path)}");
        }
        catch (Exception ex) { Log.Warn("search", $"reveal failed: {ex.Message}"); }
    }

    // Which program opens a file type - the shell's own association table. Only what TryOpenAt
    // needs to decide whether a known player owns the extension before handing it a start-time
    // argument; not a general shell-association API.
    private const int ASSOCSTR_EXECUTABLE = 2;

    [DllImport("shlwapi.dll", CharSet = CharSet.Unicode)]
    private static extern uint AssocQueryStringW(uint flags, int str, string pszAssoc, string? pszExtra,
        StringBuilder? pszOut, ref uint pcchOut);

    private static string? DefaultHandlerExe(string extension)
    {
        if (string.IsNullOrEmpty(extension)) return null;
        if (!extension.StartsWith('.')) extension = "." + extension;
        try
        {
            uint len = 0;
            AssocQueryStringW(0, ASSOCSTR_EXECUTABLE, extension, "open", null, ref len);
            if (len == 0) return null;
            var sb = new StringBuilder((int)len + 1);
            if (AssocQueryStringW(0, ASSOCSTR_EXECUTABLE, extension, "open", sb, ref len) != 0) return null;
            return sb.ToString();
        }
        catch { return null; }
    }
}
