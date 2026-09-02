namespace Findra.Pipe;

/// <summary>
/// The helper's fan-out: one journal tail on one side, every subscribed session on the other.
///
/// One plain lock guards the whole thing, and <see cref="Publish"/> takes it too. That means a
/// publish parks for the length of one backlog read, which is a real cost and an acceptable one:
/// the tail's own NextUsn is untouched while it waits, so nothing is lost - it simply catches up
/// on its next pass. Nobody should "optimise" that lock away.
/// </summary>
public sealed class JournalBroadcast
{
    private readonly object _lock = new();
    private readonly Dictionary<long, Action<JournalEvent>> _sinks = [];
    private long _next;

    public int SubscriberCount { get { lock (_lock) return _sinks.Count; } }

    /// <summary>
    /// Hand one event to every registered sink. Synchronous and non-blocking by contract - see
    /// <see cref="SubscribeWithBacklog"/> for what a sink is allowed to do.
    ///
    /// One dead session must never stop the others hearing the journal, so each sink is guarded
    /// on its own. The failure is logged once: a sink that throws will throw for every event,
    /// and a per-event log line would bury the log under a fault it already recorded.
    /// </summary>
    public void Publish(JournalEvent e)
    {
        lock (_lock)
            foreach (Action<JournalEvent> sink in _sinks.Values)
                Deliver(sink, e);
    }

    /// <summary>
    /// Registration is inseparable from the backlog that must precede it. <paramref name="backlog"/>
    /// runs INSIDE the lock and everything it returns is handed to <paramref name="sink"/> before
    /// the sink can see a single live event, so there is no window in which an event reaches
    /// neither the backlog nor the sink, and no way for a live event to be delivered ahead of an
    /// older replayed one.
    ///
    /// Splitting this into "read the gap, then Subscribe" is wrong in both orders. Register second
    /// and an event published in between is lost - the smaller version of the very gap this
    /// machinery exists to close. Register first and enqueue the gap afterwards and the live event
    /// goes out ahead of older records: a create-then-delete replayed behind a newer create of the
    /// same FRN makes the feeder delete a file that exists, and the queue's upsert on (volume, frn)
    /// does not save it, because last write wins and the last write is the older one.
    ///
    /// A sink MUST NOT block, await or throw. The only sink in the tree is a TryWrite onto a
    /// DropOldest bounded channel, which does none of the three - which is why this takes an
    /// Action and why Publish returns void.
    /// </summary>
    public IDisposable SubscribeWithBacklog(Func<IReadOnlyList<JournalEvent>> backlog,
                                            Action<JournalEvent> sink)
    {
        lock (_lock)
        {
            foreach (JournalEvent e in backlog())
                Deliver(sink, e);

            long id = _next++;
            _sinks[id] = sink;
            return new Registration(this, id);
        }
    }

    private static void Deliver(Action<JournalEvent> sink, JournalEvent e)
    {
        try { sink(e); }
        catch (Exception ex)
        {
            Log.Once("journal-sink-threw", "WARN ", "journal",
                     $"a journal subscriber's sink threw and its events are being dropped: " +
                     $"{ex.GetType().Name}: {ex.Message}");
        }
    }

    private void Remove(long id)
    {
        lock (_lock) _sinks.Remove(id);
    }

    private sealed class Registration(JournalBroadcast bus, long id) : IDisposable
    {
        private bool _gone;

        public void Dispose()
        {
            if (_gone) return;
            _gone = true;
            bus.Remove(id);
        }
    }
}
