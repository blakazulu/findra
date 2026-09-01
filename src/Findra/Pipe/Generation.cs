namespace Findra.Pipe;

/// <summary>
/// Name search is a round trip, so answers can arrive out of order. Every request is
/// stamped with a generation; every reply echoes it; only the newest generation is
/// allowed to reach the UI, and only once.
/// </summary>
public sealed class Generation
{
    private long _issued;
    private long _accepted;

    public long Current => Interlocked.Read(ref _issued);

    public long Next() => Interlocked.Increment(ref _issued);

    /// <summary>True at most once, and only for the newest generation issued.</summary>
    public bool Accept(long gen)
    {
        if (gen != Interlocked.Read(ref _issued)) return false;
        // Exchange, not CompareExchange against gen-1: generations that were dropped as
        // stale are never accepted, so _accepted is not a dense sequence. Comparing
        // against gen-1 would refuse every generation after the first drop.
        return Interlocked.Exchange(ref _accepted, gen) != gen;
    }
}
