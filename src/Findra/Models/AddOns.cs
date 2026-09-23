using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Findra;

/// <summary>What one re-queue of an add-on's files is: which kinds, and the reason the indexer
/// reads them for.</summary>
public readonly record struct ForgetStep(int[] Kinds, string Reason);

/// <summary>
/// Turning an add-on off, removing it, and catching up when it comes back.
///
/// <para><b>Off</b> stops an add-on reading new files and keeps everything else: its files stay on
/// disk and what it found stays searchable. The indexer child learns the list from
/// <see cref="OffKey"/>, per file, like the transcription limit.</para>
///
/// <para><b>Remove</b> deletes its files, except any another installed add-on still needs - Speech
/// embeds its transcripts with Meaning's pair, so removing Meaning under Speech deletes nothing and
/// only turns Meaning off. Removing Speech takes Hebrew with it.</para>
///
/// <para><b>Coming back</b> - turned on again, or added again - re-reads exactly the files read
/// while it was away (<see cref="NoteAway"/>, <see cref="CatchUp"/>). A document read with Meaning
/// off has its words and no meaning, and nothing else would ever send it back.</para>
/// </summary>
public static class AddOns
{
    /// <summary>The meta row the interface writes the off list to, and the child reads per file.</summary>
    public const string OffKey = "index:addonsoff";

    private const string AwayPrefix = "models:away:";

    private static string AwayKey(Capability c) => AwayPrefix + c.ToString().ToLowerInvariant();

    public static string Format(IEnumerable<Capability> off)
    {
        ArgumentNullException.ThrowIfNull(off);
        return string.Join(",", off.Distinct().Order());
    }

    /// <summary>Anything unreadable is dropped rather than guessed at: a name that parses to the
    /// wrong add-on would stop the wrong thing reading.</summary>
    public static IReadOnlyList<Capability> Parse(string? row)
    {
        var off = new List<Capability>();
        if (string.IsNullOrWhiteSpace(row)) return off;
        foreach (string part in row.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (Enum.TryParse(part, ignoreCase: false, out Capability c) && Enum.IsDefined(c) && !off.Contains(c)) off.Add(c);
        return off;
    }

    /// <summary>The config's list, as add-ons.</summary>
    public static IReadOnlyList<Capability> Off(Config config)
    {
        ArgumentNullException.ThrowIfNull(config);
        return Parse(string.Join(",", config.AddOnsOff));
    }

    /// <summary>The add-on and every installed one that cannot work without it. Speech is never
    /// taken with Meaning: it needs Meaning's FILES, not Meaning being wanted, and removing Meaning
    /// under Speech keeps those files.</summary>
    public static IReadOnlySet<Capability> RemovedWith(Capability c, CapabilitySet installed)
    {
        var gone = new HashSet<Capability> { c };
        bool grew = true;
        while (grew)
        {
            grew = false;
            foreach (Capability other in Capabilities.All)
            {
                if (!installed.Has(other) || gone.Contains(other) || other == Capability.Speech) continue;
                if (Capabilities.Requires(other).Any(gone.Contains)) grew |= gone.Add(other);
            }
        }
        return gone;
    }

    /// <summary>The model files removing this add-on deletes: its own and its dependents', less
    /// anything an add-on that stays still needs.</summary>
    public static IReadOnlyList<string> FilesToDelete(Capability c, CapabilitySet installed)
    {
        IReadOnlySet<Capability> gone = RemovedWith(c, installed);
        IEnumerable<Capability> stays = Capabilities.All.Where(x => installed.Has(x) && !gone.Contains(x));
        var needed = new HashSet<string>(Capabilities.ModelsFor(stays).Select(m => m.File), StringComparer.OrdinalIgnoreCase);
        return [.. gone.SelectMany(Capabilities.OwnModels).Select(m => m.File)
                       .Where(f => !needed.Contains(f)).Distinct(StringComparer.OrdinalIgnoreCase)];
    }

    /// <summary>Removing this deletes nothing, because what stays needs every file of it. The
    /// Remove question says so, and the button turns it off instead.</summary>
    public static bool OnlyTurnsOff(Capability c, CapabilitySet installed) => FilesToDelete(c, installed).Count == 0;

    /// <summary>What removing it frees, from the declared sizes.</summary>
    public static long Frees(Capability c, CapabilitySet installed)
    {
        var files = new HashSet<string>(FilesToDelete(c, installed), StringComparer.OrdinalIgnoreCase);
        return Capabilities.All.SelectMany(Capabilities.OwnModels).Where(m => files.Remove(m.File)).Sum(m => m.Bytes);
    }

    /// <summary>
    /// What forgetting an add-on's findings re-reads. The indexer drops a file's findings when it
    /// can no longer read that kind (the file goes back to skipped, its vectors released), so
    /// forgetting is re-reading once the add-on is gone. A video keeps what was heard in it:
    /// <see cref="Indexer.Reframe"/> takes its pictures again and leaves the transcript alone.
    /// The Hebrew pass leaves ordinary transcripts, so it has nothing of its own to forget.
    /// </summary>
    public static IReadOnlyList<ForgetStep> Forget(Capability c) => c switch
    {
        Capability.Photos => [new([(int)ResultKind.Photo], Indexer.Recheck), new([(int)ResultKind.Video], Indexer.Reframe)],
        Capability.Meaning => [new([(int)ResultKind.Document], Indexer.Recheck)],
        Capability.Speech => [new([(int)ResultKind.Audio, (int)ResultKind.Video], Indexer.Recheck)],
        _ => [],
    };

    /// <summary>Model files a removal could not delete because something still held them. Swept at
    /// the next start, before anything opens a model.</summary>
    public const string LeftoverKey = "models:toremove";

    /// <summary>Delete one model file and any half-finished download of it. True when neither is
    /// on the disk afterwards; false when something holds it, which is a retry, never an error.</summary>
    public static bool TryDelete(string dir, string file)
    {
        ArgumentNullException.ThrowIfNull(dir);
        ArgumentNullException.ThrowIfNull(file);
        string path = System.IO.Path.Combine(dir, file);
        try
        {
            if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            if (System.IO.File.Exists(path + ".part")) System.IO.File.Delete(path + ".part");
            return !System.IO.File.Exists(path);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Finish a removal an earlier session could not: delete what is on the list, keep
    /// what still will not go. Returns how many were deleted.</summary>
    public static int SweepLeftovers(ContentDb db, string dir)
    {
        ArgumentNullException.ThrowIfNull(db);
        string? row = db.Get(LeftoverKey);
        if (string.IsNullOrEmpty(row)) return 0;
        var still = new List<string>();
        int deleted = 0;
        foreach (string file in row.Split('|', StringSplitOptions.RemoveEmptyEntries))
            if (TryDelete(dir, file)) deleted++; else still.Add(file);
        db.Set(LeftoverKey, string.Join("|", still));
        if (deleted > 0) Log.Info("models", $"finished removing {deleted.ToString(CultureInfo.InvariantCulture)} model file(s) an earlier session could not delete");
        return deleted;
    }

    /// <summary>Re-read what an add-on found, so its findings are dropped (see <see cref="Forget"/>).
    /// Returns how many files were queued.</summary>
    public static int QueueForgetting(ContentDb db, Capability c)
    {
        ArgumentNullException.ThrowIfNull(db);
        int n = 0;
        foreach (ForgetStep step in Forget(c)) n += db.RequeueKinds(step.Kinds, step.Reason);
        Log.Info("models", $"forgetting what {Capabilities.Title(c)} found: {n.ToString("N0", CultureInfo.InvariantCulture)} file(s) queued");
        return n;
    }

    /// <summary>Record that an add-on stopped reading at this moment. The EARLIER of two moments
    /// wins: turned off, then removed, has been away since it was turned off.</summary>
    public static void NoteAway(ContentDb db, Capability c, long unixSeconds)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (long.TryParse(db.Get(AwayKey(c)), NumberStyles.Integer, CultureInfo.InvariantCulture, out long was)
            && was > 0 && was <= unixSeconds)
            return;
        db.Set(AwayKey(c), unixSeconds.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Re-read what every add-on that is reading again missed while it was away, and clear the
    /// record. Returns how many files were queued. Safe to call on every launch and after every
    /// change: an add-on still away, or never away, owes nothing.
    /// </summary>
    public static int CatchUp(ContentDb db, CapabilitySet reading)
    {
        ArgumentNullException.ThrowIfNull(db);
        int n = 0;
        foreach (Capability c in Capabilities.All)
        {
            if (!reading.Has(c)) continue;
            if (!long.TryParse(db.Get(AwayKey(c)), NumberStyles.Integer, CultureInfo.InvariantCulture, out long since) || since <= 0)
                continue;
            int q = db.RequeueKinds(Capabilities.KindsCovered(c), Indexer.Recheck, readSince: since);
            db.Set(AwayKey(c), "0");
            n += q;
            Log.Info("models", $"{Capabilities.Title(c)} is reading again: " +
                               $"{q.ToString("N0", CultureInfo.InvariantCulture)} file(s) read while it was away are queued");
        }
        return n;
    }
}
