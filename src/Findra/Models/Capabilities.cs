using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace Findra;

/// <summary>The four things a model buys. Words in documents is not here: it needs no model, it
/// is always on, and it is printed "free" on the first-run screen precisely so that taking none
/// of these is a safe choice rather than a broken one (spec §6).</summary>
public enum Capability { Photos, Meaning, Speech, Hebrew }

/// <summary>Which capability a preset stands for. Custom is not a preset a user picks - it is
/// what the screen becomes the moment they touch a row.</summary>
public enum Preset { JustNames, Recommended, Everything, Custom }

/// <summary>A capability the card would like to offer, and what it would cost on THIS machine.
/// <see cref="MarginalBytes"/> is marginal given what is already installed, so the sentence a
/// person reads is the size they would actually download.</summary>
public readonly record struct Offer(Capability Capability, long MarginalBytes, string Text);

/// <summary>
/// What this machine can actually do right now, read from the files on disk.
///
/// <para>Deliberately not read from config.json: what is installed is a fact about the disk, and
/// a settings file that claims a capability whose 1.5 GB is not there produces a load failure on
/// the first query instead of a quiet skip. The selection a user made is a setting; what arrived
/// is not.</para>
/// </summary>
public readonly record struct CapabilitySet(IReadOnlySet<Capability> Have, IReadOnlySet<string>? Files = null)
{
    public static readonly CapabilitySet None = new(new HashSet<Capability>());

    public bool Has(Capability c) => Have is not null && Have.Contains(c);

    /// <summary>
    /// The model FILES this machine holds, by name, or an empty set where they were not read.
    ///
    /// <para>It is carried beside <see cref="Have"/> rather than derived from it because the two
    /// answer different questions and a capability cannot answer the file one. A capability is
    /// all-or-nothing: a folder holding whisper-turbo with no e5 pair beside it has Speech
    /// uninstalled, so 550 MB of it counts for nothing, and every surface that priced the Speech
    /// row from capabilities alone quoted the closed total for a download that was really Meaning's
    /// alone. That folder
    /// is ordinary, not hypothetical - a download run carries on past a file that failed, so one
    /// bad leg of a Speech install leaves exactly it.</para>
    /// </summary>
    /// <para>Where the files were not read - a set built by hand naming capabilities alone - they
    /// are DERIVED from <see cref="Have"/>, because a set that says Meaning is installed is saying
    /// its two e5 files are on the disk. That keeps the old arithmetic exactly where it was right
    /// and fixes it only where a real disk disagrees with it, which is the half-installed folder
    /// no capability can describe.</para>
    public IReadOnlySet<string> OnDisk
    {
        get
        {
            if (Files is not null) return Files;
            var derived = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (Have is not null)
                foreach (Model m in Capabilities.ModelsFor(Have)) derived.Add(m.File);
            return derived;
        }
    }

    /// <summary>Every capability whose whole closed model set is on disk. Closed, not own: a
    /// Whisper file with no e5 pair beside it cannot answer a search, because a transcript is
    /// searched as a document.</summary>
    public static CapabilitySet Installed(string? dir = null)
    {
        var have = new HashSet<Capability>();
        // Read while the capabilities are being decided rather than in a second pass: these are
        // the same seven files, and two passes are two answers if one arrives mid-download.
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Capability c in Capabilities.All)
            foreach (Model m in Capabilities.OwnModels(c))
                if (ModelStore.Present(m, dir)) files.Add(m.File);

        foreach (Capability c in Capabilities.All)
        {
            bool all = true;
            foreach (Model m in Capabilities.ModelsFor(Capabilities.Close([c])))
                if (!files.Contains(m.File)) { all = false; break; }
            if (all) have.Add(c);
        }
        return new CapabilitySet(have, files);
    }
}

public static class Presets
{
    public static readonly IReadOnlySet<Capability> JustNames = new HashSet<Capability>();
    public static readonly IReadOnlySet<Capability> Recommended =
        Capabilities.Close([Capability.Photos, Capability.Meaning]);
    public static readonly IReadOnlySet<Capability> Everything =
        Capabilities.Close([Capability.Photos, Capability.Meaning, Capability.Speech, Capability.Hebrew]);

    public static Preset Match(IReadOnlySet<Capability> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        if (chosen.SetEquals(JustNames)) return Preset.JustNames;
        if (chosen.SetEquals(Recommended)) return Preset.Recommended;
        if (chosen.SetEquals(Everything)) return Preset.Everything;
        return Preset.Custom;
    }
}

/// <summary>
/// The capability graph and every number derived from it.
///
/// <para>The capabilities are NOT peers, and the two edges fall out of the engine rather than out
/// of a preference: Speech needs Meaning because a transcript is embedded and searched exactly
/// like a document, and Hebrew needs Speech because the general model runs first for language
/// detection and only the files it calls Hebrew are re-run through the fine-tune. Hebrew is a
/// second pass, never an alternative (spec §6).</para>
///
/// <para>Every size this type produces is MARGINAL - what adding one costs given what is already
/// chosen. Nothing anywhere may hold a fixed per-capability number: Speech is 1.57 GB alone and
/// 547 MB beside documents, and a fixed table makes the first-run total visibly fail to add
/// up.</para>
/// </summary>
public static class Capabilities
{
    public static readonly IReadOnlyList<Capability> All =
        [Capability.Photos, Capability.Meaning, Capability.Speech, Capability.Hebrew];

    /// <summary>The DIRECT prerequisites. <see cref="Close"/> walks these to a fixed point.</summary>
    public static IReadOnlyList<Capability> Requires(Capability c) => c switch
    {
        Capability.Speech => [Capability.Meaning],
        Capability.Hebrew => [Capability.Speech],
        _ => [],
    };

    /// <summary>A selection with everything it depends on, transitively. Hebrew closes to
    /// {Hebrew, Speech, Meaning} - a single step would stop at Speech and produce a download set
    /// that installs and then cannot answer anything.</summary>
    public static IReadOnlySet<Capability> Close(IEnumerable<Capability> chosen)
    {
        ArgumentNullException.ThrowIfNull(chosen);
        var have = new HashSet<Capability>(chosen);
        var queue = new Queue<Capability>(have);
        while (queue.Count > 0)
            foreach (Capability need in Requires(queue.Dequeue()))
                if (have.Add(need)) queue.Enqueue(need);
        return have;
    }

    /// <summary>A selection with one capability removed, and with anything that depended on it
    /// removed too. Unticking Speech while Hebrew is ticked must take Hebrew with it; leaving it
    /// selects a 1.5 GB fine-tune with no model to detect language for it.</summary>
    public static IReadOnlySet<Capability> Drop(IEnumerable<Capability> from, Capability gone)
    {
        ArgumentNullException.ThrowIfNull(from);
        var keep = new HashSet<Capability>(from);
        keep.Remove(gone);
        bool changed = true;
        while (changed)
        {
            changed = false;
            foreach (Capability c in keep.ToList())
                foreach (Capability need in Requires(c))
                    if (!keep.Contains(need)) { keep.Remove(c); changed = true; }
        }
        return keep;
    }

    /// <summary>The files this capability itself adds - not its prerequisites'.</summary>
    public static IReadOnlyList<Model> OwnModels(Capability c) => c switch
    {
        Capability.Photos => [ModelStore.Siglip2Vision, ModelStore.Siglip2Text, ModelStore.Siglip2Spm],
        Capability.Meaning => [ModelStore.E5Base, ModelStore.E5Spm],
        Capability.Speech => [ModelStore.WhisperTurbo],
        Capability.Hebrew => [ModelStore.WhisperHebrew],
        _ => [],
    };

    /// <summary>Every file a selection needs, closed and de-duplicated.</summary>
    public static IReadOnlyList<Model> ModelsFor(IEnumerable<Capability> chosen)
    {
        var files = new List<Model>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Capability c in Close(chosen))
            foreach (Model m in OwnModels(c))
                if (seen.Add(m.File)) files.Add(m);
        return files;
    }

    public static long TotalBytes(IEnumerable<Capability> chosen) => ModelStore.TotalBytes(ModelsFor(chosen));

    /// <summary>What adding one more capability would cost, given what is already chosen. This
    /// is the only place the arithmetic lives, and every surface reads it from here.</summary>
    public static long MarginalBytes(Capability add, IEnumerable<Capability> already)
    {
        ArgumentNullException.ThrowIfNull(already);
        var have = new HashSet<Capability>(already);
        long before = TotalBytes(have);
        have.Add(add);
        return TotalBytes(have) - before;
    }

    /// <summary>
    /// What adding one more capability would cost on a machine holding these FILES.
    ///
    /// <para>The overload above counts in capabilities, and a capability is all-or-nothing: a
    /// machine holding whisper-turbo with no e5 pair beside it has Speech uninstalled, so that
    /// 550 MB counts for nothing and Settings, the card's offer, <c>--models</c> and
    /// <c>--searchmodels</c> all quote the closed total for a download that is really smaller. It is a
    /// reachable disk, not a hypothetical one: a download run continues past a file that failed,
    /// so one bad leg of a Speech install leaves exactly that folder - and
    /// <c>--models install speech</c>, which prices by file, then disagrees with all four of
    /// them.</para>
    ///
    /// <para>Counting in files is also the simpler statement of the question: what this row would
    /// add is the size of the files its closed set needs and that are not already on the disk. The
    /// first-run screen has always priced this way; this is the same arithmetic, named once.</para>
    /// </summary>
    public static long MarginalBytes(Capability add, IReadOnlySet<string> onDisk)
    {
        ArgumentNullException.ThrowIfNull(onDisk);
        long bytes = 0;
        foreach (Model m in ModelsFor([add]))
            if (!onDisk.Contains(m.File)) bytes += m.Bytes;
        return bytes;
    }

    /// <summary>The same, for a machine described by a <see cref="CapabilitySet"/>. This is the
    /// overload every surface should call: the set carries the files it was built from, so the
    /// half-installed folder above is priced at what is actually missing. A set built without
    /// them - a test's, or <see cref="CapabilitySet.None"/> - has no files, and then every file
    /// its closed set needs is counted, which is the right answer for an empty disk.</summary>
    public static long MarginalBytes(Capability add, CapabilitySet installed)
        => MarginalBytes(add, installed.OnDisk);

    /// <summary>The result kinds this capability can newly read - and therefore exactly what
    /// enabling it re-queues. Nothing else is touched.</summary>
    public static int[] KindsCovered(Capability c) => c switch
    {
        Capability.Photos => [(int)ResultKind.Photo, (int)ResultKind.Video],
        Capability.Meaning => [(int)ResultKind.Document],
        // A transcript is speech, and speech lives in both sound files and the sound track of a
        // short video. The Hebrew pass covers the same two kinds: it re-runs files the general
        // model already heard, and there is no way to know which without re-opening them.
        Capability.Speech or Capability.Hebrew => [(int)ResultKind.Audio, (int)ResultKind.Video],
        _ => [],
    };

    public static string Title(Capability c) => c switch
    {
        Capability.Photos => "Photos and video",
        Capability.Meaning => "Meaning in documents",
        Capability.Speech => "Speech",
        Capability.Hebrew => "Speech in Hebrew",
        _ => c.ToString(),
    };

    /// <summary>Is a Hebrew row worth showing at all? Spec §6: only when the system locale or the
    /// installed languages include Hebrew. Compared on the language SUBTAG, because a substring
    /// test on "he" is true for "th-TH" and would put a 1.5 GB row in front of a Thai user.</summary>
    public static bool HebrewIsOffered(IEnumerable<string> languageTags)
    {
        ArgumentNullException.ThrowIfNull(languageTags);
        foreach (string tag in languageTags)
        {
            string primary = tag.Split('-')[0];
            if (primary.Equals("he", StringComparison.OrdinalIgnoreCase)
             || primary.Equals("iw", StringComparison.OrdinalIgnoreCase))   // the legacy tag, still emitted
                return true;
        }
        return false;
    }

    /// <summary>The language tags this machine actually has. Impure, so it is separate from the
    /// rule above and the rule stays testable. Its two callers are <c>--models list</c> and the
    /// first-run screen, both of which show the Hebrew row only on a machine where it is worth
    /// 1.5 GB.</summary>
    public static IReadOnlyList<string> SystemLanguages()
    {
        var tags = new List<string> { CultureInfo.CurrentUICulture.Name, CultureInfo.InstalledUICulture.Name };
        try { foreach (string t in Windows.System.UserProfile.GlobalizationPreferences.Languages) tags.Add(t); }
        catch (Exception ex) { Log.Once("models|langs", "WARN", "models", $"could not read the installed languages :: {ex.Message}"); }
        return tags;
    }

    /// <summary>
    /// What this query would have found with a capability this machine has not got - or null.
    ///
    /// <para>The rule is deliberately narrow and ordered, because an offer that fires on every
    /// query is an advertisement. A query that names a kind offers the capability that reads that
    /// kind; anything else offers meaning in documents, which is the one that changes an ordinary
    /// word search. Hebrew is never offered here: it refines a capability somebody already chose,
    /// and 1.5 GB is not a decision to put in a search box.</para>
    /// </summary>
    public static Offer? OfferFor(SearchQuery q, CapabilitySet installed)
    {
        ArgumentNullException.ThrowIfNull(q);
        Capability? want = null;
        if (q.Kinds.Contains(ResultKind.Photo) || q.Kinds.Contains(ResultKind.Video)) want = Capability.Photos;
        else if (q.Kinds.Contains(ResultKind.Audio)) want = Capability.Speech;
        else if (q.HasNameTerms) want = Capability.Meaning;
        if (want is null || installed.Has(want.Value)) return null;

        long marginal = MarginalBytes(want.Value, installed);
        string what = want.Value switch
        {
            Capability.Photos => "Searching inside photos and video",
            Capability.Speech => "Searching what was said out loud",
            _ => "Searching documents by meaning rather than exact words",
        };
        return new Offer(want.Value, marginal, $"{what} needs {Sizes.Human(marginal)} - get it?");
    }
}
