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

    // Enumerations are matched BY ID, not positionally like status and subscribe. One request
    // produces many frames, and a positional queue cannot express that: the head waiter would take
    // the first frame and everything after it would land on whoever asked next.
    private readonly ConcurrentDictionary<long, Channel<EnumerateReply>> _enumerations = new();
    private long _enumerateId;

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
    /// Every live file on one volume whose name ends in one of these suffixes.
    ///
    /// The suffix list is the CALLER'S and travels on the wire: the helper compares names against
    /// it and decides nothing else. Everything about whether a file is worth opening is settled
    /// here, at normal integrity, on the rows that come back.
    ///
    /// The id is a plain counter and deliberately NOT the generation. A first pass is not an
    /// answer to a query, so stamping it would discard the next real search reply as stale and
    /// blank the card in the middle of the walk.
    ///
    /// Throws if the helper goes away mid-walk rather than ending quietly. A caller that took a
    /// truncated stream for a finished one would stamp a consumed position and clear the walk
    /// debt over a disk it never finished reading, and nothing afterwards would ever notice.
    ///
    /// A list longer than one request can carry is SPLIT across requests, never trimmed. The
    /// helper clamps the frame at <see cref="NameServer.MaxSuffixes"/> to bound a per-record
    /// inner loop it did not choose, and it drops the tail to do it - so a caller whose list
    /// outgrew the clamp would lose whichever extensions sort last, on every machine, with no
    /// error anywhere. Splitting costs one extra pass over the volume per extra chunk, which is
    /// the honest price and is visible in the log rather than in a hole in the index.
    /// </summary>
    public async IAsyncEnumerable<EnumeratedFile> EnumerateAsync(
        char volume, IReadOnlyList<string> suffixes, int batchSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        if (suffixes.Count <= NameServer.MaxSuffixes)
        {
            await foreach (EnumeratedFile f in OneEnumerationAsync(volume, suffixes, batchSize, ct)
                               .ConfigureAwait(false))
                yield return f;
            yield break;
        }

        int chunks = (suffixes.Count + NameServer.MaxSuffixes - 1) / NameServer.MaxSuffixes;
        Log.Warn("pipe", string.Create(CultureInfo.InvariantCulture,
            $"{char.ToUpperInvariant(volume)}: {suffixes.Count} suffixes is more than one enumerate " +
            $"request carries ({NameServer.MaxSuffixes}), so the volume is walked {chunks} times"));

        // One file can end in two of the caller's suffixes at once - ".gz" and ".tar.gz" - so the
        // chunks are not disjoint over files even though they are disjoint over suffixes. The
        // caller is handed each file once whatever it named.
        var seen = new HashSet<ulong>();
        for (int i = 0; i < suffixes.Count; i += NameServer.MaxSuffixes)
        {
            var chunk = new List<string>(NameServer.MaxSuffixes);
            for (int j = i; j < suffixes.Count && j < i + NameServer.MaxSuffixes; j++) chunk.Add(suffixes[j]);

            await foreach (EnumeratedFile f in OneEnumerationAsync(volume, chunk, batchSize, ct)
                               .ConfigureAwait(false))
                if (seen.Add(f.Frn)) yield return f;
        }
    }

    /// <summary>One enumerate request and the whole of its reply stream.</summary>
    private async IAsyncEnumerable<EnumeratedFile> OneEnumerationAsync(
        char volume, IReadOnlyList<string> suffixes, int batchSize,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        ThrowIfPumpGone();

        long id = Interlocked.Increment(ref _enumerateId);
        var frames = Channel.CreateUnbounded<EnumerateReply>(
            new UnboundedChannelOptions { SingleWriter = true, SingleReader = true });

        // Registered BEFORE the write, or a helper that answers faster than this thread resumes
        // has its first frame routed to nobody.
        _enumerations[id] = frames;
        try
        {
            await _writeLock.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await Frame.WriteAsync(_transport, Envelope.Pack(Envelope.KindEnumerate,
                    new EnumerateRequest(id, volume, suffixes, batchSize)), ct).ConfigureAwait(false);
            }
            finally { _writeLock.Release(); }

            // Same race as every other call: the pump may have died between the entry check and
            // the registration. The pump's drain completes this channel, so either it finds this
            // entry or this sees the flag.
            ThrowIfPumpGone();

            await foreach (EnumerateReply r in frames.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            {
                foreach (EnumeratedFile f in r.Files) yield return f;
                if (r.Done) yield break;
            }

            throw new IOException(
                "the name helper stopped answering before the enumeration of " +
                char.ToUpperInvariant(volume) + ": finished");
        }
        finally { _enumerations.TryRemove(id, out _); }
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
                        case Envelope.KindEnumerateReply:
                        {
                            // By id: many frames answer one request. An unbounded channel because
                            // the frames are already bounded by the helper's batch size and the
                            // consumer is the walk itself - dropping one here would silently
                            // shorten a first pass.
                            EnumerateReply r = e.Body<EnumerateReply>();
                            if (_enumerations.TryGetValue(r.Id, out var into)) into.Writer.TryWrite(r);
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

            // Completed, never faulted: a walk in flight sees its channel end without a Done
            // frame and throws its own IOException, which says what was actually lost.
            foreach (var kv in _enumerations) kv.Value.Writer.TryComplete();

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
