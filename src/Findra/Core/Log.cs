using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Findra;

// App-wide diagnostic log. One line per event, greppable:
//   2026-09-01 01:23:45.678 [WARN ] [pipe] [7284] reply generation 41 discarded, current is 44
// Files: %LOCALAPPDATA%\Findra\logs\findra-YYYYMMDD.log, 7 days retained.
//
// Two things that are easy to get wrong and were both bugs once:
//  * The file is picked from the date of the EVENT, not of process start - otherwise a session
//    that spans midnight keeps appending to the starting day's file and "today's log" is a lie.
//  * EVERY Findra process shares the day's file (the UI, the elevated --names helper, the
//    --index child, and CLI modes like --searchprobe or --searchtest). Plain File.AppendAllText
//    from two processes interleaves and tears lines, leaving NUL bytes mid-line. Appends are
//    therefore serialized on a named mutex, and each line carries its pid so one process's
//    lines can be told apart from another's.
//
// Writes are queued and flushed by a background task - hot paths never touch the disk.
// Rules of use: never log per-frame; perf data is SAMPLED (periodic summaries), and
// repeating failures are logged once per unique key via Once().
public static class Log
{
    private static readonly ConcurrentQueue<(DateTime When, string Line)> Queue = new();
    private static readonly HashSet<string> Onced = new();
    private static readonly object OnceLock = new();

    // Serializes appends across processes. Local\ (not Global\) is per-logon-session, which is
    // exactly the scope we need - the elevated helper and an unelevated UI/CLI run share it - and
    // an unelevated process lacks the privilege to create a Global\ object anyway.
    private static readonly Mutex FileLock = new(false, @"Local\Findra.Log.File");
    private static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly int Pid = Environment.ProcessId;
    private static readonly DateTime SessionStart = DateTime.Now;

    private static int _started;
    private static bool _ready;
    private static int _warns, _errors;

    /// <summary>Which build this is. One source (Directory.Build.props), normalised so the update
    /// check can parse it - see <see cref="BuildInfo"/>.</summary>
    public static string Version => BuildInfo.Version;

    // One-line health verdict for this process, written at exit so "was that run OK?" is a single
    // grep instead of a scan.
    public static string SessionSummary()
    {
        var up = DateTime.Now - SessionStart;
        return $"uptime={(int)up.TotalHours}h{up.Minutes:00}m warns={Volatile.Read(ref _warns)} errors={Volatile.Read(ref _errors)}";
    }

    public static string Dir => Paths.Ensure(Paths.Logs);

    public static void Info(string cat, string msg) => Write("INFO ", cat, msg);
    public static void Warn(string cat, string msg) => Write("WARN ", cat, msg);
    public static void Error(string cat, string msg) => Write("ERROR", cat, msg);

    public static void Error(string cat, string msg, Exception ex)
        => Write("ERROR", cat, $"{msg} :: {ex.GetType().Name}: {ex.Message}");

    // Log a repeating condition only the first time it happens (per unique key).
    //
    // Right for a condition that is true once and is then either fixed or permanent. WRONG for a
    // retry loop: a session that fails every thirty seconds for four hours would report itself in
    // the first minute of the log and be silent for the rest of the process. Use Repeat for that.
    public static void Once(string key, string level, string cat, string msg)
    {
        lock (OnceLock)
        {
            if (!Onced.Add(key)) return;
        }
        Write(level is "WARN" or "WARN " ? "WARN " : level is "ERROR" ? "ERROR" : "INFO ", cat, msg);
    }

    /// <summary>When this key last spoke, and how many times it has been asked since.</summary>
    private static readonly Dictionary<string, (DateTime Last, int Held)> Repeats = [];

    /// <summary>
    /// May this key say something now, and how many occurrences were held back since it last did?
    ///
    /// <para>Separated from <see cref="Repeat"/> and given the time rather than reading a clock, so
    /// the policy - first one always, then at most one per interval, carrying the count it swallowed
    /// - is checkable without a log file and without waiting five minutes.</para>
    ///
    /// <para>A clock that moved BACKWARDS lets it speak. A resync under a long-running process
    /// moves the wall clock, and "now - last >= interval" would go silent for as far as the clock
    /// jumped, which is the same silence this method exists to prevent.</para>
    /// </summary>
    public static bool DueToRepeat(string key, TimeSpan every, DateTime now, out int held)
    {
        lock (OnceLock)
        {
            if (Repeats.TryGetValue(key, out (DateTime Last, int Held) at)
                && now >= at.Last && now - at.Last < every)
            {
                Repeats[key] = (at.Last, at.Held + 1);
                held = 0;
                return false;
            }
            held = Repeats.TryGetValue(key, out at) ? at.Held : 0;
            Repeats[key] = (now, 0);
            return true;
        }
    }

    /// <summary>
    /// Say it now, then at most once per <paramref name="every"/> for as long as it keeps
    /// happening - with the number of occurrences held back since the last line.
    ///
    /// <para>A failure that recurs is a different fact from one that happened once, and a log that
    /// cannot tell them apart is why a persistent fault reads as a one-off. This is
    /// <see cref="Once"/>'s counterpart for anything inside a retry loop.</para>
    /// </summary>
    public static void Repeat(string key, TimeSpan every, string level, string cat, string msg)
    {
        if (!DueToRepeat(key, every, DateTime.UtcNow, out int held)) return;
        if (held > 0) msg += $" ({held.ToString(CultureInfo.InvariantCulture)} more since the last line)";
        Write(level is "WARN" or "WARN " ? "WARN " : level is "ERROR" ? "ERROR" : "INFO ", cat, msg);
    }

    private static void Write(string level, string cat, string msg)
    {
        EnsureStarted();
        bool error = level == "ERROR";
        if (level == "WARN ") Interlocked.Increment(ref _warns);
        else if (error) Interlocked.Increment(ref _errors);

        var now = DateTime.Now;
        string stamp = now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture);
        Queue.Enqueue((now, $"{stamp} [{level}] [{cat}] [{Pid}] {OneLine(msg)}"));

        // An ERROR is often the last thing that happens before the process dies. Sitting in the
        // queue for up to a second means a crash takes the explanation down with it - so errors
        // (and only errors, which are rare) go to disk immediately.
        if (error) Flush();
    }

    // "One line per event, greppable" is this class's contract, and a caller cannot be trusted to
    // honour it: file and folder names come straight off an NTFS volume, and NTFS permits control
    // characters and unpaired surrogates that no ordinary Explorer rename dialog would ever let a
    // person type. Left alone, a name like that lands mid-line in the log and looks exactly like a
    // torn concurrent append even though the appends themselves are fine. Control characters
    // (including a stray newline, which would split one event across two lines) become '?' here.
    // Clean text, which is nearly every line, allocates nothing.
    internal static string OneLine(string s)
    {
        bool dirty = false;
        foreach (char c in s) if (c < ' ' || c == '\u007f') { dirty = true; break; }
        if (!dirty) return s;

        var sb = new StringBuilder(s.Length);
        foreach (char c in s) sb.Append(c < ' ' || c == '\u007f' ? '?' : c);
        return sb.ToString();
    }

    private static void EnsureStarted()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        try
        {
            Directory.CreateDirectory(Dir);
            Prune();
            _ready = true;
        }
        catch { /* logging must never take the app down */ }

        AppDomain.CurrentDomain.ProcessExit += (_, _) => Flush();
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(1000);
                Flush();
            }
        });
    }

    // retention: drop logs older than 7 days, by the date in the NAME (the mtime of a file a
    // long session kept appending to says nothing about the days it covers)
    private static void Prune()
    {
        foreach (var f in Directory.GetFiles(Dir, "findra-*.log"))
        {
            string stem = Path.GetFileNameWithoutExtension(f);
            if (stem.Length < 14) continue;
            if (!DateTime.TryParseExact(stem[^8..], "yyyyMMdd", CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out var day)) continue;
            if (day < DateTime.Today.AddDays(-7))
                try { File.Delete(f); } catch { }
        }
    }

    public static void Flush()
    {
        if (Queue.IsEmpty || !_ready) return;

        // group by event day: a flush that straddles midnight writes to both days' files
        var byDay = new Dictionary<DateTime, StringBuilder>();
        while (Queue.TryDequeue(out var e))
        {
            if (!byDay.TryGetValue(e.When.Date, out var sb))
                byDay[e.When.Date] = sb = new StringBuilder();
            sb.AppendLine(e.Line);
        }
        foreach (var (day, sb) in byDay) Append(day, sb.ToString());
    }

    private static void Append(DateTime day, string text)
    {
        if (text.Length == 0) return;
        bool held = false;
        try
        {
            // if the wait times out something is badly wrong; write anyway rather than drop
            // diagnostics - a torn line beats a missing one
            try { held = FileLock.WaitOne(2000); }
            catch (AbandonedMutexException) { held = true; }   // a writer died mid-flush; it's ours now

            string path = Path.Combine(Dir, $"findra-{day:yyyyMMdd}.log");
            using var fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
            using var w = new StreamWriter(fs, Utf8);
            w.Write(text);
        }
        catch { /* disk full / locked: drop rather than crash */ }
        finally
        {
            if (held) { try { FileLock.ReleaseMutex(); } catch { } }
        }
    }
}
