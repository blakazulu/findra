using System.Globalization;
using System.Text.Json;

namespace Findra;

/// <summary>
/// What was opened from the card, and how often, so that among results that answer a query about
/// equally well the one somebody actually uses comes first - and so the empty card can list what
/// was opened last.
///
/// <para>It stays on this machine, in <see cref="Paths.OpenedFile"/>: paths and counts, nothing
/// about what was typed. It keeps the <see cref="Keep"/> most recent paths and forgets the rest.
/// Settings turns it off, and off deletes the file.</para>
///
/// <para>The lift is small on purpose. It is added to the score only for ORDERING (the score the
/// card shows is untouched) and it is at most <see cref="MaxLift"/>, so it reorders neighbours and
/// never puts a poor match above a good one.</para>
/// </summary>
public sealed class OpenedHistory
{
    /// <summary>How many paths are remembered.</summary>
    public const int Keep = 200;

    /// <summary>The most the ordering score is raised by.</summary>
    public const float MaxLift = 0.1f;

    /// <summary>Opens past this many add nothing more.</summary>
    public const int CountCap = 8;

    public sealed record Entry(string Path, int Count, DateTime LastUtc);

    private readonly string? _file;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _byPath = new(StringComparer.OrdinalIgnoreCase);

    /// <param name="file">Where the list is kept; null keeps it in memory only.</param>
    public OpenedHistory(string? file, Func<DateTime>? now = null)
    {
        _file = file;
        _now = now ?? (() => DateTime.UtcNow);
        foreach (Entry e in Read(file)) _byPath[e.Path] = e;
    }

    public int Count { get { lock (_gate) return _byPath.Count; } }

    /// <summary>Remember that <paramref name="path"/> was opened now.</summary>
    public void Note(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        lock (_gate)
        {
            DateTime now = _now();
            _byPath[path] = _byPath.TryGetValue(path, out Entry? had)
                ? had with { Count = had.Count + 1, LastUtc = now, Path = path }
                : new Entry(path, 1, now);

            if (_byPath.Count > Keep)
                foreach (Entry old in _byPath.Values.OrderBy(e => e.LastUtc).Take(_byPath.Count - Keep).ToList())
                    _byPath.Remove(old.Path);
            Write();
        }
    }

    /// <summary>
    /// How much a path's ordering score is raised: half for how often it was opened (up to
    /// <see cref="CountCap"/>), half for how lately - a day, a week, a month, longer.
    /// </summary>
    public float Lift(string path)
    {
        Entry? e;
        lock (_gate) if (!_byPath.TryGetValue(path, out e)) return 0f;
        double days = (_now() - e.LastUtc).TotalDays;
        float lately = days < 1 ? 1f : days < 7 ? 0.75f : days < 30 ? 0.5f : 0.25f;
        float often = Math.Min(e.Count, CountCap) / (float)CountCap;
        return MaxLift * (0.5f * often + 0.5f * lately);
    }

    /// <summary>The paths opened most lately, newest first.</summary>
    public IReadOnlyList<Entry> Recent(int n)
    {
        lock (_gate) return [.. _byPath.Values.OrderByDescending(e => e.LastUtc).Take(Math.Max(0, n))];
    }

    /// <summary>Forget everything, on disk as well.</summary>
    public void Forget()
    {
        lock (_gate)
        {
            _byPath.Clear();
            if (_file is null) return;
            try { File.Delete(_file); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("search", "the list of opened files could not be deleted: " + ex.Message);
            }
        }
    }

    /// <summary>"opened today", "opened 3 days ago": the stage's line for a row from this list.</summary>
    public static string When(DateTime lastUtc, DateTime nowUtc)
    {
        int days = (int)Math.Floor((nowUtc - lastUtc).TotalDays);
        return days switch
        {
            <= 0 => "opened today",
            1 => "opened yesterday",
            _ => "opened " + days.ToString(CultureInfo.InvariantCulture) + " days ago",
        };
    }

    // ---- the file ----------------------------------------------------------------------------

    private static readonly JsonSerializerOptions Opts = new() { WriteIndented = false };

    private static IEnumerable<Entry> Read(string? file)
    {
        if (file is null || !File.Exists(file)) return [];
        try
        {
            List<Entry>? list = JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(file), Opts);
            return list?.Where(e => e is not null && !string.IsNullOrWhiteSpace(e.Path) && e.Count > 0) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            // A damaged list costs its ranking hints and nothing else.
            Log.Warn("search", "the list of opened files is not readable and starts again: " + ex.Message);
            return [];
        }
    }

    // Under _gate. Written beside and moved over, so a crash mid-write leaves the old list.
    private void Write()
    {
        if (_file is null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            string temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_byPath.Values.OrderByDescending(e => e.LastUtc).ToList(), Opts));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("search", "the list of opened files could not be written: " + ex.Message);
        }
    }
}
