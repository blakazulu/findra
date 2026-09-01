using System.Text.Json;

namespace Findra;

/// <summary>
/// The UI's aliveness, on disk, so a headless `--searchprobe` run in a different process can
/// report whether Findra is running and which hotkey combination it holds - without a pipe,
/// since this is a fact about the UI process, not the name helper.
///
/// Written once the hotkey chain has landed (or failed to land) at startup, and removed on a
/// clean quit. A crash leaves the file behind naming a pid that is no longer running, so
/// <see cref="Read"/> treats a dead pid exactly like a missing or unreadable file: "not
/// running" must never require anyone to notice and delete a leftover file by hand.
/// </summary>
public static class UiStatus
{
    /// <summary>What was written, plus what a reader gets back.</summary>
    public readonly record struct Status(int Pid, string? Hotkey, DateTime StartedAtUtc);

    private sealed record Record(int Pid, string? Hotkey, DateTime StartedAtUtc);

    private static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>%LOCALAPPDATA%\Findra\ui.json.</summary>
    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                     "Findra", "ui.json");

    /// <summary><paramref name="path"/> defaults to <see cref="DefaultPath"/>; a test passes a
    /// temp-directory path instead so nothing here ever touches a real profile.</summary>
    public static void Write(int pid, string? hotkey, string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var record = new Record(pid, hotkey, DateTime.UtcNow);
            File.WriteAllText(path, JsonSerializer.Serialize(record, Opts));
        }
        catch (Exception ex) { Log.Warn("app", "could not write ui.json: " + ex.Message); }
    }

    public static void Clear(string? path = null)
    {
        path ??= DefaultPath;
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) { Log.Warn("app", "could not remove ui.json: " + ex.Message); }
    }

    /// <summary>Null for a missing file, unparsable content, or a pid that is no longer alive -
    /// three different facts on disk that all mean the same thing to a caller: the UI is not
    /// running.</summary>
    public static Status? Read(string? path = null)
    {
        path ??= DefaultPath;
        try
        {
            if (!File.Exists(path)) return null;
            Record? r = JsonSerializer.Deserialize<Record>(File.ReadAllText(path), Opts);
            if (r is null) return null;

            try { System.Diagnostics.Process.GetProcessById(r.Pid); }
            catch (ArgumentException) { return null; }   // no such pid: stale, from a crash or an old run

            return new Status(r.Pid, r.Hotkey, r.StartedAtUtc);
        }
        catch { return null; }   // garbage on disk reads exactly like "not running"
    }
}
