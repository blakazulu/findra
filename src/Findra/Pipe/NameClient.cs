using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Pipes;
using System.Threading.Channels;

namespace Findra.Pipe;

/// <summary>
/// The normal-integrity half. Every call is async - name search is a round trip,
/// never an in-RAM lookup, and pretending otherwise deadlocks the UI thread.
/// </summary>
public sealed class NameClient : IAsyncDisposable
{
    private readonly Stream _transport;
    private readonly Generation _gen = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<QueryReply>> _pending = new();
    private readonly ConcurrentQueue<TaskCompletionSource<StatusReply>> _statusWaiters = new();
    private readonly ConcurrentQueue<TaskCompletionSource<SubscribeReply>> _subscribeWaiters = new();
    private readonly Channel<JournalEvent> _journal;
    private readonly CancellationTokenSource _reader = new();
    private readonly Task _pump;
    private long _journalDropped;
    private volatile bool _pumpGone;
    private bool _disposed;

    public long CurrentGeneration => _gen.Current;

    /// <summary>
    /// How many pushed journal events this client's own channel has evicted because nothing was
    /// draining it. THE CLIENT DROP PATH HAS NO RESET MARKER OF ITS OWN - nothing upstream knows
    /// it happened, and the helper's own count says nothing about it - so this number is the only
    /// trace, and it is the caller's job to watch it. A consumer that sees it move owes itself a
    /// fresh walk of every volume it is tracking (the queue's own list of known volumes, not a
    /// list this type keeps), including any volume whose first full pass is still in flight, or
    /// the index ends up claiming to be complete over a range it never saw.
    /// </summary>
    public long JournalDropped => Interlocked.Read(ref _journalDropped);

    public NameClient(Stream transport)
    {
        _transport = transport;

        // The same bound as the helper's per-session outbound queue, for the same reason: a
        // client channel smaller than one apply slice drops on an ORDINARY catch-up rather than
        // only under abuse. DropOldest because a UI that stops draining must cost stale events,
        // not a stalled pipe - a blocked pump also stops answering queries, so back-pressure
        // here would freeze the card.
        //
        // The itemDropped overload, not `if (!TryWrite) ...`: with DropOldest, TryWrite ALWAYS
        // returns true and the eviction is silent, so this callback is the only way the count
        // is ever non-zero.
        _journal = Channel.CreateBounded<JournalEvent>(
            new BoundedChannelOptions(NameServer.MaxOutbound)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleWriter = true,        // the pump, and nothing else
                SingleReader = false,
            },
            _ =>
            {
                Interlocked.Increment(ref _journalDropped);
                Log.Once("journal-client-drop", "WARN ", "pipe",
                    "nothing is draining the journal channel and events are being dropped (bound " +
                    NameServer.MaxOutbound.ToString(CultureInfo.InvariantCulture) +
                    ") - a fresh walk is owed");
            });

        _pump = Task.Run(() => PumpAsync(_reader.Token));
    }

    public static async Task<NameClient> ConnectAsync(TimeSpan timeout, CancellationToken ct)
    {
        // CurrentUserOnly is the client half of the squatting defence. The server sets
        // FirstPipeInstance so nobody can take the name first; this makes the client verify
        // the server is running as the same user before it trusts a single path it returns.
        // Without it, a pipe from another account could feed fabricated results into the
        // card, and a click would launch them as this user.
        var pipe = new NamedPipeClientStream(".", NameServer.PipeName,
            PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync((int)timeout.TotalMilliseconds, ct).ConfigureAwait(false);
        return new NameClient(pipe);
    }

    /// <summary>Null means the answer arrived after a newer query had been issued.</summary>
    public async Task<QueryReply?> SearchAsync(string raw, int max, CancellationToken ct)
    {
        ThrowIfPumpGone();

        long gen;
        TaskCompletionSource<QueryReply> tcs;

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Stamp, register and write as one unit under the lock. Stamping outside it
            // bumps the generation for a query that may never reach the wire, and Accept
            // would then reject the genuinely-newest reply that did.
            gen = _gen.Next();
            tcs = new TaskCompletionSource<QueryReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pending[gen] = tcs;
            try
            {
                await Frame.WriteAsync(_transport, Envelope.Pack(Envelope.KindQuery,
                    new QueryRequest(gen, raw, max)), ct).ConfigureAwait(false);
            }
            catch { _pending.TryRemove(gen, out _); throw; }   // never reached the wire; do not leak it
        }
        finally { _writeLock.Release(); }

        // The pump may have died between the check above and the registration. Whichever
        // side loses that race, one of them sees the other: the pump's drain either finds
        // this entry, or this re-check finds the flag.
        if (_pumpGone) { _pending.TryRemove(gen, out _); ThrowIfPumpGone(); }

        QueryReply reply;
        try { reply = await tcs.Task.WaitAsync(ct).ConfigureAwait(false); }
        catch { _pending.TryRemove(gen, out _); throw; }

        return _gen.Accept(reply.Gen) ? reply : null;
    }

    public async Task<StatusReply> StatusAsync(CancellationToken ct)
    {
        ThrowIfPumpGone();

        var tcs = new TaskCompletionSource<StatusReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Status replies carry no id, so waiters are matched positionally. Enqueue
            // inside the lock: enqueuing before it lets two concurrent callers queue in one
            // order and write in the other, and each then receives the other's reply.
            _statusWaiters.Enqueue(tcs);
            try
            {
                await Frame.WriteAsync(_transport, Envelope.Pack(Envelope.KindStatus, new StatusRequest()), ct)
                    .ConfigureAwait(false);
            }
            catch
            {
                // A stranded waiter at the head of a positional queue desynchronises every
                // later status call permanently, so mark it dead rather than leaving it.
                // The pump skips dead entries when it dequeues; that is the other half.
                tcs.TrySetCanceled();
                throw;
            }
        }
        finally { _writeLock.Release(); }

        // Same race as SearchAsync: the pump may have died between the entry check and the
        // enqueue. Marking the waiter dead is enough - the pump's skip-on-dequeue discards it.
        if (_pumpGone) { tcs.TrySetCanceled(); ThrowIfPumpGone(); }

        return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Ask the helper to push journal events, resuming from the positions this caller stored.
    /// The reply says where each volume actually resumes, which is not always where it asked.
    ///
    /// A subscription is a registration, not a question: it does NOT touch the generation
    /// counter. Stamping it would make the next real query reply look stale and blank the card
    /// on a keystroke.
    /// </summary>
    public async Task<SubscribeReply> SubscribeJournalAsync(IReadOnlyList<VolumeCursor> from,
                                                            CancellationToken ct)
    {
        ThrowIfPumpGone();

        var tcs = new TaskCompletionSource<SubscribeReply>(TaskCreationOptions.RunContinuationsAsynchronously);

        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Subscribe replies carry no id, so waiters are matched positionally, exactly as
            // status replies are. Enqueue inside the lock: enqueuing before it lets two
            // concurrent callers queue in one order and write in the other, and each then
            // receives the other's reply.
            _subscribeWaiters.Enqueue(tcs);
            try
            {
                await Frame.WriteAsync(_transport, Envelope.Pack(Envelope.KindSubscribe,
                    new SubscribeRequest(from)), ct).ConfigureAwait(false);
            }
            catch
            {
                // A stranded waiter at the head of a positional queue desynchronises every later
                // call permanently, so mark it dead rather than leaving it. The pump skips dead
                // entries when it dequeues; that is the other half.
                tcs.TrySetCanceled();
                throw;
            }
        }
        finally { _writeLock.Release(); }

        if (_pumpGone) { tcs.TrySetCanceled(); ThrowIfPumpGone(); }

        return await tcs.Task.WaitAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Every journal event the helper pushes, in the order it pushed them, until the helper goes
    /// away. Ends rather than hanging when the pump stops, because the channel is completed in
    /// the pump's finally.
    /// </summary>
    public IAsyncEnumerable<JournalEvent> JournalAsync(CancellationToken ct)
        => _journal.Reader.ReadAllAsync(ct);

    private void ThrowIfPumpGone()
    {
        if (_pumpGone)
            throw new IOException("the name helper connection is closed");
    }

    private async Task PumpAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[]? payload = await Frame.ReadAsync(_transport, ct).ConfigureAwait(false);
                if (payload is null) break;

                // Guard decoding per frame. One undecodable reply must not end the pump:
                // the transport stays writable, so a dead reader turns every later search
                // into an await nobody will ever complete. The server guards its own
                // decode for the same reason - this is the matching half.
                Envelope e;
                try { e = Envelope.Unpack(payload); }
                catch (Exception ex) { Log.Warn("pipe", "undecodable frame from the helper: " + ex.Message); continue; }

                try
                {
                    switch (e.Kind)
                    {
                        case Envelope.KindQueryReply:
                        {
                            QueryReply r = e.Body<QueryReply>();
                            if (_pending.TryRemove(r.Gen, out var waiter)) waiter.TrySetResult(r);
                            break;
                        }
                        case Envelope.KindStatusReply:
                        {
                            // Status replies carry no id, so waiters match positionally -
                            // which means a dead entry at the head would swallow this reply
                            // and starve whoever queued behind it. TrySetResult returns
                            // false for an already-completed waiter, so walk past those.
                            StatusReply s = e.Body<StatusReply>();
                            while (_statusWaiters.TryDequeue(out var waiting))
                                if (waiting.TrySetResult(s)) break;
                            break;
                        }
                        case Envelope.KindSubscribeReply:
                        {
                            // Positional, and skip-on-dequeue, for the same reason as status.
                            SubscribeReply s = e.Body<SubscribeReply>();
                            while (_subscribeWaiters.TryDequeue(out var waiting))
                                if (waiting.TrySetResult(s)) break;
                            break;
                        }
                        case Envelope.KindJournal:
                            // Never blocks the pump. A bounded DropOldest channel takes this
                            // synchronously whether or not anyone is reading; what it evicts is
                            // counted on JournalDropped, which is the caller's only warning.
                            _journal.Writer.TryWrite(e.Body<JournalEvent>());
                            break;
                        default:
                            Log.Info("pipe", $"client ignoring unknown kind '{e.Kind}'");
                            break;
                    }
                }
                catch (Exception ex)
                {
                    // Not just JsonException. Body<T> throws InvalidDataException for a body
                    // that decodes to null - the literal `null` in the Json field - and a
                    // JsonException-only guard lets that one shape through to kill the pump
                    // permanently, which is the failure this catch exists to prevent.
                    Log.Warn("pipe", $"undecodable body for '{e.Kind}': {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log.Error("pipe", "client pump ended", ex); }
        finally
        {
            // Set the flag BEFORE draining. A caller registering concurrently either has
            // its entry found by the drain below, or sees this flag on its own re-check -
            // one of the two always happens, so nobody is left awaiting a dead pump.
            _pumpGone = true;
            foreach (var kv in _pending) kv.Value.TrySetCanceled();
            while (_statusWaiters.TryDequeue(out var s)) s.TrySetCanceled();
            while (_subscribeWaiters.TryDequeue(out var sub)) sub.TrySetCanceled();

            // Completed here beside the other drains, so a consumer awaiting JournalAsync ends
            // rather than hanging when the helper goes away.
            _journal.Writer.TryComplete();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        await _reader.CancelAsync().ConfigureAwait(false);
        try { await _pump.ConfigureAwait(false); } catch { }
        _transport.Dispose();
        _reader.Dispose();

        // _writeLock is deliberately NOT disposed. SemaphoreSlim.Dispose does not complete
        // queued async waiters - it neither resumes nor faults them - so disposing it while
        // a caller is parked on WaitAsync hangs that caller silently, and the Release in
        // its finally then throws ObjectDisposedException out of a finally block, masking
        // whatever it was really failing on. Nothing here allocates its wait handle, so
        // there is nothing to release.
    }
}
