using System.Globalization;
using Findra.Pipe;

namespace Findra;

/// <summary>
/// One volume's first pass: ask the helper for every file whose suffix this build cares about,
/// and hand the whole answer to the queue.
///
/// <para>It lives here rather than inside the interface because it is the longest thing the
/// content flow ever does - minutes on a real disk - and because what it owes the rest of that
/// flow while it runs is the part that was wrong and needs to be drivable in a test.</para>
/// </summary>
public static class FirstPass
{
    /// <summary>
    /// Walk one volume.
    ///
    /// <para><paramref name="pump"/> is called as rows arrive, and it is not an optional extra.
    /// The flow that runs this walk is the flow that owns the index's writer connection, so
    /// everything else that connection owes - draining what the settings window posted, keeping
    /// the capsule's line moving, starting the indexer child when there is work - can only happen
    /// between the walk's own steps. Without it, a person who pressed "Start reading now" during
    /// a first pass watched nothing happen and was told nothing, for as long as the pass took.
    /// The caller decides how often the delegate actually does anything; this calls it per row.
    /// </para>
    ///
    /// <para>The stream is collected before it is written, and that is a deliberate bound rather
    /// than an oversight: the suffix filter runs in the helper, so what comes back is the
    /// machine's documents, photos, audio and video, not its every file - a tenth to a twentieth
    /// of the rows, which is the whole reason enumerate takes a suffix list instead of streaming a
    /// snapshot. One transaction for the pass is also what makes the consumed position, the suffix
    /// stamp and the discharged debt land together or not at all.</para>
    /// </summary>
    public static async Task WalkAsync(NameClient client, QueueFeeder feeder, char volume,
                                       VolumeResume at, int batchSize, Action pump, CancellationToken ct)
    {
        Log.Info("index", string.Create(CultureInfo.InvariantCulture,
            $"{volume}: walking the disk for the first time this index has seen it ({at.Note})"));

        feeder.NoteWalkStarted(volume);
        var found = new List<EnumeratedFile>();
        await foreach (EnumeratedFile f in client
                           .EnumerateAsync(volume, QueueFeeder.ContentSuffixes(), batchSize, ct)
                           .ConfigureAwait(false))
        {
            found.Add(f);
            pump();
        }

        // Only a stream that reached its Done frame gets here - EnumerateAsync throws otherwise -
        // so a truncated walk can never stamp a position or clear the debt it did not discharge.
        //
        // The walk above takes minutes on a real disk, and events lost while it ran are past the
        // position it is about to stamp. FillFrom re-reads this session's dropped-event counter
        // itself before it decides whether to discharge anything, which is why nothing has to be
        // reported here: a call on this line is a call the next refactor deletes without noticing.
        feeder.FillFrom(volume, at.JournalId, at.Usn, found);
    }
}
