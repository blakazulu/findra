using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Findra.Pipe;

public sealed record QueryRequest(long Gen, string Raw, int Max);

/// <summary>
/// One answer. <c>Volume</c> lives here rather than on the reply because a reply can carry rows
/// from every volume at once: a single letter on the envelope would name whichever one happened
/// to supply the last row, which means nothing as soon as two contribute.
/// </summary>
public sealed record NameRow(char Volume, ulong Frn, string Name, string Path, uint Attributes,
                             float Score, int Match);

public sealed record QueryReply(long Gen, long ElapsedTicks, IReadOnlyList<NameRow> Rows);

public sealed record StatusRequest();

/// <summary>
/// One volume as the helper sees it right now. <c>EnumerateMs</c> is the cold-start MFT walk and
/// <c>NextUsn</c> the journal position that walk was taken against - both are what
/// <c>--searchbench</c> publishes, and neither is reconstructable at normal integrity.
/// <c>Dropped</c> is this SESSION's count of events its outbound queue evicted because the client
/// stopped reading; a gap the user is told about is recoverable, a silent one is not.
///
/// The last three are optional so that every existing caller - the capsule's index line and the
/// message round-trip test among them - keeps compiling and keeps meaning what it meant. A zero
/// there reads as "not measured", which is exactly what a caller that never had the numbers is
/// saying. <see cref="NameServer"/> always fills all three from the volume's view.
/// </summary>
public sealed record VolumeStatus(char Letter, int Count, long BufferBytes, bool Live,
                                  double EnumerateMs = 0, long NextUsn = 0, long Dropped = 0);

public sealed record StatusReply(int ProcessId, IReadOnlyList<VolumeStatus> Volumes);

/// <summary>
/// One journal record on its way to the UI.
///
/// <c>Path</c> is resolved by the helper from its own in-RAM index, exactly as it already does for
/// <see cref="NameRow.Path"/>. It has to be: the UI holds no parent map and cannot turn a parent
/// FRN into a path, and building a second one at normal integrity is the duplication the thin
/// helper exists to avoid. Reading a path out of an index the helper already owns is not parsing
/// untrusted file content - no file is opened and no byte of one is decoded. A delete carries
/// <c>Path = ""</c>: the record is gone, and the feeder keys deletes on (volume, frn).
///
/// <c>JournalId</c> is here because a USN means nothing without it. A recreated journal restarts
/// its numbering from zero, so a stored position from the old one names a record that no longer
/// exists or, worse, a different one. Every event carries it - live slices, replayed gap records
/// and the reset marker alike - because the feeder writes the id alongside the position, and a
/// zero written there makes every later launch answer NeedsFullPass and re-walk every disk.
/// </summary>
public sealed record JournalEvent(char Volume, ulong JournalId, ulong Frn, ulong Parent,
                                  uint Attributes, string Name, string Path, uint Reason, long Usn);

/// <summary>Where a subscriber last got to on one volume, as it stored it.</summary>
public sealed record VolumeCursor(char Volume, ulong JournalId, long Usn);

/// <summary>Ask to be pushed journal events, resuming from these stored positions.</summary>
public sealed record SubscribeRequest(IReadOnlyList<VolumeCursor> From);

/// <summary>
/// Where one volume actually resumes, which is not always where the caller asked.
/// <c>JournalId</c> is always the volume's CURRENT id, never the caller's stale one - what the
/// feeder stores has to be what the next launch is compared against.
/// </summary>
public sealed record VolumeResume(char Volume, ulong JournalId, long Usn, bool NeedsFullPass,
                                  int Replayed, string Note);

public sealed record SubscribeReply(IReadOnlyList<VolumeResume> Volumes);

/// <summary>
/// Kind outside, body inside. An envelope whose kind is unknown can still be read and
/// skipped, so one side can learn a message the other has not.
/// </summary>
public sealed record Envelope(string Kind, string Json)
{
    public const string KindQuery       = "query";
    public const string KindQueryReply  = "query-reply";
    public const string KindStatus      = "status";
    public const string KindStatusReply = "status-reply";
    public const string KindJournal     = "journal";

    // A subscription is a registration, not a question, and a journal event is not an answer to
    // anything - so neither is stamped with a generation. The counter exists to discard a stale
    // ANSWER to an abandoned query; applying it to a push would mean the newest event silently
    // invalidating a query reply still in flight.
    public const string KindSubscribe      = "subscribe";
    public const string KindSubscribeReply = "subscribe-reply";

    private static readonly JsonSerializerOptions Opts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    public static byte[] Pack<T>(string kind, T body)
    {
        var e = new Envelope(kind, JsonSerializer.Serialize(body, Opts));
        return Encoding.UTF8.GetBytes(JsonSerializer.Serialize(e, Opts));
    }

    public static Envelope Unpack(byte[] payload) =>
        JsonSerializer.Deserialize<Envelope>(Encoding.UTF8.GetString(payload), Opts)
            ?? throw new InvalidDataException("empty envelope");

    public T Body<T>() =>
        JsonSerializer.Deserialize<T>(Json, Opts) ?? throw new InvalidDataException($"empty body for {Kind}");
}
