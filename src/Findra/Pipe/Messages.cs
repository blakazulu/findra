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

public sealed record VolumeStatus(char Letter, int Count, long BufferBytes, bool Live);

public sealed record StatusReply(int ProcessId, IReadOnlyList<VolumeStatus> Volumes);

public sealed record JournalEvent(char Volume, ulong Frn, ulong Parent, uint Attributes,
                                 string Name, uint Reason, long Usn);

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
