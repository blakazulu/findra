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
        // The guard and the mutation have to be one decision. Reading _issued and then
        // writing _accepted as two separate atomics leaves a window: Next() can land
        // between them, a newer reply can be accepted in that gap, and this call would
        // still go on to write its own older generation - showing results for a query the
        // user already abandoned, and leaving _accepted regressed so the newer generation
        // could then win a second time. The CAS loop closes it by making _accepted
        // monotone. The UI thread issues; the pipe reader thread arbitrates. They race.
        while (true)
        {
            long accepted = Interlocked.Read(ref _accepted);
            if (gen <= accepted) return false;                       // already shown, or older than what is
            if (gen != Interlocked.Read(ref _issued)) return false;  // stale, or never issued

            // Compare against the value just observed, never against gen - 1: generations
            // dropped as stale are never accepted, so _accepted is not a dense sequence,
            // and comparing against gen - 1 would refuse everything after the first drop -
            // silently killing search.
            if (Interlocked.CompareExchange(ref _accepted, gen, accepted) == accepted) return true;
        }
    }
}
