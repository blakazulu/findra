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

    /// <summary>The process name a live pid has to carry for this file to be believed. Windows
    /// reports it without the extension.</summary>
    public const string ProcessName = "findra";

    /// <summary>Null for a missing file, unparsable content, a pid that is no longer alive, or a
    /// live pid belonging to some other program - four different facts on disk that all mean the
    /// same thing to a caller: the UI is not running.
    ///
    /// <para>The name check is what stops a crash from being reported as a running Findra. The
    /// file survives a crash naming a pid Windows is free to hand to anything; once it does, a
    /// pid-only check says "running (pid N, hotkey Alt+Space)" about a process that has never
    /// heard of Findra, and the first thing anyone does with that answer is go looking for a bug
    /// in the hotkey.</para></summary>
    /// <param name="path">Defaults to <see cref="DefaultPath"/>; a test passes a temp path.</param>
    /// <param name="expectedProcessName">Defaults to <see cref="ProcessName"/>. A test overrides
    /// it because no test run is ever named findra, and a live pid it can actually produce is the
    /// only way to exercise the alive branch at all.</param>
    public static Status? Read(string? path = null, string? expectedProcessName = null)
    {
        path ??= DefaultPath;
        expectedProcessName ??= ProcessName;
        try
        {
            if (!File.Exists(path)) return null;
            Record? r = JsonSerializer.Deserialize<Record>(File.ReadAllText(path), Opts);
            if (r is null) return null;

            // Everything is caught, not just ArgumentException: GetProcessById can also throw
            // InvalidOperationException for a process that exited between the lookup and the read,
            // and Win32Exception when the name cannot be read at all. Every one of those means the
            // same thing here, and none of them may reach a caller who only asked a yes/no.
            string name;
            try { name = System.Diagnostics.Process.GetProcessById(r.Pid).ProcessName; }
            catch { return null; }   // no such pid, or nothing that can be identified: not running

            if (!string.Equals(name, expectedProcessName, StringComparison.OrdinalIgnoreCase))
                return null;         // the pid was reused; this file is a leftover

            return new Status(r.Pid, r.Hotkey, r.StartedAtUtc);
        }
        catch { return null; }   // garbage on disk reads exactly like "not running"
    }
}
